"""Where the agent keeps its files and how it starts its helper processes, whether run from source or frozen
(PyInstaller). Frozen, the install folder is read-only, so tools, runtime files and logs live in the per-user data
folder and helpers are the same executable with a `--_<name>` flag (dispatched in app.py)."""
import logging
import logging.handlers
import subprocess
import sys
from pathlib import Path

import identity

HERE = Path(__file__).resolve().parent
LOG_BYTES = 1_000_000
LOG_BACKUPS = 3


def frozen() -> bool:
    return bool(getattr(sys, "frozen", False))


def data_dir() -> Path:
    return identity.default_home()


def tools_dir() -> Path:
    return data_dir() / "bin" if frozen() else HERE / "bin"


def runtime_dir() -> Path:
    return data_dir() / "runtime" if frozen() else HERE / ".runtime"


def logs_dir() -> Path:
    return data_dir() / "logs"


def no_window(kwargs: dict) -> dict:
    """Returns kwargs with CREATE_NO_WINDOW added to creationflags on Windows (a windowed app has no console for its
    children to flash); other platforms get them unchanged."""
    out = dict(kwargs)
    if sys.platform == "win32":
        out["creationflags"] = out.get("creationflags", 0) | getattr(subprocess, "CREATE_NO_WINDOW", 0x08000000)
    return out


def helper_argv(name: str, script: Path | str) -> list[str]:
    """The command line that runs a helper: the frozen executable with --_<name>, or `python -I script`."""
    if frozen():
        return [sys.executable, "--_" + name]
    return [sys.executable, "-I", str(script)]


def setup_logging(gui_handler: logging.Handler | None = None, level: int = logging.INFO) -> Path:
    """Logs to logs_dir()/agent.log (rotating). A console handler is added only when there is a stderr (a windowed
    frozen app has none). Returns the log file path."""
    path = logs_dir() / "agent.log"
    path.parent.mkdir(parents=True, exist_ok=True)
    fmt = logging.Formatter("%(asctime)s %(name)s: %(message)s", datefmt="%H:%M:%S")
    root = logging.getLogger()
    root.setLevel(level)
    handlers: list[logging.Handler] = [logging.handlers.RotatingFileHandler(
        path, maxBytes=LOG_BYTES, backupCount=LOG_BACKUPS, encoding="utf-8")]
    if sys.stderr is not None:
        handlers.append(logging.StreamHandler())
    if gui_handler is not None:
        handlers.append(gui_handler)
    for handler in handlers:
        handler.setFormatter(fmt)
        root.addHandler(handler)
    return path
