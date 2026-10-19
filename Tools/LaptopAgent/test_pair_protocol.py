"""Tests for the code-based pairing exchange (wire v2): both roles over real sockets on 127.0.0.1.

  python -m unittest test_pair_protocol -v        (from Tools/LaptopAgent)

Most tests run PBKDF2 with a few iterations (both sides get the same override) to stay fast; the known-answer vectors
and one full exchange use the real 100000.
"""
import asyncio
import hashlib
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from cryptography.hazmat.primitives import serialization

import handshake as hs
import identity
import pair_protocol as pp
import pairing
from test_handshake import AGENT, HEADSET, OTHER, FakeClock, RecWriter, Relay, b64, edit, recv, send
from test_identity import TEST_SPKI_PEM

FAST = 1000
ANY_WRONG = "ZZZZZZZZZZZZ"


class PairCase(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.store = pairing.PairStore(Path(self._tmp.name))
        self.clock = FakeClock()
        self.issuer = pairing.CodeIssuer(clock=self.clock)
        self.gate = hs.HeadsetGate(attempts=1000)
        self.headset_pins: list[tuple[bytes, str]] = []
        self.prompts: list[str] = []
        self.answer = True
        self.confirm_delay = 0.0
        self._servers = []

    async def asyncTearDown(self):
        for s in self._servers:
            s.close()
            await s.wait_closed()

    async def confirm(self, prompt):
        self.prompts.append(prompt)
        if self.confirm_delay:
            await asyncio.sleep(self.confirm_delay)
        return self.answer

    def pin(self, spki, name):
        self.headset_pins.append((spki, name))

    async def serve(self, handler) -> int:
        server = await asyncio.start_server(handler, "127.0.0.1", 0)
        self._servers.append(server)
        return server.sockets[0].getsockname()[1]

    async def start_headset(self, iterations=FAST, headset=HEADSET, name="Test Headset", gate=None, **kw):
        results: asyncio.Queue = asyncio.Queue()

        async def handle(reader, writer):
            try:
                r = await pp.serve_pair_connection(reader, writer, gate or self.gate, headset, self.issuer, self.pin,
                                                   self.confirm, name, iterations=iterations, **kw)
            except Exception as e:
                r = e
            results.put_nowait(r)
        return await self.serve(handle), results

    async def run_agent(self, port, code, name_a="Test Laptop", iterations=FAST, local_addr=None, ident=AGENT, **kw):
        reader, writer = await asyncio.open_connection("127.0.0.1", port, local_addr=local_addr)
        self.sent = RecWriter(writer)
        try:
            return await pp.agent_pair(reader, self.sent, ident, self.store, code, name_a, iterations=iterations, **kw)
        except hs.HandshakeError as e:
            return e

    async def pair(self, code=None, mutate=None, **kw):
        """One exchange with a freshly issued code unless one is given. Returns (agent outcome, headset outcome)."""
        if code is None:
            code = self.issuer.issue()
        headset_kw = {k: v for k, v in kw.items() if k in ("iterations", "gate", "preauth_timeout", "total_timeout",
                                                         "confirm_timeout")}
        agent_kw = {k: v for k, v in kw.items() if k in ("iterations", "local_addr")}
        port, results = await self.start_headset(**headset_kw)
        relay = None
        if mutate:
            relay = Relay(port, mutate)
            port = await relay.start()
        agent = await self.run_agent(port, code, **agent_kw)
        headset = await asyncio.wait_for(results.get(), 10)
        if relay:
            await relay.close()
        return agent, headset

    def assertRefused(self, outcome, reason, remote=None):
        self.assertIsInstance(outcome, hs.HandshakeError)
        if remote is None:
            self.assertEqual(outcome.reason.removeprefix("peer refused: "), reason)
        else:
            self.assertEqual((outcome.reason, outcome.remote), (reason, remote))

    def assertNothingPinned(self):
        self.assertEqual(self.store.list(), [])
        self.assertEqual(self.headset_pins, [])


class SuccessTests(PairCase):
    async def test_correct_code_pins_both_keys_with_real_iterations(self):
        agent, headset = await self.pair(iterations=pp.PAIR_ITERATIONS)
        self.assertEqual(agent, pp.PairResult(HEADSET.spki_der, "Test Headset"))
        self.assertEqual(headset, pp.PairHeadsetResult(AGENT.spki_der, "Test Laptop"))
        (pair,) = self.store.list()
        self.assertEqual((pair.name, pair.public_key), ("Test Headset", HEADSET.spki_der))
        self.assertEqual(self.headset_pins, [(AGENT.spki_der, "Test Laptop")])
        self.assertFalse(self.issuer.active)  # consumed

    async def test_prompt_names_the_laptop_and_its_fingerprint(self):
        await self.pair()
        self.assertEqual(self.prompts, [f"Pair with laptop Test Laptop ({identity.format_fp(AGENT.fingerprint)})?"])

    async def test_typed_forms_of_the_code_work(self):
        code = self.issuer.issue()
        typed = pairing.format_code(code).lower().replace("0", "o").replace("1", "l")
        agent, headset = await self.pair(code=typed)
        self.assertIsInstance(agent, pp.PairResult)

    async def test_default_name_for_a_blank_headset_name(self):
        port, results = await self.start_headset(name="   ")
        agent = await self.run_agent(port, self.issuer.issue())
        await results.get()
        self.assertEqual(agent.name, hs.default_name(HEADSET.spki_der))

    async def test_agent_sends_its_name_stripped(self):
        port, results = await self.start_headset()
        agent = await self.run_agent(port, self.issuer.issue(), name_a="  Test Laptop  ")
        headset = await results.get()
        self.assertIsInstance(agent, pp.PairResult)
        self.assertEqual(self.sent.frames()[0]["name"], "Test Laptop")
        self.assertEqual(headset.name, "Test Laptop")

    async def test_agent_refuses_a_blank_name_before_sending_anything(self):
        code = self.issuer.issue()
        port, results = await self.start_headset()
        agent = await self.run_agent(port, code, name_a="   ")
        self.assertRefused(agent, "bad name", remote=False)
        self.assertEqual(self.sent.data, b"")
        self.assertEqual(self.issuer.strikes, 0)
        self.assertNothingPinned()

    async def test_headset_refuses_a_blank_name_a_before_the_not_pairing_check(self):
        for active in (False, True):
            with self.subTest(active=active):
                if active:
                    self.issuer.issue()
                port, results = await self.start_headset()
                reader, writer = await asyncio.open_connection("127.0.0.1", port)
                await send(writer, {"v": 2, "type": "pair", "nonce": b64(os.urandom(32)),
                                    "spki": b64(AGENT.spki_der), "name": "  "})
                self.assertRefused(await asyncio.wait_for(results.get(), 5), "bad name", remote=False)
                self.assertEqual((await recv(reader))["reason"], "bad name")
                self.assertEqual(self.issuer.strikes, 0)
                self.assertEqual(self.issuer.active, active)
                writer.close()

    async def test_the_code_never_crosses_the_wire(self):
        code = self.issuer.issue()
        port, results = await self.start_headset()
        agent = await self.run_agent(port, code)
        await results.get()
        self.assertIsInstance(agent, pp.PairResult)
        frames = self.sent.frames()
        self.assertEqual([f["type"] for f in frames], ["pair", "pair-proof", "pair-done"])
        self.assertTrue(all(f["v"] == 2 for f in frames))
        for secret in (code.encode(), pairing.format_code(code).encode(), code.lower().encode(), b64(code.encode()).encode()):
            self.assertNotIn(secret, self.sent.data)


class RefusalTests(PairCase):
    async def test_wrong_code(self):
        code = self.issuer.issue()
        agent, headset = await self.pair(code=ANY_WRONG)
        self.assertRefused(headset, "wrong code", remote=False)
        self.assertRefused(agent, "wrong code")
        self.assertEqual(self.issuer.strikes, 1)
        self.assertTrue(self.issuer.active)
        self.assertNothingPinned()
        self.assertTrue(self.issuer.correct(code))

    async def test_right_code_still_works_after_a_wrong_one(self):
        code = self.issuer.issue()
        await self.pair(code=ANY_WRONG)
        agent, headset = await self.pair(code=code)
        self.assertIsInstance(agent, pp.PairResult)

    async def test_nothing_issued_is_not_pairing_and_no_strike(self):
        agent, headset = await self.pair(code=pairing.new_code())
        self.assertRefused(headset, "not pairing", remote=False)
        self.assertRefused(agent, "not pairing")
        self.assertEqual(self.issuer.strikes, 0)
        self.assertNothingPinned()

    async def test_expired_code(self):
        code = self.issuer.issue()
        self.clock.t += 121
        agent, headset = await self.pair(code=code)
        self.assertRefused(agent, "not pairing")
        self.assertNothingPinned()

    async def test_code_expiring_during_the_headsets_derivation_is_refused(self):
        code = self.issuer.issue()
        port, results = await self.start_headset()
        real = pp.derive_keys
        calls = []

        def slow(*a, **kw):
            out = real(*a, **kw)
            calls.append(1)
            if len(calls) == 2:  # the headset's derivation: the code runs out while it is being stretched
                self.clock.t += 121
            return out
        with mock.patch.object(pp, "derive_keys", slow):
            agent = await self.run_agent(port, code)
        self.assertRefused(await results.get(), "not pairing", remote=False)
        self.assertRefused(agent, "not pairing")
        self.assertNothingPinned()

    async def test_reused_code(self):
        code = self.issuer.issue()
        first, _ = await self.pair(code=code)
        self.assertIsInstance(first, pp.PairResult)
        agent, headset = await self.pair(code=code)
        self.assertRefused(agent, "not pairing")
        self.assertRefused(headset, "not pairing", remote=False)
        self.assertEqual(len(self.headset_pins), 1)

    async def test_code_is_spent_even_if_the_user_declines(self):
        code = self.issuer.issue()
        self.answer = False
        await self.pair(code=code)
        self.assertFalse(self.issuer.active)

    async def test_reissue_kills_the_old_code(self):
        old = self.issuer.issue()
        new = self.issuer.issue()
        agent, _ = await self.pair(code=old if old != new else ANY_WRONG)
        self.assertRefused(agent, "wrong code")
        agent, _ = await self.pair(code=new)
        self.assertIsInstance(agent, pp.PairResult)

    async def test_three_wrong_attempts_kill_the_code_even_for_the_right_one(self):
        code = self.issuer.issue()
        for i in range(3):
            agent, _ = await self.pair(code=ANY_WRONG)
            self.assertRefused(agent, "wrong code")
            self.assertEqual(self.issuer.active, i < 2)
        agent, headset = await self.pair(code=code)
        self.assertRefused(agent, "not pairing")
        self.assertNothingPinned()

    async def test_strikes_count_across_source_addresses_and_ports(self):
        code = self.issuer.issue()
        for local in (None, ("127.0.0.2", 0), ("127.0.0.3", 0)):
            agent, _ = await self.pair(code=ANY_WRONG, local_addr=local)
            self.assertRefused(agent, "wrong code")
        self.assertFalse(self.issuer.active)
        agent, _ = await self.pair(code=code)
        self.assertRefused(agent, "not pairing")
        self.assertNothingPinned()

    async def test_declined_pins_neither_side(self):
        self.answer = False
        agent, headset = await self.pair()
        self.assertRefused(agent, "declined", remote=True)
        self.assertRefused(headset, "declined")
        self.assertNothingPinned()

    async def test_confirm_timeout_pins_neither_side(self):
        self.confirm_delay = 5.0
        agent, headset = await self.pair(confirm_timeout=0.2)
        self.assertRefused(agent, "declined", remote=True)
        self.assertRefused(headset, "declined")
        self.assertNothingPinned()

    async def test_agent_name_taken_is_checked_before_the_code_is_used(self):
        self.store.add("Test Headset", OTHER.spki_der)
        code = self.issuer.issue()
        agent, headset = await self.pair(code=code)
        self.assertRefused(agent, "name taken", remote=False)
        self.assertTrue(self.issuer.active)
        self.assertEqual(self.issuer.strikes, 0)
        self.assertEqual([p.public_key for p in self.store.list()], [OTHER.spki_der])
        self.assertEqual(self.headset_pins, [])

    async def test_agent_rejects_a_malformed_code_before_talking(self):
        for text in ("", "ABCD", "UUUUUUUUUUUU"):
            reader, writer = await asyncio.open_connection("127.0.0.1", await self.serve(lambda r, w: None))
            sent = RecWriter(writer)
            with self.assertRaises(hs.HandshakeError) as cm:
                await pp.agent_pair(reader, sent, AGENT, self.store, text, "L")
            self.assertEqual(cm.exception.reason, "bad code")
            self.assertEqual(sent.data, b"")

    async def test_other_wire_versions_are_refused(self):
        self.issuer.issue()
        port, results = await self.start_headset()
        reader, writer = await asyncio.open_connection("127.0.0.1", port)
        await send(writer, {"v": 1, "type": "pair", "nonce": b64(os.urandom(32)), "spki": b64(AGENT.spki_der),
                            "name": "x"})
        self.assertRefused(await results.get(), "unsupported version", remote=False)
        writer.close()

    async def test_gate_drops_a_second_exchange_from_the_same_address(self):
        gate = hs.HeadsetGate(attempts=1)
        self.issuer.issue()
        port, results = await self.start_headset(gate=gate)
        agent = await self.run_agent(port, ANY_WRONG)
        self.assertRefused(await results.get(), "wrong code", remote=False)
        again = await self.run_agent(port, ANY_WRONG)
        self.assertRefused(await results.get(), "rate limited", remote=False)
        self.assertEqual(self.issuer.strikes, 1)


class TamperTests(PairCase):
    """Every field of T_P is bound: changing one in flight is refused by a signature, costs no strike, pins nothing."""

    def cases(self):
        return {
            "na": edit("a2h", "pair", nonce=b64(os.urandom(32))),
            "nh": edit("h2a", "pair-hello", nonce=b64(os.urandom(32))),
            "spki_a": edit("a2h", "pair", spki=b64(OTHER.spki_der)),
            "spki_h": edit("h2a", "pair-hello", spki=b64(OTHER.spki_der)),
            "name_a": edit("a2h", "pair", name="Evil Laptop"),
            "name_h": edit("h2a", "pair-hello", name="Evil Headset"),
        }

    async def test_tampering_any_field_is_refused(self):
        for field, mutate in self.cases().items():
            with self.subTest(field=field):
                self.issuer.issue()
                code = self.issuer.code
                agent, headset = await self.pair(code=code, mutate=mutate)
                self.assertIsInstance(agent, hs.HandshakeError)
                self.assertIsInstance(headset, hs.HandshakeError)
                self.assertEqual(headset.reason, "bad signature")
                self.assertEqual(self.issuer.strikes, 0)
                self.assertTrue(self.issuer.correct(code))
                self.assertNothingPinned()

    async def test_a_changed_mac_is_a_wrong_code(self):
        code = self.issuer.issue()
        agent, headset = await self.pair(code=code, mutate=edit("a2h", "pair-proof", mac=b64(os.urandom(32))))
        self.assertRefused(headset, "wrong code", remote=False)
        self.assertEqual(self.issuer.strikes, 1)
        self.assertNothingPinned()

    async def test_a_changed_headset_mac_is_refused_by_the_agent(self):
        agent, headset = await self.pair(mutate=edit("h2a", "pair-proof", mac=b64(os.urandom(32))))
        self.assertRefused(agent, "wrong code", remote=False)
        self.assertNothingPinned()

    async def test_a_changed_headset_signature_is_refused_by_the_agent(self):
        agent, headset = await self.pair(mutate=edit("h2a", "pair-proof", sig=b64(os.urandom(256))))
        self.assertRefused(agent, "bad signature", remote=False)
        self.assertNothingPinned()

    async def test_flipping_the_confirmation_is_refused(self):
        self.answer = False
        agent, headset = await self.pair(mutate=edit("h2a", "pair-confirm", ok=True))
        self.assertRefused(agent, "bad signature", remote=False)
        self.assertNothingPinned()

    async def test_a_forged_pinned_message_is_refused_by_the_headset(self):
        agent, headset = await self.pair(mutate=edit("a2h", "pair-done", sig=b64(os.urandom(256))))
        self.assertRefused(headset, "bad signature", remote=False)
        self.assertEqual(self.headset_pins, [])

    async def test_bad_p3_signature_is_refused_before_pbkdf2_and_costs_no_strike(self):
        code = self.issuer.issue()
        with mock.patch.object(hashlib, "pbkdf2_hmac", wraps=hashlib.pbkdf2_hmac) as spy:
            agent, headset = await self.pair(code=code, mutate=edit("a2h", "pair-proof", sig=b64(os.urandom(256))))
        self.assertRefused(headset, "bad signature", remote=False)
        self.assertEqual(spy.call_count, 1)  # the agent's own derivation; the headset never got to run one
        self.assertEqual(self.issuer.strikes, 0)
        self.assertTrue(self.issuer.correct(code))
        self.assertNothingPinned()

    async def test_good_exchange_runs_pbkdf2_on_both_sides(self):
        with mock.patch.object(hashlib, "pbkdf2_hmac", wraps=hashlib.pbkdf2_hmac) as spy:
            await self.pair()
        self.assertEqual(spy.call_count, 2)


class LostMessageTests(PairCase):
    async def test_lost_p6_leaves_only_the_agent_pinned_and_a_new_code_repairs(self):
        agent, headset = await self.pair(mutate=lambda d, m: None if m["type"] == "pair-done" else m)
        self.assertIsInstance(agent, pp.PairResult)
        self.assertIsInstance(headset, hs.HandshakeError)
        self.assertEqual(len(self.store.list()), 1)
        self.assertEqual(self.headset_pins, [])
        self.assertFalse(self.issuer.active)
        again, headset = await self.pair()  # a new code; same key and same name
        self.assertIsInstance(again, pp.PairResult)
        self.assertEqual(len(self.store.list()), 1)
        self.assertEqual(self.headset_pins, [(AGENT.spki_der, "Test Laptop")])


class TimeoutTests(PairCase):
    async def test_silent_peer_hits_the_preauth_timeout(self):
        self.issuer.issue()
        port, results = await self.start_headset(preauth_timeout=0.3)
        reader, writer = await asyncio.open_connection("127.0.0.1", port)
        self.assertRefused(await asyncio.wait_for(results.get(), 5), "handshake timeout", remote=False)
        writer.close()

    async def test_stalling_after_p2_hits_the_preauth_timeout(self):
        self.issuer.issue()
        port, results = await self.start_headset(preauth_timeout=0.3)
        reader, writer = await asyncio.open_connection("127.0.0.1", port)
        await send(writer, {"v": 2, "type": "pair", "nonce": b64(os.urandom(32)), "spki": b64(AGENT.spki_der),
                            "name": "x"})
        self.assertEqual((await recv(reader))["type"], "pair-hello")
        self.assertRefused(await asyncio.wait_for(results.get(), 5), "handshake timeout", remote=False)
        writer.close()

    async def test_budget_is_extended_after_the_mac_verifies(self):
        self.confirm_delay = 0.8  # far past the 0.3 s pre-auth limit
        agent, headset = await self.pair(preauth_timeout=0.3, total_timeout=5.0)
        self.assertIsInstance(agent, pp.PairResult)
        self.assertIsInstance(headset, pp.PairHeadsetResult)

    async def test_total_budget_applies_after_auth(self):
        self.confirm_delay = 5.0
        agent, headset = await self.pair(preauth_timeout=0.3, total_timeout=0.8)
        self.assertRefused(headset, "handshake timeout", remote=False)
        self.assertNothingPinned()

    def test_default_limits(self):
        self.assertEqual((pp.HEADSET_PREAUTH_TIMEOUT, pp.HEADSET_TOTAL_TIMEOUT, pp.CONFIRM_TIMEOUT), (5.0, 75.0, 60.0))
        self.assertEqual((pp.AGENT_READ_TIMEOUT, pp.AGENT_CONFIRM_TIMEOUT, pp.AGENT_TOTAL_TIMEOUT), (10.0, 70.0, 90.0))
        self.assertEqual((pp.MAX_FRAMES, hs.CONNECT_TIMEOUT, pp.PAIR_ITERATIONS), (8, 5.0, 100_000))


# A second fixed public key (openssl genrsa 2048; the private half was discarded). The vectors only need SPKIs.
TEST_SPKI_H_PEM = b"""-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEApKJ8LajE25E9xSKiZMV6
kJXwvrM/OS8WPFPRMh4bcKJNGtGcITuPJ5/+QbLqFCoziIo4J+cW/VebqtUsKU8Z
61fT1hhDrac3ds1s6E0lKHfkClonOezlJsX3oBaR+YAmWOEdF+YL2rIw4IFOgSSi
3RmxTMvh1CnfZAKZa65D+h325m8UePMPtz+Le1x+uP+NvVrNgNC2jLpr29uVA30t
3nhwV1eNAWH/tZA2P0GSLqMO6KfLTbk4dbR8ZQKJXQEgr/zlYptUce+zitbBuxgV
XqiSV3JKIXUFrv8TqrvlCxPhNF4KxEGa+Nu+Ar8AobXxj50rIlLL0P5BxU0ojSUg
5wIDAQAB
-----END PUBLIC KEY-----
"""


def _der(pem: bytes) -> bytes:
    return serialization.load_pem_public_key(pem).public_bytes(
        serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)


class VectorTests(unittest.TestCase):
    """Known answers for the C# side. spki_a is TEST_SPKI_PEM (test_identity), spki_h is TEST_SPKI_H_PEM."""
    NA = bytes(range(32))
    NH = bytes(range(32, 64))
    CODE = "ABCDEFGHJKMN"
    SPKI_A = _der(TEST_SPKI_PEM)
    SPKI_H = _der(TEST_SPKI_H_PEM)
    NAME_A = "laptop"
    NAME_H = "headset"

    K = "f373e4b15247fe94b91aef750bc3efddde8777910380df1bcd60e08b8c3f46a0"
    K_A = "473162332210bd9eca3d99dfe3a507ac33e133a7c33289610fddb08e14953b63"
    K_H = "26227287f32fdb5fa2d87b1f05a34081f039ffce910bfb2c790ec8f592df57df"
    T_P_SHA256 = "37060e2d64a5907633130129001cc4f607fd19c8904b3f4879a35f5570c38675"
    MAC_A = "3b12a278a24e055ffb01b67dc139ba7a5a0db2a067c4cc899a331aceb9c34907"
    MAC_H = "6f6408b29b2d7fcd4ad41714dd3b57827901530cbb82bbe79096211c3a0eebea"

    def t_p(self):
        return pp.transcript(self.NA, self.NH, self.SPKI_A, self.SPKI_H, self.NAME_A, self.NAME_H)

    def test_the_two_spkis_differ(self):
        self.assertNotEqual(self.SPKI_A, self.SPKI_H)

    def test_vectors_at_100000_iterations(self):
        self.assertEqual(pp.PAIR_ITERATIONS, 100000)
        k, k_a, k_h = pp.derive_keys(self.CODE, self.NA, self.NH)
        self.assertEqual((k.hex(), k_a.hex(), k_h.hex()), (self.K, self.K_A, self.K_H))
        t = self.t_p()
        self.assertEqual(hashlib.sha256(t).hexdigest(), self.T_P_SHA256)
        self.assertEqual(pp.agent_mac(k_a, t).hex(), self.MAC_A)
        self.assertEqual(pp.headset_mac(k_h, t).hex(), self.MAC_H)

    def test_vectors_match_a_from_scratch_derivation(self):
        import hmac

        def e(b: bytes) -> bytes:
            return len(b).to_bytes(4, "big") + b
        t = (e(b"spatiallauncher-pair-v2") + e(self.NA) + e(self.NH) + e(self.SPKI_A) + e(self.SPKI_H)
             + e(b"laptop") + e(b"headset"))
        self.assertEqual(hashlib.sha256(t).hexdigest(), self.T_P_SHA256)
        k = hashlib.pbkdf2_hmac("sha256", self.CODE.encode("ascii"), e(b"sl-pair-v2") + self.NA + self.NH, 100000, 32)
        k_a = hmac.new(k, b"agent", "sha256").digest()
        k_h = hmac.new(k, b"headset", "sha256").digest()
        self.assertEqual((k.hex(), k_a.hex(), k_h.hex()), (self.K, self.K_A, self.K_H))
        self.assertEqual(hmac.new(k_a, e(b"pair-mac-agent") + t, "sha256").hexdigest(), self.MAC_A)
        self.assertEqual(hmac.new(k_h, e(b"pair-mac-headset") + t, "sha256").hexdigest(), self.MAC_H)

    def test_transcript_layout(self):
        t = self.t_p()
        label = b"spatiallauncher-pair-v2"
        self.assertTrue(t.startswith(len(label).to_bytes(4, "big") + label + b"\x00\x00\x00\x20" + self.NA))
        self.assertTrue(t.endswith(b"\x00\x00\x00\x07headset"))

    def test_message_prefixes(self):
        t = b"T"
        self.assertEqual(pp.agent_sig_message(t), hs.enc("agent") + t)
        self.assertEqual(pp.headset_sig_message(t), hs.enc("headset") + t)
        self.assertEqual(pp.confirm_message(True, t), hs.enc("headset-confirm") + hs.enc("yes") + t)
        self.assertEqual(pp.confirm_message(False, t), hs.enc("headset-confirm") + hs.enc("no") + t)
        self.assertEqual(pp.pinned_message(t), hs.enc("agent-pinned") + t)

    def test_every_input_changes_the_keys(self):
        base = pp.derive_keys(self.CODE, self.NA, self.NH, 10)
        for args in (("ABCDEFGHJKMP", self.NA, self.NH), (self.CODE, self.NH, self.NA),
                     (self.CODE, self.NA, bytes(32))):
            self.assertNotEqual(base, pp.derive_keys(*args, 10))
        self.assertNotEqual(base, pp.derive_keys(self.CODE, self.NA, self.NH, 11))

    def test_transcript_binds_every_field(self):
        base = dict(na=self.NA, nh=self.NH, spki_a=b"A", spki_h=b"H", name_a="a", name_h="h")
        t = pp.transcript(**base)
        for k, v in dict(na=self.NH, nh=self.NA, spki_a=b"B", spki_h=b"I", name_a="b", name_h="i").items():
            self.assertNotEqual(t, pp.transcript(**{**base, k: v}), k)


if __name__ == "__main__":
    unittest.main()
