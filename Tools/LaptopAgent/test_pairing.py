"""Unit tests for pairing: the one-time code and its policy, the pair store and pinning. No agent needs to be running.

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


class CodeFormatTests(unittest.TestCase):
    def test_new_code_shape_and_entropy(self):
        codes = {pairing.new_code() for _ in range(200)}
        self.assertEqual(len(codes), 200)
        for c in codes:
            self.assertRegex(c, r"^[0-9ABCDEFGHJKMNPQRSTVWXYZ]{12}$")

    def test_alphabet_is_crockford(self):
        self.assertEqual(pairing.ALPHABET, "0123456789ABCDEFGHJKMNPQRSTVWXYZ")
        self.assertEqual(len(set(pairing.ALPHABET)), 32)
        self.assertEqual(pairing.CODE_LEN, 12)

    def test_format(self):
        self.assertEqual(pairing.format_code("ABCDEFGHJKMN"), "ABCD-EFGH-JKMN")

    def test_parse_accepts_typed_forms(self):
        for text in ("ABCD-EFGH-JKMN", "abcd efgh jkmn", " abcdefghjkmn  ", "ab-cd ef-gh-jk mn"):
            self.assertEqual(pairing.parse_code(text), "ABCDEFGHJKMN", text)

    def test_parse_maps_confusables(self):
        self.assertEqual(pairing.parse_code("iL0o-1111-0000"), "1100" + "1111" + "0000")
        self.assertEqual(pairing.parse_code("IIII-LLLL-OOOO"), "1111" + "1111" + "0000")

    def test_parse_rejects(self):
        for text in ("", "ABCD-EFGH-JKM", "ABCD-EFGH-JKMNP", "ABCD-EFGH-JKMU", "UUUUUUUUUUUU", "ABCD-EFGH-JKM!",
                     "ABCD-EFGH-JKÉN", "ABCD_EFGH_JKMN"):
            with self.subTest(text=text), self.assertRaises(ValueError):
                pairing.parse_code(text)

    def test_round_trip(self):
        code = pairing.new_code()
        self.assertEqual(pairing.parse_code(pairing.format_code(code)), code)


class CodeIssuerTests(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.issuer = pairing.CodeIssuer(clock=self.clock)

    def wrong_for(self, code):
        return "000000000000" if code != "000000000000" else "111111111111"

    def test_defaults(self):
        self.assertEqual(pairing.CODE_TTL, 120.0)
        self.assertEqual(pairing.MAX_WRONG_ATTEMPTS, 3)
        self.assertFalse(self.issuer.active)
        self.assertIsNone(self.issuer.code)

    def test_issue_gives_canonical_code(self):
        code = self.issuer.issue()
        self.assertEqual(pairing.parse_code(code), code)
        self.assertTrue(self.issuer.active)
        self.assertTrue(self.issuer.correct(code))
        self.assertFalse(self.issuer.correct(self.wrong_for(code)))

    def test_single_use(self):
        code = self.issuer.issue()
        self.issuer.consume()
        self.assertFalse(self.issuer.active)
        self.assertFalse(self.issuer.correct(code))

    def test_expires_at_ttl(self):
        self.issuer.issue()
        self.clock.t += 119
        self.assertTrue(self.issuer.active)
        self.clock.t += 2
        self.assertFalse(self.issuer.active)
        self.assertIsNone(self.issuer.code)

    def test_reissue_invalidates_old(self):
        old = self.issuer.issue()
        new = self.issuer.issue()
        while new == old:
            new = self.issuer.issue()
        self.assertFalse(self.issuer.correct(old))
        self.assertTrue(self.issuer.correct(new))

    def test_reissue_resets_strikes(self):
        self.issuer.issue()
        self.issuer.strike()
        self.issuer.strike()
        self.issuer.issue()
        self.assertEqual(self.issuer.strikes, 0)

    def test_third_strike_invalidates(self):
        self.issuer.issue()
        self.issuer.strike()
        self.issuer.strike()
        self.assertTrue(self.issuer.active)
        self.issuer.strike()
        self.assertFalse(self.issuer.active)

    def test_strike_without_code_does_nothing(self):
        self.issuer.strike()
        self.assertEqual(self.issuer.strikes, 0)

    def test_comparison_is_constant_time(self):
        with mock.patch.object(pairing.hmac, "compare_digest", wraps=pairing.hmac.compare_digest) as spy:
            code = self.issuer.issue()
            self.issuer.correct(code)
        spy.assert_called()


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


if __name__ == "__main__":
    unittest.main()
