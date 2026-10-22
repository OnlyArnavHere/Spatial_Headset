"""Start at login for the frozen app: the HKCU Run value on Windows, a LaunchAgent plist on macOS. From source it is
"unavailable". status() is a (state, message) pair with state "enabled", "disabled" or "unavailable"."""
import os
import plistlib
import sys
from pathlib import Path

import paths

RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
APPROVED_KEY = r"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
VALUE_NAME = "SpatialLauncherAgent"
PLIST_LABEL = "com.spatiallauncher.agent"
ENABLED, DISABLED, UNAVAILABLE = "enabled", "disabled", "unavailable"
TASK_MANAGER_OFF = "turned off in Task Manager"

# Injection points for tests; None means the real thing.
_winreg = None             # a winreg-like module
_launch_agents_dir = None  # Path, default ~/Library/LaunchAgents
_exe = None                # str, default sys.executable
_platform = None           # str, default sys.platform


def _plat() -> str:
    return _platform or sys.platform


def _reg():
    if _winreg is not None:
        return _winreg
    import winreg
    return winreg


def exe_path() -> str:
    return _exe or sys.executable


def command_line(exe: str | None = None) -> str:
    """The Run value: the executable always quoted, so a path with spaces survives."""
    return f'"{exe or exe_path()}" --background'


def _plist_path() -> Path:
    return (_launch_agents_dir or Path.home() / "Library" / "LaunchAgents") / (PLIST_LABEL + ".plist")


def mac_path_problem(exe: str) -> str | None:
    """Why this executable can't be a login item, or None."""
    p = exe.replace("\\", "/")
    if "/AppTranslocation/" in p:
        return "Move the app to Applications first (it is running from a temporary location)."
    home = str(Path.home()).replace("\\", "/").rstrip("/")
    if not (p.startswith("/Applications/") or p.startswith(home + "/Applications/")):
        return "Move the app to Applications to start it at login."
    return None


def _win_approved(reg) -> bool | None:
    """False if the user turned it off in Task Manager (first byte odd, 0x03), True if even, None without an entry."""
    try:
        with reg.OpenKey(reg.HKEY_CURRENT_USER, APPROVED_KEY) as key:
            data, _ = reg.QueryValueEx(key, VALUE_NAME)
    except OSError:
        return None
    if not data:
        return None
    return data[0] % 2 == 0


def _win_has_value(reg) -> bool:
    try:
        with reg.OpenKey(reg.HKEY_CURRENT_USER, RUN_KEY) as key:
            reg.QueryValueEx(key, VALUE_NAME)
        return True
    except OSError:
        return False


def status() -> tuple[str, str]:
    if not paths.frozen():
        return UNAVAILABLE, "Start at login is only available in the installed app."
    plat = _plat()
    if plat == "win32":
        reg = _reg()
        if not _win_has_value(reg):
            return DISABLED, ""
        if _win_approved(reg) is False:
            return DISABLED, TASK_MANAGER_OFF
        return ENABLED, ""
    if plat == "darwin":
        plist = _plist_path()
        if not plist.exists():
            return DISABLED, ""
        try:
            args = plistlib.loads(plist.read_bytes()).get("ProgramArguments") or [""]
        except (OSError, ValueError, plistlib.InvalidFileException):
            return ENABLED, "enabled (the login item file is damaged; turn it off and on again)"
        if args[0] != exe_path():
            return ENABLED, "enabled (points to an old location; turn it off and on again to fix it)"
        return ENABLED, ""
    return UNAVAILABLE, "Start at login isn't supported on this system."


def is_enabled() -> bool:
    return status()[0] == ENABLED


def enable() -> tuple[bool, str]:
    """Turns it on; returns (ok, message). Never overwrites a Task Manager "disabled" choice."""
    state, msg = status()
    if state == UNAVAILABLE:
        return False, msg
    if _plat() == "win32":
        reg = _reg()
        with reg.CreateKey(reg.HKEY_CURRENT_USER, RUN_KEY) as key:
            reg.SetValueEx(key, VALUE_NAME, 0, reg.REG_SZ, command_line())
        if _win_approved(reg) is False:
            return False, TASK_MANAGER_OFF
        return True, ""
    problem = mac_path_problem(exe_path())
    if problem:
        return False, problem
    plist = _plist_path()
    plist.parent.mkdir(parents=True, exist_ok=True)
    tmp = plist.with_name(plist.name + ".tmp")
    try:
        with open(tmp, "wb") as f:
            plistlib.dump({"Label": PLIST_LABEL, "ProgramArguments": [exe_path(), "--background"], "RunAtLoad": True}, f)
        os.replace(tmp, plist)  # always rewritten, so enabling also repairs a stale path
    except BaseException:
        tmp.unlink(missing_ok=True)
        raise
    return True, ""


def disable() -> tuple[bool, str]:
    """Removes the Run value / plist. Leaves StartupApproved alone."""
    state, msg = status()
    if state == UNAVAILABLE:
        return False, msg
    if _plat() == "win32":
        reg = _reg()
        try:
            with reg.OpenKey(reg.HKEY_CURRENT_USER, RUN_KEY, 0, reg.KEY_SET_VALUE) as key:
                reg.DeleteValue(key, VALUE_NAME)
        except OSError:
            pass  # already absent
        return True, ""
    try:
        _plist_path().unlink()
    except FileNotFoundError:
        pass
    return True, ""
