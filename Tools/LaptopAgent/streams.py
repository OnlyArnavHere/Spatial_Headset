"""What the headset can stream: the whole desktop (and any other screen) and each open window, every one on its own
MediaMTX path named after its title (e.g. "untitled-notepad").

A stream starts when a headset opens it on the control connection and stops a little while after the last connection
using it closes, so nothing is captured that no one is watching. Streams started from the command line (--window,
--monitor) are pinned: they run until the agent stops.
"""
import logging
import re
import subprocess
import time
from dataclasses import dataclass
from typing import Callable

import winapi

log = logging.getLogger("streams")

DESKTOP_ID = "desktop"
STOP_AFTER_IDLE_S = 15.0
# Smaller windows are popups, tooltips and badges (e.g. WinUI's "PopupHost" key tips), not something to watch, and
# too small for hardware encoders (NVENC refuses frames below about 145x49).
MIN_WINDOW_SIZE = (160, 90)


@dataclass
class Target:
    """Something that can be streamed; its rectangle maps normalized pointer positions to screen pixels."""
    id: str                        # "desktop", "monitor:N" or "window:HWND"
    kind: str                      # "window" or "monitor"
    handle: int
    title: str
    description: str
    selector: str                  # ffmpeg gfxcapture selector: an exact HWND/HMONITOR, so input maps to the same thing
    rect: Callable[[], "winapi.Rect | None"]
    usable: Callable[[], bool]

    def describe(self) -> dict:
        r = self.rect()
        return {"kind": self.kind, "description": self.description,
                "width": r.width if r else 0, "height": r.height if r else 0}


def window_target(window: winapi.Window) -> Target:
    handle = window.handle
    return Target(f"window:{handle}", "window", handle, window.title, f"window {window.title!r}", f"hwnd={handle}",
                  rect=lambda: winapi.window_rect(handle),
                  usable=lambda: winapi.window_exists(handle) and not winapi.window_is_minimized(handle))


def monitor_target(monitor: winapi.Monitor) -> Target:
    handle = monitor.handle
    target_id, title = (DESKTOP_ID, "Whole desktop") if monitor.primary else (f"monitor:{monitor.index}", f"Screen {monitor.index + 1}")
    return Target(target_id, "monitor", handle, title, f"monitor {monitor.index} ({monitor.rect})", f"hmonitor={handle}",
                  rect=lambda: winapi.monitor_rect(handle), usable=lambda: True)


def slug(title: str) -> str:
    """A MediaMTX path from a window title: lowercase letters, digits and dashes."""
    text = re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-")[:40].strip("-")
    return text or "window"


@dataclass
class Stream:
    path: str
    target: Target
    size: tuple[int, int]
    process: subprocess.Popen
    users: int = 0
    pinned: bool = False
    idle_since: float = 0.0

    @property
    def running(self) -> bool:
        return self.process.poll() is None

    def stop(self) -> None:
        if self.running:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()


class StreamManager:
    """Starts and stops one capture process (ffmpeg) per streamed target.

    launch(target, path) starts the capture for a target on a path and returns (process, output size).
    on_ended(stream) is called when a capture stops on its own (window closed, ffmpeg failed).
    """

    def __init__(self, launch: Callable[[Target, str], tuple[subprocess.Popen, tuple[int, int]]],
                 only_pinned: bool = False):
        self._launch = launch
        self._only_pinned = only_pinned
        self._paths: dict[str, str] = {}        # target id -> path, stable for this run
        self._streams: dict[str, Stream] = {}   # target id -> running stream
        self._pinned: set[str] = set()
        self.on_ended: Callable[[Stream], None] = lambda stream: None

    def available(self) -> list[Target]:
        """The whole desktop and other screens first, then the windows a user would recognise (minimized ones can't
        be captured and tiny popups aren't worth it, so they're left out). With only_pinned, just the targets given
        on the command line."""
        monitors = sorted(winapi.list_monitors(), key=lambda m: (not m.primary, m.index))
        targets = [monitor_target(m) for m in monitors]
        targets += [window_target(w) for w in winapi.list_windows() if self._listable(w.handle)]
        if self._only_pinned:
            targets = [t for t in targets if t.id in self._pinned]
        return targets

    @staticmethod
    def _listable(handle: int) -> bool:
        if winapi.window_is_minimized(handle):
            return False
        rect = winapi.window_rect(handle)
        return rect is not None and rect.width >= MIN_WINDOW_SIZE[0] and rect.height >= MIN_WINDOW_SIZE[1]

    def listing(self) -> list[dict]:
        """The targets as the headset's picker shows them, with the path each one streams on."""
        return [{"id": t.id, "path": self.path_for(t), "title": t.title, **t.describe()} for t in self.available()]

    def path_for(self, target: Target) -> str:
        path = self._paths.get(target.id)
        if path is None:
            base = DESKTOP_ID if target.id == DESKTOP_ID else slug(target.title)
            taken = set(self._paths.values()) | ({DESKTOP_ID} if target.id != DESKTOP_ID else set())
            path, n = base, 2
            while path in taken:
                path, n = f"{base}-{n}", n + 1
            self._paths[target.id] = path
        return path

    def acquire(self, target_id: str) -> Stream:
        """Starts the target's stream unless it's running, and counts one more user.
        Raises LookupError("gone") for a closed (or unknown) window, LookupError("minimized") for a minimized one."""
        stream = self._streams.get(target_id)
        if stream is None or not stream.running:
            target = next((t for t in self.available() if t.id == target_id), None)
            if target is None:
                minimized = target_id.startswith("window:") and winapi.window_exists(int(target_id[7:])) \
                    and winapi.window_is_minimized(int(target_id[7:]))
                raise LookupError("minimized" if minimized else "gone")
            path = self.path_for(target)
            process, size = self._launch(target, path)
            stream = Stream(path, target, size, process, pinned=target_id in self._pinned)
            self._streams[target_id] = stream
            log.info("Streaming %s on /%s (%dx%d)", target.description, path, *size)
        stream.users += 1
        return stream

    def pin(self, target_id: str) -> Stream:
        """Starts a stream that runs until the agent stops (a target given on the command line)."""
        self._pinned.add(target_id)
        stream = self.acquire(target_id)
        stream.pinned = True
        stream.users -= 1
        return stream

    def release(self, stream: Stream) -> None:
        stream.users = max(0, stream.users - 1)
        if stream.users == 0:
            stream.idle_since = time.monotonic()

    def tick(self) -> None:
        """Call about twice a second: keeps static windows streaming, stops streams nobody has used for a while and
        reports ones that ended on their own."""
        now = time.monotonic()
        for target_id, stream in list(self._streams.items()):
            if stream.running and stream.target.kind == "window":
                winapi.request_repaint(stream.target.handle)
            if not stream.running:
                log.warning("Stream /%s stopped (exit code %s): %s closed or no longer capturable?",
                            stream.path, stream.process.returncode, stream.target.description)
                del self._streams[target_id]
                self.on_ended(stream)
            elif stream.users == 0 and not stream.pinned and now - stream.idle_since > STOP_AFTER_IDLE_S:
                log.info("Stopping /%s: no headset has used it for %g s", stream.path, STOP_AFTER_IDLE_S)
                stream.stop()
                del self._streams[target_id]

    def running(self) -> list[Stream]:
        return [s for s in self._streams.values() if s.running]

    def stop_all(self) -> None:
        for stream in self._streams.values():
            stream.stop()
        self._streams.clear()
