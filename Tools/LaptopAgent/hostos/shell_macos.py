"""The laptop shell on macOS: the Dock's running and pinned apps, the apps in /Applications, ~/Applications and
/System/Applications, their icons, launching them, and raising, minimizing, restoring or closing windows through
Accessibility (which --input already needs). pyobjc is imported inside the functions; the logic that needs none is in
_shell_macos_pure.py.

Launching only ever opens an .app bundle this module found itself (a Dock pin or one of the Applications folders),
re-checked against a fresh listing at launch time, with /usr/bin/open and the path as one argument.
"""
import logging
import os
import plistlib
import posixpath
import subprocess
import threading
import time

from . import _macos_input as inp
from . import _shell_macos_pure as pure
from ._shell_types import AppEntry, ShellBackend, WindowEntry

log = logging.getLogger("shell")

INSTALLED_TTL_S = 120.0
PINNED_TTL_S = 15.0
DOCK_PLIST = "~/Library/Preferences/com.apple.dock.plist"


def app_roots() -> list[str]:
    return ["/Applications", os.path.expanduser("~/Applications"), "/System/Applications"]


def _running_apps() -> dict:
    from AppKit import NSApplicationActivationPolicyRegular, NSWorkspace
    apps = {}
    for app in NSWorkspace.sharedWorkspace().runningApplications():
        url = app.bundleURL()
        launched = app.launchDate()
        apps[int(app.processIdentifier())] = pure.RunningApp(
            int(app.processIdentifier()), str(app.localizedName() or ""), str(url.path()) if url else None,
            float(launched.timeIntervalSince1970()) if launched else 0.0,
            app.activationPolicy() == NSApplicationActivationPolicyRegular)
    return apps


def _app_for_bundle_id(bundle_id: str) -> str | None:
    """Where Launch Services has the app with this bundle identifier, or None."""
    from AppKit import NSWorkspace
    try:
        url = NSWorkspace.sharedWorkspace().URLForApplicationWithBundleIdentifier_(bundle_id)
    except Exception:  # noqa: BLE001  (a bad identifier must not break the pin list)
        return None
    return str(url.path()) if url is not None and url.path() else None


def _frontmost_window(onscreen) -> int:
    from AppKit import NSWorkspace
    app = NSWorkspace.sharedWorkspace().frontmostApplication()
    return inp.frontmost_window(int(app.processIdentifier()), onscreen) if app else 0


def bundle_icon(path: str, size: int) -> bytes | None:
    """The app's icon drawn into a size x size bitmap, as PNG."""
    from AppKit import (NSBitmapImageFileTypePNG, NSBitmapImageRep, NSCompositingOperationCopy, NSDeviceRGBColorSpace,
                        NSGraphicsContext, NSMakeRect, NSWorkspace, NSZeroRect)
    image = NSWorkspace.sharedWorkspace().iconForFile_(path)
    if image is None:
        return None
    rep = NSBitmapImageRep.alloc().initWithBitmapDataPlanes_pixelsWide_pixelsHigh_bitsPerSample_samplesPerPixel_hasAlpha_isPlanar_colorSpaceName_bytesPerRow_bitsPerPixel_(
        None, size, size, 8, 4, True, False, NSDeviceRGBColorSpace, 0, 0)
    if rep is None:
        return None
    NSGraphicsContext.saveGraphicsState()
    try:
        NSGraphicsContext.setCurrentContext_(NSGraphicsContext.graphicsContextWithBitmapImageRep_(rep))
        image.drawInRect_fromRect_operation_fraction_(NSMakeRect(0, 0, size, size), NSZeroRect,
                                                      NSCompositingOperationCopy, 1.0)
    finally:
        NSGraphicsContext.restoreGraphicsState()
    data = rep.representationUsingType_properties_(NSBitmapImageFileTypePNG, {})
    return bytes(data) if data is not None else None


class MacShell(ShellBackend):
    os_name = "macos"
    supported = True

    def __init__(self) -> None:
        import AppKit  # noqa: F401  (ImportError here makes shell.py fall back to "unsupported")
        self._lock = threading.Lock()
        self._installed: tuple[float, list[AppEntry]] | None = None
        self._pinned: tuple[float, list[AppEntry]] | None = None
        self._own_pid = os.getpid()

    # Listing

    def installed(self) -> list[AppEntry]:
        with self._lock:
            if self._installed and time.monotonic() - self._installed[0] < INSTALLED_TTL_S:
                return self._installed[1]
        paths = pure.scan_bundles(app_roots(), os.listdir, os.path.isdir)
        result, seen = [], set()
        for path in paths:
            entry = pure.bundle_entry(path)
            if entry is not None and entry.key not in seen:
                seen.add(entry.key)
                result.append(entry)
        with self._lock:
            self._installed = (time.monotonic(), result)
        return result

    def pinned(self) -> list[AppEntry]:
        with self._lock:
            if self._pinned and time.monotonic() - self._pinned[0] < PINNED_TTL_S:
                return self._pinned[1]
        try:
            with open(os.path.expanduser(DOCK_PLIST), "rb") as f:
                doc = plistlib.load(f)
        except (OSError, plistlib.InvalidFileException, ValueError) as e:
            log.info("Couldn't read the Dock's pins (%s)", e)
            doc = {}
        named = {posixpath.basename(e.launch[1]).casefold(): e.launch[1] for e in reversed(self.installed()) if e.launch}
        paths = pure.dock_pins(doc, os.path.isdir, _app_for_bundle_id, lambda name: named.get(name.casefold()))
        result = [e for e in (pure.bundle_entry(p) for p in paths) if e is not None]
        with self._lock:
            self._pinned = (time.monotonic(), result)
        return result

    def windows(self) -> list[WindowEntry]:
        import Quartz as q
        all_infos = list(q.CGWindowListCopyWindowInfo(q.kCGWindowListOptionAll, q.kCGNullWindowID) or [])
        onscreen = list(q.CGWindowListCopyWindowInfo(
            q.kCGWindowListOptionOnScreenOnly | q.kCGWindowListExcludeDesktopElements, q.kCGNullWindowID) or [])
        onscreen_ids = {int(i["kCGWindowNumber"]) for i in onscreen}
        return pure.shell_windows(all_infos, onscreen_ids, _running_apps(), self._own_pid, _frontmost_window(onscreen))

    # Icons

    def app_icon(self, app: AppEntry, size: int) -> bytes | None:
        return bundle_icon(app.icon[1], size) if app.icon and app.icon[0] == "bundle" else None

    def window_icon(self, window: WindowEntry, size: int) -> bytes | None:
        return bundle_icon(window.icon[1], size) if window.icon and window.icon[1] else None

    # Actions

    def launch(self, app: AppEntry) -> None:
        if not app.launch or app.launch[0] != "bundle":
            raise OSError("not launchable")
        path = posixpath.normpath(app.launch[1])
        with self._lock:
            self._installed = self._pinned = None  # re-check against what is there now
        known = {e.launch[1] for e in self.installed() + self.pinned() if e.launch}
        if path not in known or not path.lower().endswith(".app") or not os.path.isdir(path):
            raise OSError("the app is no longer in the Applications folders or the Dock")
        subprocess.run(["/usr/bin/open", path], check=True, timeout=15, capture_output=True)

    def _ax_window(self, handle: int):
        import ApplicationServices as ax
        import Quartz as q
        all_infos = list(q.CGWindowListCopyWindowInfo(q.kCGWindowListOptionAll, q.kCGNullWindowID) or [])
        info = inp.info_of(handle, all_infos)
        if info is None or int(info.get("kCGWindowOwnerPID", 0)) == self._own_pid:
            return None, None
        root = ax.AXUIElementCreateApplication(int(info["kCGWindowOwnerPID"]))

        def attribute(element, attr):
            err, value = ax.AXUIElementCopyAttributeValue(element, attr, None)
            return value if err == 0 else None

        def unpack(value, kind):
            if value is None:
                return None
            ok, result = ax.AXValueGetValue(value, kind, None)
            return result if ok else None

        candidates = []
        for window in attribute(root, ax.kAXWindowsAttribute) or []:
            position = unpack(attribute(window, ax.kAXPositionAttribute), ax.kAXValueCGPointType)
            size = unpack(attribute(window, ax.kAXSizeAttribute), ax.kAXValueCGSizeType)
            if position is not None and size is not None:
                candidates.append((window, attribute(window, ax.kAXTitleAttribute), (position.x, position.y),
                                   (size.width, size.height)))
        return inp.pick_ax_window(candidates, info.get("kCGWindowName"), info.get("kCGWindowBounds") or {}), attribute

    def window_action(self, handle: int, action: str) -> bool:
        import ApplicationServices as ax
        if action not in ("raise", "minimize", "restore", "close"):
            return False
        if not any(w.handle == handle for w in self.windows()):
            return False  # not a window the shell lists (the agent's own, a palette, gone)
        window, attribute = self._ax_window(handle)
        if window is None:
            log.info("No accessibility window for %d (is Accessibility allowed?)", handle)
            return False
        if action == "minimize":
            return ax.AXUIElementSetAttributeValue(window, ax.kAXMinimizedAttribute, True) == 0
        if action == "close":
            button = attribute(window, ax.kAXCloseButtonAttribute)
            return button is not None and ax.AXUIElementPerformAction(button, ax.kAXPressAction) == 0
        if attribute(window, ax.kAXMinimizedAttribute):
            ax.AXUIElementSetAttributeValue(window, ax.kAXMinimizedAttribute, False)
        if action == "raise":
            from . import macos
            macos.raise_window(handle)
        return True
