"""Fills a backend module that isn't implemented yet: it imports everywhere, and every function says so when called."""
from .types import CONSTANTS, FUNCTIONS


def fill(namespace: dict, message: str) -> None:
    def make(name):
        def unsupported(*args, **kwargs):
            raise NotImplementedError(message)
        unsupported.__name__ = name
        return unsupported

    class ChildReaper:
        def __init__(self) -> None:
            raise NotImplementedError(message)

        def spawn(self, command, **kwargs):
            raise NotImplementedError(message)

    for name in FUNCTIONS:
        namespace[name] = make(name)
    namespace["preflight"] = lambda: []
    namespace["check_key_file"] = lambda path: None
    namespace.update(KILL_SWITCH="", ENCODERS=[], exe_suffix="", ChildReaper=ChildReaper)
    assert set(CONSTANTS) <= set(namespace)
