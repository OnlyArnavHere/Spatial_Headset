"""Unit tests for the agent's identity: key storage, signatures and fingerprints. No agent needs to be running.

  python -m unittest test_identity -v        (from Tools/LaptopAgent)
"""
import sys
import tempfile
import unittest
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding

import identity
import winapi

# A fixed 2048-bit public key (made with `openssl genrsa` + `openssl pkey -pubout`, private half discarded).
# Expected fingerprint computed independently of this code:
#   openssl pkey -pubin -in pub.pem -outform DER | openssl dgst -sha256 -binary | head -c 10 | base32
TEST_SPKI_PEM = b"""-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAu1wr5MR/nvELy6ve8lbV
OGQTdl7JKq+XCEey8ihCEpGcCBvEL/TItLFEzxsN6zHc5D2gVYRFOfurvoJw3qIH
bNFrwF3JDoyz66PAvD5eWTlrM+I1y2bYCn9NxWjVUqaZzforfSg21kPfoPo9v1jt
DMw5PoQil2dt6K0epVn6z7PEUybGl4lFjyMekIgF90habmUzc4NGubb/Oh+HNi12
R8DAFKSeXAASLVI42PIQ8NjqGkIthVEL0BGsR2LajpY7DXV93fPd1dfBadJjcsuX
73M0kIHfdXNhV5R0K1ZmAYvrtxR0QbaQNAeYVPVJTOd5VeF0dsGlfsGtfnnCZQIj
NwIDAQAB
-----END PUBLIC KEY-----
"""
TEST_SPKI_FP = "QBM5P7AOIJGXGGFP"


class IdentityStoreTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.home = Path(self.tmp.name)
        self.addCleanup(self.tmp.cleanup)

    def test_create_then_reload_gives_same_key(self):
        a = identity.load_or_create(self.home)
        b = identity.load_or_create(self.home)
        self.assertEqual(a.spki_der, b.spki_der)
        self.assertEqual(a.fingerprint, b.fingerprint)
        self.assertEqual(len(a.fingerprint), 16)

    def test_file_is_not_plain_pkcs8(self):
        ident = identity.load_or_create(self.home)
        raw = (self.home / identity.KEY_FILE).read_bytes()
        plain = ident.key.private_bytes(serialization.Encoding.DER, serialization.PrivateFormat.PKCS8,
                                        serialization.NoEncryption())
        self.assertNotEqual(raw, plain)
        self.assertNotIn(plain[-64:], raw)
        with self.assertRaises(ValueError):
            serialization.load_der_private_key(raw, password=None)

    def test_no_temp_files_left(self):
        identity.load_or_create(self.home)
        self.assertEqual([p.name for p in self.home.iterdir()], [identity.KEY_FILE])

    def test_corrupt_file_raises_and_is_kept(self):
        identity.load_or_create(self.home)
        path = self.home / identity.KEY_FILE
        path.write_bytes(b"garbage")
        with self.assertRaises(identity.IdentityError):
            identity.load_or_create(self.home)
        self.assertEqual(path.read_bytes(), b"garbage")

    def test_key_shape(self):
        key = identity.load_or_create(self.home).key
        self.assertEqual(key.key_size, 2048)
        self.assertEqual(key.public_key().public_numbers().e, 65537)

    def test_default_home_override(self):
        from unittest import mock
        with mock.patch.dict("os.environ", {"SPATIAL_LAUNCHER_HOME": str(self.home)}):
            self.assertEqual(identity.default_home(), self.home)


@unittest.skipUnless(sys.platform == "win32", "DPAPI is Windows only")
class DpapiTests(unittest.TestCase):
    def test_round_trip(self):
        blob = winapi.dpapi_protect(b"secret bytes", identity.ENTROPY)
        self.assertNotIn(b"secret bytes", blob)
        self.assertEqual(winapi.dpapi_unprotect(blob, identity.ENTROPY), b"secret bytes")

    def test_wrong_entropy_fails(self):
        blob = winapi.dpapi_protect(b"secret bytes", identity.ENTROPY)
        with self.assertRaises(OSError):
            winapi.dpapi_unprotect(blob, b"other entropy")

    def test_garbage_fails(self):
        with self.assertRaises(OSError):
            winapi.dpapi_unprotect(b"not a blob", identity.ENTROPY)


class SignatureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.ident = identity.load_or_create(Path(cls.tmp.name))

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_sign_verify(self):
        sig = identity.sign(self.ident.key, b"hello")
        self.assertTrue(identity.verify(self.ident.spki_der, sig, b"hello"))

    def test_uses_pss_with_salt_32(self):
        sig = identity.sign(self.ident.key, b"hello")
        pub = serialization.load_der_public_key(self.ident.spki_der)
        pss = lambda salt: padding.PSS(mgf=padding.MGF1(hashes.SHA256()), salt_length=salt)
        pub.verify(sig, b"hello", pss(32), hashes.SHA256())
        with self.assertRaises(Exception):
            pub.verify(sig, b"hello", pss(20), hashes.SHA256())

    def test_tamper_fails(self):
        sig = identity.sign(self.ident.key, b"hello")
        self.assertFalse(identity.verify(self.ident.spki_der, sig, b"hellO"))
        bad = bytes([sig[0] ^ 1]) + sig[1:]
        self.assertFalse(identity.verify(self.ident.spki_der, bad, b"hello"))
        self.assertFalse(identity.verify(b"not a key", sig, b"hello"))


class FingerprintTests(unittest.TestCase):
    def test_known_answer_empty_input(self):
        # base32 of the first 10 bytes of SHA-256(b"")
        self.assertEqual(identity.fingerprint(b""), "4OYMIQUY7QOBJGX3")

    def test_known_answer_fixed_public_key(self):
        der = serialization.load_pem_public_key(TEST_SPKI_PEM).public_bytes(
            serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
        self.assertEqual(identity.fingerprint(der), TEST_SPKI_FP)

    def test_format_and_normalize(self):
        self.assertEqual(identity.format_fp("4OYMIQUY7QOBJGX3"), "4OYM-IQUY-7QOB-JGX3")
        self.assertEqual(identity.normalize_fp("4oym-iquy 7qob-jgx3"), "4OYMIQUY7QOBJGX3")

    def test_normalize_rejects(self):
        for bad in ["", "4OYMIQUY7QOBJGX", "4OYMIQUY7QOBJGX33", "4OYMIQUY7QOBJGX1", "4OYMIQUY7QOBJGX=", "4OYM!QUY7QOBJGX3"]:
            with self.assertRaises(ValueError, msg=bad):
                identity.normalize_fp(bad)
        with self.assertRaises(ValueError):
            identity.format_fp("short")


if __name__ == "__main__":
    unittest.main()
