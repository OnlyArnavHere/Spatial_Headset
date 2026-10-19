"""Everything the agent needs from the host OS, behind one interface.

The backend is picked by sys.platform and its names are re-exported here, so callers write hostos.key(...) and
look it up at call time (tests patch these attributes). Keys are Windows virtual-key codes on every backend.
"""
import sys

from .types import CONSTANTS, FUNCTIONS, Monitor, Rect, Window

if sys.platform == "win32":
    from . import windows as _backend
elif sys.platform == "darwin":
    from . import macos as _backend
else:
    from . import linux_x11 as _backend

for _name in FUNCTIONS + CONSTANTS:
    globals()[_name] = getattr(_backend, _name)
del _name

__all__ = ["Monitor", "Rect", "Window", *FUNCTIONS, *CONSTANTS]
