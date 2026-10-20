"""Imported first by the test files that create identities: points the macOS Keychain wrap key at a throwaway entry
(and deletes it at exit), so no test ever reads or creates the real SpatialLauncher key. A no-op elsewhere."""
import atexit
import os
import sys
import uuid

SERVICE = os.environ["SPATIAL_LAUNCHER_KEYCHAIN_SERVICE"] = f"SpatialLauncher.test.{uuid.uuid4().hex}"


def _delete() -> None:
    if sys.platform != "darwin" or not SERVICE.startswith("SpatialLauncher.test."):
        return
    try:
        import keyring
        keyring.delete_password(SERVICE, "wrap-key")
    except Exception:  # never created, or already gone
        pass


atexit.register(_delete)
