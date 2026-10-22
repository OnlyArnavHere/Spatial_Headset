"""Plain data the laptop-shell backends hand to shell.py. Backend-private fields (where an app lives, how it is
started) never leave the agent: the headset only ever sees the opaque ids shell.py assigns."""
from dataclasses import dataclass, field


@dataclass(frozen=True)
class AppEntry:
    key: str                     # stable within a run: "lnk:<path>", "uwp:<AUMID>", "bundle:<path>", "running:<exe>"
    name: str
    program: str | None = None   # what running windows are matched on: a normalized exe path, or an .app bundle path
    launch: tuple | None = None  # backend-private launch spec; None = can't be launched (seen running only)
    icon: tuple | None = None    # backend-private icon source


@dataclass(frozen=True)
class WindowEntry:
    handle: int
    title: str
    program: str | None          # same normalization as AppEntry.program
    app_name: str                # the program's display name, for windows no listed app claims
    minimized: bool = False
    focused: bool = False
    started: float = 0.0         # when the window's process started (epoch seconds), for taskbar/Dock-like order
    pid: int = 0
    icon: tuple | None = field(default=None, compare=False)


class ShellBackend:
    """What a platform implements. Every method may block (they run on a worker thread) and may raise OSError."""
    os_name = "unknown"
    supported = False

    def installed(self) -> list[AppEntry]:
        return []

    def pinned(self) -> list[AppEntry]:
        return []

    def windows(self) -> list[WindowEntry]:
        return []

    def app_icon(self, app: AppEntry, size: int) -> bytes | None:
        return None

    def window_icon(self, window: WindowEntry, size: int) -> bytes | None:
        return None

    def launch(self, app: AppEntry) -> None:
        raise OSError("launching apps isn't supported on this laptop")

    def window_action(self, handle: int, action: str) -> bool:
        return False


class UnsupportedShell(ShellBackend):
    """Linux (report only for now) and anything else: every shell request is answered "unsupported"."""

    def __init__(self, os_name: str) -> None:
        self.os_name = os_name
