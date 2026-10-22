"""Plain data shared by every backend, and the names every backend must expose."""
from dataclasses import dataclass


@dataclass(frozen=True)
class Rect:
    left: int
    top: int
    right: int
    bottom: int

    @property
    def width(self) -> int:
        return self.right - self.left

    @property
    def height(self) -> int:
        return self.bottom - self.top

    def __str__(self) -> str:
        return f"{self.width}x{self.height} at ({self.left}, {self.top})"


@dataclass(frozen=True)
class Monitor:
    index: int
    handle: int
    rect: Rect
    primary: bool


@dataclass(frozen=True)
class Window:
    handle: int
    title: str


FUNCTIONS = (
    "make_dpi_aware", "list_monitors", "monitor_rect", "list_windows", "find_window", "window_exists",
    "window_is_minimized", "window_rect", "raise_window", "window_at", "focus_window", "foreground_window",
    "request_repaint", "mouse_move", "mouse_button", "mouse_wheel", "key", "type_text", "watch_kill_switch",
    "open_prompt", "protect", "unprotect", "default_home", "capture_input_args", "encoder_args",
    "preflight", "check_key_file", "window_selector", "monitor_selector", "visible_rect",
)
CONSTANTS = ("KILL_SWITCH", "ENCODERS", "exe_suffix", "ChildReaper")
