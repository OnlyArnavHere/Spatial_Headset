"""Unit tests for pairing: codes, the pair store, pinning and the QR / typed offer. No agent needs to be running.

  python -m unittest test_pairing -v        (from Tools/LaptopAgent)
"""
import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest import mock

import identity
import pairing

KEY_A = b"spki-A" * 20
KEY_B = b"spki-B" * 20
FP = "4OYMIQUY7QOBJGX3"
T1 = datetime(2026, 1, 2, 3, 4, 5, tzinfo=timezone.utc)
T2 = datetime(2026, 2, 3, 4, 5, 6, tzinfo=timezone.utc)


class FakeClock:
    def __init__(self):
        self.t = 1000.0

    def __call__(self):
        return self.t


class CodeTests(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.issuer = pairing.CodeIssuer(clock=self.clock, ttl=300)

    def test_format(self):
        self.assertRegex(self.issuer.issue(), r"^[A-Z2-7]{8}$")

    def test_single_use(self):
        code = self.issuer.issue()
        self.assertTrue(self.issuer.redeem(code))
        self.assertFalse(self.issuer.redeem(code))

    def test_expires(self):
        code = self.issuer.issue()
        self.clock.t += 301
        self.assertFalse(self.issuer.redeem(code))

    def test_valid_just_before_expiry(self):
        code = self.issuer.issue()
        self.clock.t += 299
        self.assertTrue(self.issuer.redeem(code))

    def test_reissue_invalidates_old(self):
        old = self.issuer.issue()
        new = self.issuer.issue()
        while new == old:
            new = self.issuer.issue()
        self.assertFalse(self.issuer.redeem(old))
        self.assertTrue(self.issuer.redeem(new))

    def test_five_wrong_attempts_burn_it(self):
        code = self.issuer.issue()
        wrong = "AAAAAAAA" if code != "AAAAAAAA" else "BBBBBBBB"
        for _ in range(5):
            self.assertFalse(self.issuer.redeem(wrong))
        self.assertFalse(self.issuer.redeem(code))

    def test_four_wrong_then_right_works(self):
        code = self.issuer.issue()
        wrong = "AAAAAAAA" if code != "AAAAAAAA" else "BBBBBBBB"
        for _ in range(4):
            self.assertFalse(self.issuer.redeem(wrong))
        self.assertTrue(self.issuer.redeem(code))

    def test_nothing_issued(self):
        self.assertFalse(self.issuer.redeem("AAAAAAAA"))


class StoreTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.home = Path(self.tmp.name)
        self.addCleanup(self.tmp.cleanup)
        self.store = pairing.PairStore(self.home)

    def test_add_list_revoke(self):
        self.assertEqual(self.store.list(), [])
        self.store.add("Arnav's iPhone", KEY_A, now=T1)
        self.store.add("Other", KEY_B, now=T1)
        self.assertEqual([p.name for p in self.store.list()], ["Arnav's iPhone", "Other"])
        self.assertTrue(self.store.revoke("arnav's iphone"))
        self.assertEqual([p.name for p in self.store.list()], ["Other"])

    def test_same_key_under_other_name_rejected(self):
        self.store.add("A", KEY_A, now=T1)
        with self.assertRaisesRegex(ValueError, "already paired as A"):
            self.store.add("B", KEY_A, now=T1)
        self.assertTrue(self.store.revoke("A"))
        self.assertIsNone(pairing.pinned(self.store, KEY_A))

    def test_pairs_command_corrupt_file(self):
        import argparse
        import contextlib
        import io
        import os
        import agent
        (self.home / "pairs.json").write_text("{not json", encoding="utf-8")
        err = io.StringIO()
        with mock.patch.dict(os.environ, {"SPATIAL_LAUNCHER_HOME": str(self.home)}), contextlib.redirect_stderr(err):
            rc = agent.pairs_command(argparse.Namespace(pairs=True, revoke=None))
        self.assertEqual(rc, 1)
        self.assertEqual(len(err.getvalue().strip().splitlines()), 1)
        self.assertIn("pairs.json", err.getvalue())

    def test_revoke_unknown(self):
        self.assertFalse(self.store.revoke("nobody"))

    def test_persistence_and_json_shape(self):
        self.store.add("Phone", KEY_A, now=T1)
        doc = json.loads((self.home / "pairs.json").read_text(encoding="utf-8"))
        self.assertEqual(doc["version"], 1)
        (entry,) = doc["pairs"]
        self.assertEqual(sorted(entry), ["added", "fingerprint", "name", "public_key"])
        self.assertEqual(entry["added"], "2026-01-02T03:04:05Z")
        self.assertEqual(entry["fingerprint"], identity.fingerprint(KEY_A))
        self.assertEqual(len(entry["fingerprint"]), 16)
        again = pairing.PairStore(self.home).list()
        self.assertEqual(again[0].public_key, KEY_A)

    def test_same_name_same_key_updates_added(self):
        self.store.add("Phone", KEY_A, now=T1)
        self.store.add("PHONE", KEY_A, now=T2)
        (p,) = self.store.list()
        self.assertEqual(p.name, "Phone")
        self.assertEqual(p.added, "2026-02-03T04:05:06Z")

    def test_same_name_different_key_raises(self):
        self.store.add("Phone", KEY_A, now=T1)
        with self.assertRaises(ValueError):
            self.store.add("phone", KEY_B, now=T2)
        self.assertEqual(self.store.list()[0].public_key, KEY_A)

    def test_pinned_and_check_headset(self):
        self.store.add("Phone", KEY_A, now=T1)
        self.assertEqual(pairing.pinned(self.store, KEY_A).name, "Phone")
        self.assertIsNone(pairing.pinned(self.store, KEY_B))
        self.assertEqual(pairing.check_headset(self.store, KEY_A, "phone").name, "Phone")
        with self.assertRaises(PermissionError):
            pairing.check_headset(self.store, KEY_B)
        with self.assertRaises(PermissionError):
            pairing.check_headset(self.store, KEY_A, "Someone else")

    def test_matching_fingerprint_but_different_bytes_is_refused(self):
        with mock.patch.object(identity, "fingerprint", return_value=FP):
            self.store.add("Phone", KEY_A, now=T1)
            self.assertEqual(identity.fingerprint(KEY_B), self.store.list()[0].fingerprint)
            self.assertIsNone(pairing.pinned(self.store, KEY_B))
            with self.assertRaises(PermissionError):
                pairing.check_headset(self.store, KEY_B)

    def test_atomic_write_leaves_no_temp_files(self):
        self.store.add("Phone", KEY_A, now=T1)
        self.store.add("Other", KEY_B, now=T1)
        self.store.revoke("Other")
        self.assertEqual([p.name for p in self.home.iterdir()], ["pairs.json"])

    def test_failed_write_keeps_old_file(self):
        self.store.add("Phone", KEY_A, now=T1)
        before = (self.home / "pairs.json").read_bytes()
        with mock.patch("os.replace", side_effect=OSError("boom")):
            with self.assertRaises(OSError):
                self.store.add("Other", KEY_B, now=T2)
        self.assertEqual((self.home / "pairs.json").read_bytes(), before)
        self.assertEqual([p.name for p in self.home.iterdir()], ["pairs.json"])

    def test_corrupt_file_raises_and_is_not_overwritten(self):
        path = self.home / "pairs.json"
        for content in (b"{not json", b'{"version": 2, "pairs": []}', b"[]"):
            path.write_bytes(content)
            with self.assertRaises(ValueError):
                self.store.list()
            with self.assertRaises(ValueError):
                self.store.add("Phone", KEY_A, now=T1)
            with self.assertRaises(ValueError):
                self.store.revoke("Phone")
            self.assertEqual(path.read_bytes(), content)


class OfferTests(unittest.TestCase):
    def test_minimal_round_trip(self):
        text = pairing.encode_payload(FP, "ABCDEFGH")
        self.assertEqual(text, f"slpair:1?f={FP}&c=ABCDEFGH")
        offer = pairing.parse_payload(text)
        self.assertEqual(offer, pairing.PairOffer(FP, "ABCDEFGH"))

    def test_full_round_trip(self):
        text = pairing.encode_payload(FP, "ABCDEFGH", host="192.168.1.20", port=8765, name="Arnav's PC & co")
        self.assertEqual(text, f"slpair:1?f={FP}&c=ABCDEFGH&h=192.168.1.20&p=8765&n=Arnav%27s%20PC%20%26%20co")
        offer = pairing.parse_payload(text)
        self.assertEqual(offer, pairing.PairOffer(FP, "ABCDEFGH", "192.168.1.20", 8765, "Arnav's PC & co"))

    def test_ipv6_round_trip(self):
        offer = pairing.parse_payload(pairing.encode_payload(FP, "ABCDEFGH", host="fe80::1"))
        self.assertEqual(offer.host, "fe80::1")

    def test_unknown_keys_ignored(self):
        offer = pairing.parse_payload(f"slpair:1?f={FP}&c=ABCDEFGH&zz=1")
        self.assertEqual(offer.code, "ABCDEFGH")

    def test_rejects(self):
        good = f"f={FP}&c=ABCDEFGH"
        bad = [
            f"slpair:2?{good}", f"other:1?{good}", f"http://x/?{good}", "",
            f"slpair:1?f=SHORT&c=ABCDEFGH", f"slpair:1?f={FP}&c=ABC", f"slpair:1?f={FP}&c=ABCDEFG1",
            f"slpair:1?c=ABCDEFGH", f"slpair:1?f={FP}",
            f"slpair:1?{good}&p=0", f"slpair:1?{good}&p=65536", f"slpair:1?{good}&p=abc", f"slpair:1?{good}&p=-1",
            f"slpair:1?{good}&h=999.1.1.1", f"slpair:1?{good}&h=example.com",
        ]
        for text in bad:
            with self.assertRaises(ValueError, msg=text):
                pairing.parse_payload(text)

    def test_encode_rejects(self):
        with self.assertRaises(ValueError):
            pairing.encode_payload(FP, "ABCDEFGH", port=70000)
        with self.assertRaises(ValueError):
            pairing.encode_payload(FP, "ABCDEFGH", host="nope")

    def test_typed_round_trip(self):
        offer = pairing.PairOffer(FP, "ABCDEFGH")
        text = pairing.typed_text(offer)
        self.assertEqual(text, "4OYM-IQUY-7QOB-JGX3 ABCD-EFGH")
        self.assertEqual(pairing.parse_typed(text), (FP, "ABCDEFGH"))
        self.assertEqual(pairing.parse_typed("4oymiquy 7qobjgx3  abcd efgh"), (FP, "ABCDEFGH"))
        with self.assertRaises(ValueError):
            pairing.parse_typed("4OYM-IQUY-7QOB-JGX3 ABCD")

    def test_render_qr_smoke(self):
        out = pairing.render_qr(pairing.encode_payload(FP, "ABCDEFGH", host="192.168.1.20", port=8765))
        self.assertIsInstance(out, str)
        self.assertGreater(len(out.splitlines()), 10)


if __name__ == "__main__":
    unittest.main()
