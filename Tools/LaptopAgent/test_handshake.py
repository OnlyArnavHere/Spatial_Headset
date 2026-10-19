"""Unit tests for the signed link handshake: both roles over real sockets on 127.0.0.1, no aiortc and no headset.

  python -m unittest test_handshake -v        (from Tools/LaptopAgent)
"""
import asyncio
import base64
import json
import os
import struct
import tempfile
import unittest
from pathlib import Path

from cryptography.hazmat.primitives.asymmetric import rsa

import handshake as hs
import identity
import pairing


def make_identity() -> identity.Identity:
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    return identity.Identity(key, identity.public_der(key))


AGENT = make_identity()
HEADSET = make_identity()
OTHER = make_identity()

FP_OFFER = ":".join(["AA"] * 32)
FP_ANSWER = ":".join(["BB"] * 32)
SHA1_LINE = "a=fingerprint:sha-1 " + ":".join(["AA"] * 20)


def sdp(fp: str | None = FP_OFFER, extra: str = "") -> str:
    lines = ["v=0", "o=- 1 1 IN IP4 127.0.0.1", "s=-", "t=0 0", "m=application 9 UDP/DTLS/SCTP webrtc-datachannel"]
    if fp:
        lines.append("a=fingerprint:sha-256 " + fp)
    if extra:
        lines.append(extra)
    return "\r\n".join(lines) + "\r\n"


def b64(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


class FakeClock:
    def __init__(self):
        self.t = 1000.0

    def __call__(self):
        return self.t


class RecWriter:
    """Delegates to a StreamWriter and records every byte written."""

    def __init__(self, writer):
        self._w = writer
        self.data = b""

    def write(self, data):
        self.data += data
        self._w.write(data)

    def __getattr__(self, name):
        return getattr(self._w, name)

    def frames(self) -> list[dict]:
        out, i = [], 0
        while i < len(self.data):
            (n,) = struct.unpack(">I", self.data[i:i + 4])
            out.append(json.loads(self.data[i + 4:i + 4 + n]))
            i += 4 + n
        return out


async def send(writer, obj: dict) -> None:
    body = json.dumps(obj).encode("utf-8")
    writer.write(struct.pack(">I", len(body)) + body)
    await writer.drain()


async def recv(reader) -> dict:
    (n,) = struct.unpack(">I", await reader.readexactly(4))
    return json.loads(await reader.readexactly(n))


class Relay:
    """A man in the middle: passes frames through mutate(direction, msg), where direction is 'a2h' or 'h2a'."""

    def __init__(self, target_port, mutate=None):
        self.target = target_port
        self.mutate = mutate or (lambda d, m: m)
        self.log: list[tuple[str, dict]] = []
        self.server = None

    async def start(self) -> int:
        self.server = await asyncio.start_server(self._handle, "127.0.0.1", 0)
        return self.server.sockets[0].getsockname()[1]

    async def _handle(self, reader, writer):
        up_r, up_w = await asyncio.open_connection("127.0.0.1", self.target)
        await asyncio.gather(self._pump("a2h", reader, up_w), self._pump("h2a", up_r, writer))

    async def _pump(self, direction, reader, writer):
        try:
            while True:
                msg = await recv(reader)
                self.log.append((direction, msg))
                msg = self.mutate(direction, msg)
                if msg is not None:
                    await send(writer, msg)
        except (asyncio.IncompleteReadError, ConnectionError):
            pass
        finally:
            writer.close()

    async def close(self):
        self.server.close()
        await self.server.wait_closed()


class HandshakeCase(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.store = pairing.PairStore(Path(self._tmp.name))
        self._servers = []

    async def asyncTearDown(self):
        for s in self._servers:
            s.close()
            await s.wait_closed()

    # -- harness --

    async def serve(self, handler) -> int:
        server = await asyncio.start_server(handler, "127.0.0.1", 0)
        self._servers.append(server)
        return server.sockets[0].getsockname()[1]

    async def start_headset(self, pinned=(), make_answer=None, headset=HEADSET, name="Test Headset",
                            **timeouts):
        results: asyncio.Queue = asyncio.Queue()

        async def answer(offer):
            return sdp(FP_ANSWER)

        async def handle(reader, writer):
            try:
                r = await hs.headset_handshake(reader, writer, headset, name, pinned,
                                               make_answer or answer, **timeouts)
            except Exception as e:
                r = e
            results.put_nowait(r)

        return await self.serve(handle), results

    async def run_agent(self, port, make_offer=None, ident=AGENT, **kw):
        reader, writer = await asyncio.open_connection("127.0.0.1", port)
        self.sent = RecWriter(writer)

        async def offer():
            return sdp(FP_OFFER)

        try:
            return await hs.agent_handshake(reader, self.sent, ident, self.store, make_offer or offer, **kw)
        except hs.HandshakeError as e:
            return e

    async def session(self, *, pin_headset=False, pinned_agents=(), mutate=None, **kw):
        """One agent-to-headset run. Returns (agent result or error, headset result or error)."""
        if pin_headset:
            self.store.add("Test Headset", HEADSET.spki_der)
        port, results = await self.start_headset(pinned=pinned_agents)
        relay = None
        if mutate:
            relay = Relay(port, mutate)
            port = await relay.start()
        agent = await self.run_agent(port, **kw)
        headset = await asyncio.wait_for(results.get(), 5)
        if relay:
            await relay.close()
        return agent, headset

    def assertRefused(self, *outcomes, reason):
        for o in outcomes:
            self.assertIsInstance(o, hs.HandshakeError)
        self.assertTrue(any(o.reason == reason and not o.remote for o in outcomes),
                        f"expected a local refusal {reason!r}, got {[o.reason for o in outcomes]}")


def edit(direction, mtype, **changes):
    """A mutate function that changes fields of matching frames (a value of None removes the field)."""
    def mutate(d, m):
        if d == direction and m.get("type") == mtype:
            m = {**m, **changes}
            for k in [k for k, v in m.items() if v is None]:
                del m[k]
        return m
    return mutate


PAIRED = dict(pin_headset=True, pinned_agents=[AGENT.spki_der])


# --- Pure functions --------------------------------------------------------------------------

class PureTests(unittest.TestCase):
    def test_enc(self):
        self.assertEqual(hs.enc("ab"), b"\x00\x00\x00\x02ab")
        self.assertEqual(hs.enc(b""), b"\x00\x00\x00\x00")

    def test_fingerprint_forms(self):
        self.assertEqual(hs.sdp_fingerprint(sdp("aa:" * 31 + "aa")), FP_OFFER)
        both = sdp(FP_OFFER).replace("m=application", "a=fingerprint:sha-256 " + FP_OFFER + "\r\nm=application")
        self.assertEqual(hs.sdp_fingerprint(both), FP_OFFER)  # session and media level, same value
        self.assertEqual(hs.sdp_fingerprint(sdp(FP_OFFER).replace("\r\n", "\n")), FP_OFFER)

    def test_fingerprint_refusals(self):
        bad = [sdp(None),
               sdp(FP_OFFER, "a=fingerprint:sha-256 " + FP_ANSWER),
               sdp(None, SHA1_LINE),
               sdp(FP_OFFER, SHA1_LINE),
               sdp(None, "a=fingerprint:sha-256 AA:BB"),
               sdp(None, "a=fingerprint:sha-256 " + "ZZ:" * 31 + "ZZ")]
        for text in bad:
            with self.assertRaises(hs.HandshakeError):
                hs.sdp_fingerprint(text)

    def test_fingerprint_line_rules_are_strict(self):
        fp = "a=fingerprint:sha-256 " + FP_OFFER
        base = "v=0\r\n" + fp + "\r\n"
        self.assertEqual(hs.sdp_fingerprint(base), FP_OFFER)
        self.assertEqual(hs.sdp_fingerprint(base.replace("\r\n", "\n")), FP_OFFER)
        bad = {
            "tab separator": base.replace("sha-256 ", "sha-256\t"),
            "double space": base.replace("sha-256 ", "sha-256  "),
            "trailing space": base.replace(FP_OFFER, FP_OFFER + " "),
            "trailing tab": base.replace(FP_OFFER, FP_OFFER + "\t"),
            "trailing text": base.replace(FP_OFFER, FP_OFFER + "x"),
            "upper-case algorithm": base.replace("sha-256", "SHA-256"),
            "short value": base.replace(FP_OFFER, ":".join(["AA"] * 31)),
            "no colons": base.replace(FP_OFFER, "AA" * 32),
            "non-hex": base.replace(FP_OFFER, ":".join(["GG"] * 32)),
            "no value": "v=0\r\na=fingerprint:sha-256\r\n" + fp + "\r\n",
            "lone CR": base.replace("v=0", "v=0\r"),
            "lone CR mid-line": "v=0\r" + "s=-\r\n" + fp + "\r\n",
            "NEL": base.replace("v=0", "v=0\x85"),
            "line separator": base.replace("v=0", "v=0\u2028"),
            "paragraph separator": base.replace("v=0", "v=0\u2029"),
            "NUL": base.replace("v=0", "v=0\x00"),
            "tab": base.replace("v=0", "v=0\t"),
            "DEL": base.replace("v=0", "v=0\x7f"),
            "CR CR LF": base.replace("v=0\r\n", "v=0\r\r\n"),
            "LF CR": base.replace("v=0\r\n", "v=0\n\r"),
        }
        for label, text in bad.items():
            with self.subTest(label), self.assertRaises(hs.HandshakeError):
                hs.sdp_fingerprint(text)

    def test_name_rules(self):
        for ok in ("Test Headset", "", " ", "a b", "\u00e9" * 32, "\U0001F600" * 32, "x" * 32):
            self.assertEqual(hs.validate_name(ok), ok)
        bad = ["x" * 33, "\U0001F600" * 33, "a\tb", "a\nb", "a\x00", "a\x7f", "a\u00a0b", "a\u2003b", "a\u200bb",
               "a\u00adb", "\ud800", "a\ue000", "a\u0378", "a\u2028", "a\u2029", None, 5, ["a"]]
        for name in bad:
            with self.subTest(name=name), self.assertRaises(hs.HandshakeError) as cm:
                hs.validate_name(name)
            self.assertEqual(cm.exception.reason, "bad name")

    def test_default_name(self):
        self.assertEqual(hs.default_name(HEADSET.spki_der), "headset-" + identity.fingerprint(HEADSET.spki_der)[:4])

    def test_base64_is_strict_and_canonical(self):
        good = base64.b64encode(b"A" * 32).decode()
        self.assertEqual(hs._b64({"k": good}, "k", exact=32), b"A" * 32)
        for label, text in {"whitespace": good[:8] + " " + good[8:], "newline": good + "\n", "no padding": good.rstrip("="),
                            "url-safe": good.replace("+", "-").replace("/", "_") + "-",
                            "trailing bits": "QR==", "extra padding": good + "=", "non-ascii": good[:-2] + "\u00e9="}.items():
            with self.subTest(label), self.assertRaises(hs.HandshakeError):
                hs._b64({"k": text}, "k")
        with self.assertRaises(hs.HandshakeError):
            hs._b64({"k": "QR=="}, "k", maximum=8)  # decodes to one byte, but not canonically

    def test_transcripts_bind_every_field(self):
        base = dict(na=b"a" * 32, nh=b"h" * 32, spki_a=b"A", spki_h=b"H", mode="pair", name="n", offer_sdp="o", fp_offer="x")
        t = hs.transcript_agent(**base)
        for k, v in dict(na=b"b" * 32, nh=b"i" * 32, spki_a=b"B", spki_h=b"I", mode="paired", name="m", offer_sdp="p",
                         fp_offer="y").items():
            self.assertNotEqual(t, hs.transcript_agent(**{**base, k: v}), k)
        hb = dict(base, answer_sdp="r", fp_answer="z")
        th = hs.transcript_headset(**hb)
        self.assertNotEqual(th, t)
        for k, v in dict(answer_sdp="s", fp_answer="w", offer_sdp="p", mode="paired", name="m").items():
            self.assertNotEqual(th, hs.transcript_headset(**{**hb, k: v}), k)

    def test_transcript_layout(self):
        t = hs.transcript_agent(b"n" * 32, b"m" * 32, b"A", b"H", "pair", "nm", "o", "fp")
        self.assertTrue(t.startswith(hs.enc("spatiallauncher-link-v1") + hs.enc("agent") + hs.enc(b"n" * 32)))
        self.assertTrue(t.endswith(hs.enc("fp")))

    def test_check_spki(self):
        hs.check_spki(AGENT.spki_der)
        small = rsa.generate_private_key(public_exponent=65537, key_size=1024)
        for bad in (b"", b"junk", identity.public_der(small), AGENT.spki_der + b"\x00" * 1024):
            with self.assertRaises(hs.HandshakeError):
                hs.check_spki(bad)


# --- Round trips and refusals ----------------------------------------------------------------

class RoundTripTests(HandshakeCase):
    async def test_paired(self):
        agent, headset = await self.session(**PAIRED)
        self.assertEqual((agent.mode, agent.answer_sdp, agent.headset_spki, agent.name),
                         ("paired", sdp(FP_ANSWER), HEADSET.spki_der, "Test Headset"))
        self.assertEqual((headset.mode, headset.offer_sdp, headset.agent_spki),
                         ("paired", sdp(FP_OFFER), AGENT.spki_der))

    async def test_agent_frames_carry_only_the_expected_fields(self):
        agent, headset = await self.session(**PAIRED)
        self.assertIsInstance(agent, hs.AgentResult)
        frames = self.sent.frames()
        self.assertEqual([f["type"] for f in frames], ["hello", "offer", "done"])
        allowed = {"hello": {"v", "type", "nonce", "spki"}, "offer": {"v", "type", "sdp", "sig"},
                   "done": {"v", "type", "sig"}}
        for f in frames:
            self.assertEqual(set(f), allowed[f["type"]])

    async def test_unpinned_headset_refused(self):
        agent, headset = await self.session(pinned_agents=[AGENT.spki_der])
        self.assertRefused(agent, reason="not paired")
        self.assertIsInstance(headset, hs.HandshakeError)
        self.assertEqual(self.store.list(), [])

    async def test_unknown_laptop_refused_by_headset(self):
        agent, headset = await self.session(pin_headset=True)
        self.assertRefused(headset, agent, reason="unknown laptop")

    async def test_on_path_name_rewrite_is_refused_by_signature(self):
        agent, headset = await self.session(**PAIRED, mutate=edit("h2a", "hello", name="Evil Headset"))
        self.assertRefused(agent, headset, reason="bad signature")

    async def test_name_is_normalised(self):
        for given, expected in (("  Desk  ", "Desk"), ("   ", "headset-" + identity.fingerprint(HEADSET.spki_der)[:4])):
            with self.subTest(given=given):
                self.store.add("x", HEADSET.spki_der)
                port, results = await self.start_headset(pinned=[AGENT.spki_der], name=given)
                agent = await self.run_agent(port)
                await results.get()
                self.assertIsInstance(agent, hs.AgentResult)
                self.assertEqual(agent.name, expected)

    async def test_pair_mode_is_unsupported(self):
        for mode in ("pair", "other", None):
            with self.subTest(mode=mode):
                agent, headset = await self.session(**PAIRED, mutate=edit("h2a", "hello", mode=mode))
                self.assertRefused(agent, reason="unsupported mode")
                self.assertEqual(self.store.list()[0].public_key, HEADSET.spki_der)

    async def test_headset_refuses_to_send_a_bad_name(self):
        port, results = await self.start_headset(pinned=[AGENT.spki_der], name="bad\tname")
        self.store.add("x", HEADSET.spki_der)
        agent = await self.run_agent(port)
        headset = await results.get()
        self.assertRefused(headset, reason="bad name")
        self.assertIsInstance(agent, hs.HandshakeError)

    async def test_agent_refuses_a_bad_wire_name(self):
        self.store.add("Test Headset", HEADSET.spki_der)
        for bad in ("x" * 33, "a\u200bb", None, 5):
            with self.subTest(name=bad):
                agent, headset = await self.session(**PAIRED, mutate=edit("h2a", "hello", name=bad))
                self.assertRefused(agent, reason="bad name")

    async def test_agent_refuses_nonce_reuse(self):
        async def evil(reader, writer):
            m1 = await recv(reader)
            await send(writer, {"v": 1, "type": "hello", "nonce": m1["nonce"], "spki": b64(HEADSET.spki_der),
                                "mode": "paired", "name": "x"})
            writer.close()
        self.store.add("x", HEADSET.spki_der)
        agent = await self.run_agent(await self.serve(evil))
        self.assertRefused(agent, reason="replayed nonce")
        self.assertTrue(agent.auth)


class SignatureTests(HandshakeCase):
    async def test_bad_offer_signature(self):
        agent, headset = await self.session(**PAIRED, mutate=edit("a2h", "offer", sig=b64(os.urandom(256))))
        self.assertRefused(headset, agent, reason="bad signature")

    async def test_bad_answer_signature(self):
        agent, headset = await self.session(**PAIRED, mutate=edit("h2a", "answer", sig=b64(os.urandom(256))))
        self.assertRefused(agent, headset, reason="bad signature")

    async def test_bad_done_signature(self):
        agent, headset = await self.session(**PAIRED, mutate=edit("a2h", "done", sig=b64(os.urandom(256))))
        self.assertRefused(headset, reason="bad signature")

    async def test_signature_by_another_key(self):
        sig = b64(identity.sign(OTHER.key, b"anything"))
        agent, headset = await self.session(**PAIRED, mutate=edit("a2h", "offer", sig=sig))
        self.assertRefused(headset, reason="bad signature")

    async def test_wrong_length_signature(self):
        agent, headset = await self.session(**PAIRED, mutate=edit("h2a", "answer", sig=b64(b"short")))
        self.assertRefused(agent, reason="malformed sig")


class TamperTests(HandshakeCase):
    """Anything changed in flight must break a signature."""

    async def test_tamper_each_field(self):
        def nonce(direction):
            def mutate(d, m):
                return {**m, "nonce": b64(os.urandom(32))} if d == direction and m["type"] == "hello" else m
            return mutate

        cases = {
            "na": nonce("a2h"),
            "nh": nonce("h2a"),
            "offer sdp": edit("a2h", "offer", sdp=sdp(FP_OFFER, "a=x:1")),
            "answer sdp": edit("h2a", "answer", sdp=sdp(FP_ANSWER, "a=x:1")),
            "offer fingerprint": edit("a2h", "offer", sdp=sdp(FP_ANSWER)),
            "answer fingerprint": edit("h2a", "answer", sdp=sdp(FP_OFFER)),
        }
        for name, mutate in cases.items():
            with self.subTest(field=name):
                self.store = pairing.PairStore(Path(tempfile.mkdtemp(dir=self._tmp.name)))
                agent, headset = await self.session(**PAIRED, mutate=mutate)
                self.assertIsInstance(agent, hs.HandshakeError)
                self.assertIsInstance(headset, hs.HandshakeError)
                self.assertTrue((agent.reason == "bad signature" and not agent.remote)
                                or (headset.reason == "bad signature" and not headset.remote))

    async def test_tamper_spki_a(self):
        # The headset has pinned both laptops, so only the transcript can tell the swap apart.
        agent, headset = await self.session(pin_headset=True, pinned_agents=[AGENT.spki_der, OTHER.spki_der],
                                            mutate=edit("a2h", "hello", spki=b64(OTHER.spki_der)))
        self.assertRefused(headset, reason="bad signature")

    async def test_tamper_spki_h(self):
        self.store.add("Other", OTHER.spki_der)
        agent, headset = await self.session(**PAIRED, mutate=edit("h2a", "hello", spki=b64(OTHER.spki_der)))
        self.assertRefused(agent, headset, reason="bad signature")

    async def test_replayed_m2_m4_refused(self):
        port, results = await self.start_headset(pinned=[AGENT.spki_der])
        relay = Relay(port)
        self.store.add("Test Headset", HEADSET.spki_der)
        first = await self.run_agent(await relay.start())
        await results.get()
        await relay.close()
        self.assertIsInstance(first, hs.AgentResult)
        m2 = next(m for d, m in relay.log if d == "h2a" and m["type"] == "hello")
        m4 = next(m for d, m in relay.log if d == "h2a" and m["type"] == "answer")

        async def replay(reader, writer):
            await recv(reader)
            await send(writer, m2)
            await recv(reader)
            await send(writer, m4)
            try:
                await reader.read()
            finally:
                writer.close()
        second = await self.run_agent(await self.serve(replay))  # same offer SDP: only na differs
        self.assertRefused(second, reason="bad signature")
        self.assertTrue(second.auth)


class NoMacTests(HandshakeCase):
    async def test_a_mac_field_in_the_answer_is_ignored(self):
        agent, headset = await self.session(**PAIRED, mutate=edit("h2a", "answer", mac=b64(os.urandom(32))))
        self.assertIsInstance(agent, hs.AgentResult)  # signed fields are what count; unknown fields are ignored


class SdpTests(HandshakeCase):
    async def test_bad_offer_sdp_never_sent(self):
        self.store.add("Test Headset", HEADSET.spki_der)
        port, results = await self.start_headset(pinned=[AGENT.spki_der])

        async def const(value):
            return value
        for bad in (sdp(None), sdp(FP_OFFER, "a=fingerprint:sha-256 " + FP_ANSWER), sdp(None, SHA1_LINE)):
            agent = await self.run_agent(port, make_offer=lambda bad=bad: const(bad))
            self.assertIsInstance(agent, hs.HandshakeError)
            self.assertNotIn("offer", [f["type"] for f in self.sent.frames()])
            await results.get()

    async def test_bad_answer_sdp_refused_by_agent(self):
        for bad in (sdp(None), sdp(FP_ANSWER, "a=fingerprint:sha-256 " + FP_OFFER), sdp(None, SHA1_LINE)):
            with self.subTest(sdp=bad):
                agent, headset = await self.session(**PAIRED, mutate=edit("h2a", "answer", sdp=bad))
                self.assertIsInstance(agent, hs.HandshakeError)
                self.assertFalse(agent.remote)

    async def test_bad_answer_sdp_refused_by_headset(self):
        async def answer(offer):
            return sdp(None)
        self.store.add("Test Headset", HEADSET.spki_der)
        port, results = await self.start_headset(pinned=[AGENT.spki_der], make_answer=answer)
        agent = await self.run_agent(port)
        self.assertRefused(await results.get(), agent, reason="sdp needs exactly one fingerprint")

    async def test_bad_offer_sdp_refused_by_headset(self):
        for bad in (sdp(None), sdp(None, SHA1_LINE)):
            with self.subTest(sdp=bad):
                agent, headset = await self.session(**PAIRED, mutate=edit("a2h", "offer", sdp=bad))
                self.assertIsInstance(headset, hs.HandshakeError)
                self.assertFalse(headset.remote)

    async def test_fingerprint_edited_after_signing(self):
        agent, headset = await self.session(**PAIRED, mutate=edit("a2h", "offer", sdp=sdp(FP_ANSWER)))
        self.assertRefused(headset, reason="bad signature")

    async def test_oversized_sdp(self):
        async def big():
            return sdp(FP_OFFER, "a=x:" + "y" * hs.MAX_SDP)
        self.store.add("Test Headset", HEADSET.spki_der)
        port, results = await self.start_headset(pinned=[AGENT.spki_der])
        agent = await self.run_agent(port, make_offer=big)
        self.assertRefused(agent, reason="sdp too large")


# --- Framing ---------------------------------------------------------------------------------

class NullWriter:
    def write(self, d):
        pass

    async def drain(self):
        pass

    def close(self):
        pass

    async def wait_closed(self):
        pass


def feed(*chunks: bytes) -> asyncio.StreamReader:
    r = asyncio.StreamReader()
    for c in chunks:
        r.feed_data(c)
    return r


def frame(obj) -> bytes:
    body = obj if isinstance(obj, bytes) else json.dumps(obj).encode("utf-8")
    return struct.pack(">I", len(body)) + body


class FramingTests(unittest.IsolatedAsyncioTestCase):
    async def read(self, *chunks, expected="hello", timeout=0.2, max_frames=6):
        return await hs.Channel(feed(*chunks), NullWriter(), timeout, max_frames).read(expected)

    async def refused(self, reason, *chunks, **kw):
        with self.assertRaises(hs.HandshakeError) as cm:
            await self.read(*chunks, **kw)
        self.assertEqual(cm.exception.reason, reason)

    async def test_valid_frame_ignores_unknown_fields(self):
        msg = await self.read(frame({"v": 1, "type": "hello", "extra": [1]}))
        self.assertEqual(msg["type"], "hello")

    async def test_over_the_cap(self):
        await self.refused("bad frame length", struct.pack(">I", hs.MAX_FRAME + 1))

    async def test_at_the_cap_is_allowed(self):
        body = json.dumps({"v": 1, "type": "hello"}).encode()
        body += b" " * (hs.MAX_FRAME - len(body))
        self.assertEqual((await self.read(frame(body)))["type"], "hello")

    async def test_zero_length(self):
        await self.refused("bad frame length", struct.pack(">I", 0))

    async def test_read_timeout(self):
        await self.refused("read timeout")

    async def test_truncated_body_times_out(self):
        await self.refused("read timeout", struct.pack(">I", 50) + b"{")

    async def test_closed_early(self):
        r = feed(b"\x00\x00")
        r.feed_eof()
        with self.assertRaises(hs.HandshakeError) as cm:
            await hs.Channel(r, NullWriter(), 1).read("hello")
        self.assertEqual(cm.exception.reason, "connection closed")

    async def test_not_json(self):
        await self.refused("malformed frame", frame(b"not json"))
        await self.refused("malformed frame", frame(b"\xff\xfe"))
        await self.refused("malformed frame", frame([1, 2]))

    async def test_deep_nesting_is_malformed_not_a_crash(self):
        await self.refused("malformed frame", frame(b"[" * 60000))
        await self.refused("malformed frame", frame(b'{"v":1,"type":"hello","x":' + b"[" * 60000))
        await self.refused("malformed frame", frame(b'{"a":' * 12000))

    async def test_depth_limit_is_64(self):
        def nested(n):
            return {"v": 1, "type": "hello", "x": json.loads("[" * n + "]" * n) if n else 0}
        self.assertEqual((await self.read(frame(nested(63))))["type"], "hello")  # object + 63 arrays = depth 64
        await self.refused("malformed frame", frame(nested(64)))
        await self.refused("malformed frame", frame(nested(200)))

    async def test_nan_and_huge_numbers_are_malformed(self):
        await self.refused("malformed frame", frame(b'{"v":1,"type":"hello","x":NaN}'))
        await self.refused("malformed frame", frame(b'{"v":1,"type":"hello","x":-Infinity}'))
        await self.refused("malformed frame", frame(b'{"v":1,"type":"hello","x":' + b"9" * 6000 + b"}"))

    async def test_v_must_be_the_integer_one(self):
        for raw in (b'{"v":1.0,"type":"hello"}', b'{"v":1e0,"type":"hello"}', b'{"v":"1","type":"hello"}',
                    b'{"v":true,"type":"hello"}', b'{"v":null,"type":"hello"}', b'{"v":[1],"type":"hello"}'):
            with self.subTest(raw=raw):
                await self.refused("unsupported version", frame(raw))
        self.assertEqual((await self.read(frame(b'{"v":1,"type":"hello"}')))["type"], "hello")

    async def test_missing_v_or_type(self):
        await self.refused("unsupported version", frame({"type": "hello"}))
        await self.refused("unsupported version", frame({"v": 2, "type": "hello"}))
        await self.refused("unsupported version", frame({"v": True, "type": "hello"}))
        await self.refused("malformed frame", frame({"v": 1}))
        await self.refused("malformed frame", frame({"v": 1, "type": 5}))

    async def test_unexpected_type(self):
        await self.refused("unexpected message", frame({"v": 1, "type": "offer"}))

    async def test_peer_error(self):
        with self.assertRaises(hs.HandshakeError) as cm:
            await self.read(frame({"v": 1, "type": "error", "reason": "unknown laptop"}))
        self.assertTrue(cm.exception.remote)
        self.assertIn("unknown laptop", cm.exception.reason)

    async def test_too_many_frames(self):
        ch = hs.Channel(feed(*[frame({"v": 1, "type": "hello"})] * 3), NullWriter(), 1, max_frames=2)
        await ch.read("hello")
        await ch.write("hello")
        with self.assertRaises(hs.HandshakeError) as cm:
            await ch.read("hello")
        self.assertEqual(cm.exception.reason, "too many frames")

    async def test_write_cap(self):
        with self.assertRaises(hs.HandshakeError):
            await hs.Channel(feed(), NullWriter(), 1).write("offer", sdp="x" * hs.MAX_FRAME)

    async def test_agent_refuses_oversized_frame_from_headset(self):
        async def garbage(reader, writer):
            await recv(reader)
            writer.write(struct.pack(">I", hs.MAX_FRAME + 1))
            await writer.drain()
            writer.close()
        server = await asyncio.start_server(garbage, "127.0.0.1", 0)
        reader, writer = await asyncio.open_connection("127.0.0.1", server.sockets[0].getsockname()[1])
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(hs.HandshakeError) as cm:
                await hs.agent_handshake(reader, writer, AGENT, pairing.PairStore(Path(tmp)), None)
        self.assertEqual(cm.exception.reason, "bad frame length")
        server.close()
        await server.wait_closed()


# --- Limits ----------------------------------------------------------------------------------

class LimitTests(HandshakeCase):
    async def test_slow_client_is_dropped(self):
        results: asyncio.Queue = asyncio.Queue()
        gate = hs.HeadsetGate()

        async def handle(reader, writer):
            try:
                r = await hs.serve_headset_connection(reader, writer, gate, HEADSET, "H", [], None,
                                                      read_timeout=5, total_timeout=0.3)
            except Exception as e:
                r = e
            results.put_nowait(r)
        reader, writer = await asyncio.open_connection("127.0.0.1", await self.serve(handle))
        writer.write(struct.pack(">I", 100) + b"{")  # promises 100 bytes, delivers one, then stalls
        await writer.drain()
        err = await asyncio.wait_for(results.get(), 3)
        self.assertEqual(err.reason, "handshake timeout")
        data = await asyncio.wait_for(reader.read(), 3)  # the headset closed TCP, after an error frame
        self.assertIn(b"handshake timeout", data)
        writer.close()

    async def test_headset_pre_auth_deadline_is_short(self):
        async def slow_offer():
            await asyncio.sleep(0.8)
            return sdp(FP_OFFER)
        self.store.add("Test Headset", HEADSET.spki_der)
        port, results = await self.start_headset(pinned=[AGENT.spki_der], total_timeout=0.3, authed_timeout=3)
        agent = await self.run_agent(port, make_offer=slow_offer)
        headset = await results.get()
        self.assertIsInstance(agent, hs.HandshakeError)
        self.assertRefused(headset, reason="handshake timeout")

    async def test_headset_budget_extends_after_the_signature_verifies(self):
        async def slow_answer(offer):
            await asyncio.sleep(0.9)  # longer than total_timeout, shorter than authed_timeout
            return sdp(FP_ANSWER)
        self.store.add("Test Headset", HEADSET.spki_der)
        port, results = await self.start_headset(pinned=[AGENT.spki_der], make_answer=slow_answer,
                                                 total_timeout=0.5, authed_timeout=5)
        agent = await self.run_agent(port)
        headset = await results.get()
        self.assertIsInstance(agent, hs.AgentResult)
        self.assertIsInstance(headset, hs.HeadsetResult)

    async def test_headset_authed_deadline_is_total(self):
        async def stuck_answer(offer):
            await asyncio.sleep(10)
            return sdp(FP_ANSWER)
        self.store.add("Test Headset", HEADSET.spki_der)
        port, results = await self.start_headset(pinned=[AGENT.spki_der], make_answer=stuck_answer,
                                                 total_timeout=0.5, authed_timeout=1.2)
        loop = asyncio.get_running_loop()
        start = loop.time()
        agent = await self.run_agent(port)
        headset = await results.get()
        self.assertRefused(headset, reason="handshake timeout")
        self.assertLess(loop.time() - start, 3)  # measured from the start, not from the verification

    async def test_agent_total_timeout(self):
        async def silent(reader, writer):
            await recv(reader)
            await asyncio.sleep(0.6)
        agent = await self.run_agent(await self.serve(silent), read_timeout=5, total_timeout=0.2)
        self.assertRefused(agent, reason="handshake timeout")

    async def test_agent_read_timeout(self):
        async def silent(reader, writer):
            await recv(reader)
            await asyncio.sleep(0.6)
        agent = await self.run_agent(await self.serve(silent), read_timeout=0.2, total_timeout=5)
        self.assertRefused(agent, reason="read timeout")

    def test_rate_limit(self):
        clock = FakeClock()
        gate = hs.HeadsetGate(clock)
        for _ in range(5):
            self.assertTrue(gate.enter("10.0.0.1"))
            gate.leave("10.0.0.1")
        self.assertFalse(gate.enter("10.0.0.1"))
        self.assertTrue(gate.enter("10.0.0.2"))  # another source is unaffected
        clock.t += 12.1  # one token refills every 12 s
        self.assertTrue(gate.enter("10.0.0.1"))

    def test_one_handshake_per_ip(self):
        gate = hs.HeadsetGate(FakeClock())
        self.assertTrue(gate.enter("10.0.0.1"))
        self.assertFalse(gate.enter("10.0.0.1"))
        gate.leave("10.0.0.1")
        self.assertTrue(gate.enter("10.0.0.1"))

    async def test_server_rate_limits_connections(self):
        outcomes: list = []
        gate = hs.HeadsetGate(FakeClock())

        async def handle(reader, writer):
            try:
                outcomes.append(await hs.serve_headset_connection(reader, writer, gate, HEADSET, "H", [], None))
            except hs.HandshakeError as e:
                outcomes.append(e.reason)
        port = await self.serve(handle)
        for n in range(6):
            _, w = await asyncio.open_connection("127.0.0.1", port)
            w.close()
            while len(outcomes) < n + 1:
                await asyncio.sleep(0.01)
        self.assertEqual(outcomes[:5], ["connection closed"] * 5)
        self.assertEqual(outcomes[5], "rate limited")

    async def test_block_after_auth_failure(self):
        connections = []

        async def evil(reader, writer):
            connections.append(1)
            m1 = await recv(reader)
            await send(writer, {"v": 1, "type": "hello", "nonce": m1["nonce"], "spki": b64(HEADSET.spki_der),
                                "mode": "paired", "name": "x"})
            writer.close()
        port = await self.serve(evil)
        self.store.add("x", HEADSET.spki_der)
        clock = FakeClock()
        blocklist = hs.AuthBlocklist(clock)
        limiter = hs.HandshakeLimiter()

        async def attempt():
            async def offer():
                return sdp()
            return await hs.agent_connect("127.0.0.1", port, AGENT, self.store, offer,
                                          limiter=limiter, blocklist=blocklist)
        with self.assertRaises(hs.HandshakeError) as cm:
            await attempt()
        self.assertEqual(cm.exception.reason, "replayed nonce")
        with self.assertRaises(hs.HandshakeError) as cm:
            await attempt()
        self.assertIn("blocked", cm.exception.reason)
        self.assertEqual(len(connections), 1)  # the second attempt never connected
        clock.t += 61
        with self.assertRaises(hs.HandshakeError) as cm:
            await attempt()
        self.assertEqual(cm.exception.reason, "replayed nonce")
        self.assertEqual(len(connections), 2)

    def test_blocklist_is_per_address_and_port(self):
        clock = FakeClock()
        b = hs.AuthBlocklist(clock)
        b.block(("1.2.3.4", 1))
        self.assertTrue(b.blocked(("1.2.3.4", 1)))
        self.assertFalse(b.blocked(("1.2.3.4", 2)))
        clock.t += 59
        self.assertTrue(b.blocked(("1.2.3.4", 1)))
        clock.t += 2
        self.assertFalse(b.blocked(("1.2.3.4", 1)))

    async def test_network_failures_do_not_block(self):
        blocklist = hs.AuthBlocklist(FakeClock())
        with self.assertRaises(hs.HandshakeError) as cm:
            await hs.agent_connect("127.0.0.1", 1, AGENT, self.store, None,
                                   limiter=hs.HandshakeLimiter(), blocklist=blocklist, connect_timeout=1)
        self.assertEqual(cm.exception.reason, "can't connect")
        self.assertFalse(blocklist.blocked(("127.0.0.1", 1)))

    async def test_limiter_caps_concurrency(self):
        limiter = hs.HandshakeLimiter(4)
        running = peak = 0

        async def job():
            nonlocal running, peak
            async with limiter:
                running += 1
                peak = max(peak, running)
                await asyncio.sleep(0.02)
                running -= 1
        await asyncio.gather(*[job() for _ in range(10)])
        self.assertEqual(peak, 4)


if __name__ == "__main__":
    unittest.main()
