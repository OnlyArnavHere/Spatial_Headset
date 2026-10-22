"""Tests for the macOS secret store: the wrap format and the Keychain key handling run everywhere with a fake keyring;
the real Keychain round trip runs on a Mac only."""
import base64
import os
import sys
import types
import unittest
import uuid
from unittest import mock

from hostos import _macos_secrets as sec

KEY = bytes(range(32))


class KeyringError(Exception):
    pass


class FakeKeyring:
    """get_password/set_password like the keyring package; `fail` makes every call raise its KeyringError."""

    def __init__(self, fail=False):
        self.store, self.fail = {}, fail
        self.errors = types.SimpleNamespace(KeyringError=KeyringError)

    def get_password(self, service, account):
        if self.fail:
            raise KeyringError("locked")
        return self.store.get((service, account))

    def set_password(self, service, account, value):
        if self.fail:
            raise KeyringError("locked")
        self.store[(service, account)] = value


class WrapTests(unittest.TestCase):
    def test_round_trip_and_layout(self):
        blob = sec.wrap(b"pkcs8 bytes", KEY)
        self.assertEqual(blob[:1], b"\x01")
        self.assertEqual(len(blob), 1 + 12 + len(b"pkcs8 bytes") + 16)
        self.assertNotIn(b"pkcs8 bytes", blob)
        self.assertEqual(sec.unwrap(blob, KEY), b"pkcs8 bytes")
        self.assertNotEqual(blob, sec.wrap(b"pkcs8 bytes", KEY))  # a fresh nonce each time

    def test_every_flipped_byte_is_rejected(self):
        blob = sec.wrap(b"secret", KEY)
        for i in range(len(blob)):
            bad = blob[:i] + bytes([blob[i] ^ 1]) + blob[i + 1:]
            with self.assertRaises(ValueError, msg=f"byte {i}"):
                sec.unwrap(bad, KEY)

    def test_wrong_key_bad_version_and_short_blob(self):
        blob = sec.wrap(b"secret", KEY)
        with self.assertRaises(ValueError):
            sec.unwrap(blob, bytes(32))
        with self.assertRaisesRegex(ValueError, "version"):
            sec.unwrap(b"\x02" + blob[1:], KEY)
        for short in (b"", b"\x01", blob[:28]):
            with self.assertRaises(ValueError):
                sec.unwrap(short, KEY)


class KeychainTests(unittest.TestCase):
    def setUp(self):
        patcher = mock.patch.dict(os.environ, {"SPATIAL_LAUNCHER_KEYCHAIN_SERVICE": "SpatialLauncher.test.fake"})
        patcher.start()
        self.addCleanup(patcher.stop)
        self.kr = FakeKeyring()

    def test_service_is_read_at_call_time_with_a_default(self):
        self.assertEqual(sec.service(), "SpatialLauncher.test.fake")
        with mock.patch.dict(os.environ):
            del os.environ["SPATIAL_LAUNCHER_KEYCHAIN_SERVICE"]
            self.assertEqual(sec.service(), "SpatialLauncher.agent.identity.v1")

    def test_protect_creates_the_key_once(self):
        blob = sec.protect(self.kr, b"one")
        self.assertEqual(list(self.kr.store), [("SpatialLauncher.test.fake", "wrap-key")])
        stored = self.kr.store[("SpatialLauncher.test.fake", "wrap-key")]
        self.assertEqual(len(base64.b64decode(stored)), 32)
        blob2 = sec.protect(self.kr, b"two")
        self.assertEqual(self.kr.store[("SpatialLauncher.test.fake", "wrap-key")], stored)
        self.assertEqual((sec.unprotect(self.kr, blob), sec.unprotect(self.kr, blob2)), (b"one", b"two"))

    def test_unprotect_without_a_key_raises_and_never_creates_one(self):
        blob = sec.wrap(b"x", KEY)
        with self.assertRaisesRegex(ValueError, "Keychain entry SpatialLauncher.test.fake missing; "
                                                "identity.key cannot be decrypted"):
            sec.unprotect(self.kr, blob)
        self.assertEqual(self.kr.store, {})
        self.assertIsNone(sec.load_key(self.kr, create=False))

    def test_wrong_stored_key_is_a_value_error(self):
        blob = sec.protect(self.kr, b"x")
        self.kr.store[("SpatialLauncher.test.fake", "wrap-key")] = base64.b64encode(bytes(32)).decode()
        with self.assertRaises(ValueError):
            sec.unprotect(self.kr, blob)

    def test_malformed_stored_key(self):
        self.kr.store[("SpatialLauncher.test.fake", "wrap-key")] = "not base64!"
        with self.assertRaises(ValueError):
            sec.unprotect(self.kr, sec.wrap(b"x", KEY))
        with self.assertRaises(OSError):  # protect never overwrites it
            sec.protect(self.kr, b"x")
        self.assertEqual(self.kr.store[("SpatialLauncher.test.fake", "wrap-key")], "not base64!")

    def test_keyring_errors_become_os_errors(self):
        bad = FakeKeyring(fail=True)
        with self.assertRaises(OSError):
            sec.protect(bad, b"x")
        with self.assertRaises(OSError):
            sec.unprotect(bad, sec.wrap(b"x", KEY))

    def test_identity_round_trip_through_hostos_wrappers(self):
        from hostos import macos
        with mock.patch.object(macos, "_keyring", return_value=self.kr):
            self.assertEqual(macos.unprotect(macos.protect(b"data")), b"data")

    def test_default_home(self):
        from hostos import macos
        self.assertEqual(macos.default_home().parts[-4:], ("Library", "Application Support", "SpatialLauncher", "agent"))


@unittest.skipUnless(sys.platform == "darwin", "needs the macOS Keychain")
class RealKeychainTests(unittest.TestCase):
    def test_round_trip_under_a_throwaway_service(self):
        import keyring
        service = f"SpatialLauncher.test.{uuid.uuid4().hex}"
        with mock.patch.dict(os.environ, {"SPATIAL_LAUNCHER_KEYCHAIN_SERVICE": service}):
            try:
                from hostos import macos
                self.assertEqual(macos.unprotect(macos.protect(b"hello")), b"hello")
                self.assertIsNotNone(keyring.get_password(service, "wrap-key"))
            finally:
                try:
                    keyring.delete_password(service, "wrap-key")
                except Exception:
                    pass
            self.assertIsNone(keyring.get_password(service, "wrap-key"))


if __name__ == "__main__":
    unittest.main()
