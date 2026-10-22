"""macOS permission rows and one-shot requests for the status window. Pure: the probes and requests are injected, and
pyobjc is imported lazily inside the default ones, so this imports and is tested on every OS. The microphone is never
asked for."""
import subprocess
import sys
from dataclasses import dataclass

from ._macos_pure import ACCESSIBILITY, LISTEN, SCREEN, _PANE

LOCAL_NETWORK = "Local Network"
GRANTED, MISSING, UNKNOWN = "granted", "missing", "unknown"


@dataclass(frozen=True)
class PermRow:
    name: str
    status: str      # granted / missing / unknown
    why: str
    url: str
    optional: bool = False


_INFO = [  # name, why, pane, optional
    (SCREEN, "Lets the headset see your windows and screens.", "ScreenCapture", False),
    (ACCESSIBILITY, "Lets the headset click, type and raise windows.", "Accessibility", False),
    (LISTEN, "Only for the kill-switch hotkey. Optional.", "ListenEvent", True),
    (LOCAL_NETWORK, "Lets the agent reach a headset on your network.", "LocalNetwork", False),
]
NAMES = tuple(i[0] for i in _INFO)
_REQUESTABLE = (SCREEN, ACCESSIBILITY, LISTEN)


def _default_probe(name: str):
    """True/False from the system, or None when it can't be told (Local Network has no public check)."""
    if name == ACCESSIBILITY:
        from ApplicationServices import AXIsProcessTrusted
        return bool(AXIsProcessTrusted())
    if name in (SCREEN, LISTEN):
        import Quartz
        return bool(Quartz.CGPreflightScreenCaptureAccess() if name == SCREEN else Quartz.CGPreflightListenEventAccess())
    return None


def _default_request(name: str) -> None:
    if name == SCREEN:
        import Quartz
        Quartz.CGRequestScreenCaptureAccess()
    elif name == ACCESSIBILITY:
        from ApplicationServices import AXIsProcessTrustedWithOptions, kAXTrustedCheckOptionPrompt
        AXIsProcessTrustedWithOptions({kAXTrustedCheckOptionPrompt: True})
    elif name == LISTEN:
        import Quartz
        Quartz.CGRequestListenEventAccess()
    # Local Network has no request API: macOS asks on first use


def rows(probe=None, local_network_denied: bool = False, platform: str | None = None) -> list[PermRow]:
    """The permission rows, [] off macOS. probe(name) -> True/False/None; Local Network is "unknown" unless
    local_network_denied says a connection looked blocked (then "missing")."""
    if (platform or sys.platform) != "darwin":
        return []
    probe = probe or _default_probe
    out = []
    for name, why, pane, optional in _INFO:
        if name == LOCAL_NETWORK:
            status = MISSING if local_network_denied else UNKNOWN
        else:
            try:
                got = probe(name)
            except Exception:
                got = None
            status = UNKNOWN if got is None else GRANTED if got else MISSING
        out.append(PermRow(name, status, why, _PANE + pane, optional))
    return out


def request(name: str, requester=None) -> bool:
    """Calls the system request for one permission. False for a name with nothing to request."""
    if name not in _REQUESTABLE:
        return False
    (requester or _default_request)(name)
    return True


class PermissionGate:
    """Requests each permission at most once per run, whatever triggers it."""

    def __init__(self, requester=None):
        self._requester = requester
        self.requested: set[str] = set()

    def ensure(self, name: str) -> bool:
        """Requests name the first time only; True if a request was made now."""
        if name in self.requested or name not in _REQUESTABLE:
            return False
        self.requested.add(name)
        return request(name, self._requester)


def open_settings(url: str, runner=None) -> None:
    (runner or subprocess.run)(["open", url], check=False)
