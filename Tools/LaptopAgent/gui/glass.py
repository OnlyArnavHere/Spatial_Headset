"""The app window as a glass panel, kept out of the headset's picture.

apply(window) works on the pywebview window once it has its native window (call it off the GUI thread; it waits a
little for that). It returns whether the OS draws real blur behind the window; when it doesn't, the page falls back
to a solid dark panel (the "solid" class), so text stays readable.

- Windows: rounded corners (Windows 11) and a dark frame, but no system blur: the page paints its own frosted glass on an
  opaque window (the "solid" class). Windows' acrylic turned flat light grey whenever the widget wasn't the active
  window, and only works on the whole window rectangle anyway.
- Windows 10 2004 and later: SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE), so screen capture (the agent's own
  Windows Graphics Capture, and any other recorder) leaves the window out: a "Whole desktop" stream shows what is
  behind it. Older Windows falls back to WDA_MONITOR, where it shows as a black rectangle instead.
- macOS: the window is not shared with screen capture (NSWindowSharingNone) and its appearance is dark, over
  pywebview's vibrancy (the system blur). set_shape() masks that blur to the widget's tiles, so the gaps between
  them show the desktop as it is. Untested on a Mac so far.

Windows can't do the per-tile cut: the blur covers the window's rectangle, gaps included (a WebView2 window is only
see-through over a system backdrop; window regions and colour keys turn it opaque).
"""
import logging
import sys
import time

log = logging.getLogger("window")

WDA_MONITOR = 0x01
WDA_EXCLUDEFROMCAPTURE = 0x11
DWMWA_USE_IMMERSIVE_DARK_MODE = 20
DWMWA_WINDOW_CORNER_PREFERENCE = 33
DWMWA_SYSTEMBACKDROP_TYPE = 38
DWMWCP_ROUND = 2
DWMSBT_TRANSIENTWINDOW = 3  # acrylic
CORNER_RADIUS = 16.0  # points, macOS


def apply(window, wait_s: float = 10.0) -> bool:
    deadline = time.monotonic() + wait_s
    while getattr(window, "native", None) is None and time.monotonic() < deadline:
        time.sleep(0.05)
    native = getattr(window, "native", None)
    if native is None:
        log.warning("Glass: the window has no native handle; left as it is")
        return False
    try:
        if sys.platform == "win32":
            return _windows(int(native.Handle.ToInt64()), native)
        if sys.platform == "darwin":
            return _macos(native)
    except Exception:
        log.exception("Glass window setup failed")
    return False


def _windows(hwnd: int, form=None) -> bool:
    import ctypes
    user32, dwmapi = ctypes.windll.user32, ctypes.windll.dwmapi
    handle = ctypes.c_void_p(hwnd)

    def dwm(attribute: int, value: int) -> bool:
        v = ctypes.c_int(value)
        return dwmapi.DwmSetWindowAttribute(handle, attribute, ctypes.byref(v), ctypes.sizeof(v)) == 0

    if not user32.SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE):
        if user32.SetWindowDisplayAffinity(handle, WDA_MONITOR):
            log.info("Glass: this Windows can't leave the window out of capture; it shows black in the stream")
        else:
            log.warning("Glass: couldn't keep the window out of screen capture")
    dwm(DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND)
    dwm(DWMWA_USE_IMMERSIVE_DARK_MODE, 1)
    log.info("Glass: frosted panel drawn by the page (opaque window)")
    return False  # the page draws the glass itself


def _macos(ns_window) -> bool:
    from PyObjCTools import AppHelper

    def setup():
        import AppKit
        try:
            ns_window.setSharingType_(AppKit.NSWindowSharingNone)
        except Exception:
            log.exception("Glass: couldn't keep the window out of screen capture")
        try:
            ns_window.setAppearance_(AppKit.NSAppearance.appearanceNamed_(AppKit.NSAppearanceNameVibrantDark))
            content = ns_window.contentView()
            content.setWantsLayer_(True)
            content.layer().setCornerRadius_(CORNER_RADIUS)
            content.layer().setMasksToBounds_(True)
            ns_window.setOpaque_(False)
            ns_window.setBackgroundColor_(AppKit.NSColor.clearColor())
            ns_window.setHasShadow_(True)
            ns_window.invalidateShadow()
        except Exception:
            log.exception("Glass: rounded corners failed")

    AppHelper.callAfter(setup)  # AppKit wants the main thread
    return True


MAX_TILES = 24


def set_shape(window, shape) -> None:
    """shape: [[left, top, width, height, radius], ...] in CSS pixels (points on a Mac). macOS only."""
    if sys.platform != "darwin" or getattr(window, "native", None) is None:
        return
    tiles = []
    for item in list(shape or [])[:MAX_TILES]:
        if isinstance(item, (list, tuple)) and len(item) == 5 and all(isinstance(v, (int, float)) for v in item):
            left, top, width, height, radius = (float(v) for v in item)
            if width > 0 and height > 0:
                tiles.append((left, top, width, height, max(0.0, min(radius, width / 2, height / 2))))
    from PyObjCTools import AppHelper
    ns_window = window.native

    def mask():
        import AppKit
        content = ns_window.contentView()
        effect = _find_effect_view(content, AppKit)
        if effect is None:
            log.info("Glass: no vibrancy view to shape")
            return
        size = content.bounds().size

        def draw(rect):
            AppKit.NSColor.blackColor().setFill()
            for left, top, width, height, radius in tiles:
                AppKit.NSBezierPath.bezierPathWithRoundedRect_xRadius_yRadius_(
                    AppKit.NSMakeRect(left, top, width, height), radius, radius).fill()
            return True

        image = AppKit.NSImage.imageWithSize_flipped_drawingHandler_(size, True, draw)  # flipped: y from the top
        effect.setMaskImage_(image)

    AppHelper.callAfter(mask)


def _find_effect_view(view, AppKit):
    if isinstance(view, AppKit.NSVisualEffectView):
        return view
    for sub in view.subviews() or []:
        found = _find_effect_view(sub, AppKit)
        if found is not None:
            return found
    return None
