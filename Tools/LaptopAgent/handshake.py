"""The signed link handshake between the laptop agent (client) and the headset (server), for headsets that are
already paired. Wire protocol v1. First-time pairing is a separate exchange (pair_protocol.py, wire v2).

The C# headset mirrors this file; change both together. Both roles are implemented here so tests and the fake headset
can run either side.

Transport: the headset listens on TCP (LINK_PORT, 47810 by default) and advertises Bonjour `_spatiallauncher._tcp.local.`
with TXT `v=1` (plus `m=pair` while it is showing a pairing code), under an instance name from a CSPRNG with at least 64 bits: "SL-" + 16 lowercase hex characters
(discovery.random_instance_name). The agent connects, runs the handshake below, then both sides close TCP; the WebRTC
session negotiated in the offer/answer carries everything after that.

Framing: u32 big-endian length, then that many bytes of a UTF-8 JSON object. MAX_FRAME is 64 KiB; a length of 0 or
above the cap closes the connection. Every message has "v":1 and "type". Unknown fields are ignored. Either side may
send {"v":1,"type":"error","reason":...} and close.
JSON: the body must be valid UTF-8 and parse as one JSON object. NaN/Infinity are refused. Nesting depth is at most 64
(the System.Text.Json default; the top-level object is depth 1); anything deeper, or anything that makes the parser
fail in any way, is "malformed frame". "v" must be the JSON integer 1: 1.0, 1e0, "1" and true are all
"unsupported version" (Python's True == 1, so check the type, not just the value). "type" must be a string.

Encodings: binary fields are RFC 4648 standard base64 (alphabet A-Z a-z 0-9 + /) with '=' padding and no whitespace
or other characters; the decoder must be strict and the canonical re-encoding of the decoded bytes must equal the
received text exactly (no non-zero trailing bits). nonce is exactly 32 bytes. spki is DER SubjectPublicKeyInfo, at
most 1 KiB, an RSA key of exactly 2048 bits with e=65537. sig is exactly 256 bytes, RSA-PSS (SHA-256, MGF1-SHA-256,
salt 32; identity.sign / identity.verify). sdp is a string of at most 48 KiB of UTF-8.

Messages, in order (A = agent, H = headset):
  M1 A->H hello  {nonce: na, spki: spki_a}
  M2 H->A hello  {nonce: nh, spki: spki_h, mode: "paired", name: <headset name>}
       The headset answers only if spki_a is pinned; otherwise it sends error "unknown laptop". The only mode is
       "paired" (any other value is "unsupported mode"). The agent accepts it only if spki_h is pinned, otherwise it
       sends error "not paired". The agent refuses nh == na.
  M3 A->H offer  {sdp, sig: Sign_A(T_A)}
  M4 H->A answer {sdp, sig: Sign_H(T_H)}
       The agent verifies sig and then applies the answer.
  M5 A->H done   {sig: Sign_A(enc("agent-done") || T_H)}

Name. The headset computes name = name.strip() or "headset-" + fp[:4] (fp = identity.fingerprint(spki_h), 16
base32 characters), validates it and sends it in M2; it is also signed into both transcripts, so an on-path rewrite
breaks a signature. Rules, applied to the name as it appears on the wire (after validation only U+0020 can be
stripped):
  - a JSON string of at most 32 Unicode code points (count runes / scalar values, not UTF-16 units; a surrogate
    pair is one);
  - no character whose Unicode general category is Cc, Cf, Cs, Co, Cn, Zl or Zp, and none in Zs except U+0020.
    (Use the category, not isprintable / char.IsControl, so both implementations agree. Cn is "unassigned" in
    Python's Unicode database; headsets should send only assigned characters.)
  Otherwise "bad name". A missing or non-string name is also "bad name". The empty string is valid on the wire
  (the agent then stores the default); the reference headset never sends it.

Transcript: enc(x) = u32 BE length || bytes (strings are UTF-8).
  T_A = enc("spatiallauncher-link-v1") enc("agent") enc(na) enc(nh) enc(spki_a) enc(spki_h) enc(mode) enc(name)
        enc(SHA256(offer_sdp)) enc(fpO)
  T_H = enc("spatiallauncher-link-v1") enc("headset") enc(na) enc(nh) enc(spki_a) enc(spki_h) enc(mode) enc(name)
        enc(SHA256(offer_sdp)) enc(SHA256(answer_sdp)) enc(fpO) enc(fpA)
SDP hashes are over the exact UTF-8 bytes sent. fpO and fpA are the DTLS fingerprints of the offer and answer SDP:
exactly one distinct value, as uppercase hex with colons.

SDP and fingerprint line rules (applied to offer and answer alike, in this order):
  1. Characters: the SDP may contain no control character (category Cc: U+0000-U+001F, U+007F-U+009F) and no U+2028 or
     U+2029, except line ends. A line end is CR immediately followed by LF, or a bare LF. A lone CR, NUL, tab, U+0085,
     U+2028, U+2029 and the like mean refuse ("malformed sdp").
  2. Split into lines at each LF, dropping one CR directly before it. Every line that starts with `a=fingerprint:` (no
     leading whitespace; whitespace-led lines are not matched) must match, exactly and case-sensitively except for the
     hex digits, `a=fingerprint:sha-256 ` followed by 32 two-digit hex groups (0-9 A-F a-f) joined by single colons,
     and nothing after: no tabs, extra spaces or trailing spaces. If the token after the colon is not exactly
     `sha-256` the reason is "unsupported fingerprint algorithm"; any other mismatch is "malformed fingerprint".
  3. Uppercase the values (session and media level both count). Zero distinct values or more than one is "sdp needs
     exactly one fingerprint".

Limits. Agent: connect 5 s, each frame read 10 s, whole handshake 30 s, at most 6 frames, at most 4 handshakes at
once, and after an authentication failure that (address, port) is blocked for 60 s. Headset: frames at most 64 KiB, at
most 5 attempts per source IP per 60 s (token bucket) and one handshake at a time per source IP. Headset time limits
have two phases. Pre-authentication, until the headset has verified the M3 signature (the agent is then
authenticated), the whole handshake must finish within 5 s, which also bounds each read. Once M3 verifies, the budget is
extended: make_answer (ICE gathering) and M5 together may take until 30 s after the connection started, and the M5
read may wait for that long. Past either limit: "handshake timeout".

Error reasons. Sent to the peer in {"type":"error","reason":...} (the receiver never echoes them back):
  "bad frame length", "read timeout", "malformed frame", "unsupported version", "unexpected message",
  "too many frames", "handshake timeout", "missing <field>", "malformed <field>" (field is one of nonce, spki, sig,
  sdp), "bad public key", "sdp too large", "malformed sdp", "sdp needs exactly one fingerprint",
  "unsupported fingerprint algorithm", "malformed fingerprint", "bad name", "unsupported mode", "not paired",
  "replayed nonce", "unknown laptop", "bad signature", "no offer", "no answer".
Local only (never sent): "connection closed", "frame too large", "peer refused: <peer's reason, at most 100
characters>", "rate limited", "blocked after a failed authentication", "can't connect".
"""
import asyncio
import base64
import binascii
import hashlib
import hmac
import json
import re
import secrets
import struct
import time
import unicodedata
from dataclasses import dataclass

from cryptography.hazmat.primitives.asymmetric import rsa

import identity
import pairing

VERSION = 1
LINK_PORT = 47810
SERVICE_TYPE = "_spatiallauncher._tcp.local."
MAX_FRAME = 64 * 1024
MAX_SDP = 48 * 1024
MAX_SPKI = 1024
MAX_NAME = 32
NONCE_LEN = 32
SIG_LEN = 256
MAX_FRAMES = 6
PROTOCOL_LABEL = "spatiallauncher-link-v1"

CONNECT_TIMEOUT = 5.0
AGENT_READ_TIMEOUT = 10.0
AGENT_TOTAL_TIMEOUT = 30.0
MAX_CONCURRENT_HANDSHAKES = 4
BLOCK_SECONDS = 60.0

HEADSET_TOTAL_TIMEOUT = 5.0
HEADSET_AUTHED_TIMEOUT = 30.0
MAX_JSON_DEPTH = 64
RATE_ATTEMPTS = 5
RATE_WINDOW = 60.0

_FP_PREFIX = "a=fingerprint:"
_FP_LINE = re.compile(r"a=fingerprint:sha-256 ([0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){31})")
_BAD_NAME_CATEGORIES = frozenset(("Cc", "Cf", "Cs", "Co", "Cn", "Zl", "Zp", "Zs"))


class HandshakeError(Exception):
    """The handshake was refused. reason is short and safe to send to the peer.

    auth marks an authentication failure (bad signature or code proof): the agent then blocks the address for a while.
    remote marks a refusal the peer told us about, which is never echoed back.
    """

    def __init__(self, reason: str, *, auth: bool = False, remote: bool = False):
        super().__init__(reason)
        self.reason = reason
        self.auth = auth
        self.remote = remote


@dataclass(frozen=True)
class AgentResult:
    answer_sdp: str
    headset_spki: bytes
    mode: str
    name: str


@dataclass(frozen=True)
class HeadsetResult:
    offer_sdp: str
    agent_spki: bytes
    mode: str


# --- Pure helpers ----------------------------------------------------------------------------

def enc(x: str | bytes) -> bytes:
    data = x.encode("utf-8") if isinstance(x, str) else bytes(x)
    return struct.pack(">I", len(data)) + data


def sdp_hash(sdp: str) -> bytes:
    return hashlib.sha256(sdp.encode("utf-8")).digest()


def _check_sdp_chars(sdp: str) -> None:
    n = len(sdp)
    for i, ch in enumerate(sdp):
        if ch == "\n":
            continue
        if ch == "\r":
            if i + 1 < n and sdp[i + 1] == "\n":
                continue
            raise HandshakeError("malformed sdp")
        if unicodedata.category(ch) == "Cc" or ch in "\u2028\u2029":
            raise HandshakeError("malformed sdp")


def sdp_fingerprint(sdp: str) -> str:
    """The one DTLS fingerprint in an SDP, as uppercase hex with colons. Raises HandshakeError unless it is a single
    distinct sha-256 value. The exact line rules are in the module docstring."""
    _check_sdp_chars(sdp)
    found: set[str] = set()
    for line in sdp.split("\n"):
        if line.endswith("\r"):
            line = line[:-1]
        if not line.startswith(_FP_PREFIX):
            continue
        m = _FP_LINE.fullmatch(line)
        if not m:
            algorithm, sep, _ = line[len(_FP_PREFIX):].partition(" ")
            if sep and algorithm != "sha-256" and algorithm:
                raise HandshakeError("unsupported fingerprint algorithm")
            raise HandshakeError("malformed fingerprint")
        found.add(m.group(1).upper())
    if len(found) != 1:
        raise HandshakeError("sdp needs exactly one fingerprint")
    return next(iter(found))


def transcript_agent(na: bytes, nh: bytes, spki_a: bytes, spki_h: bytes, mode: str, name: str, offer_sdp: str,
                     fp_offer: str) -> bytes:
    return b"".join((enc(PROTOCOL_LABEL), enc("agent"), enc(na), enc(nh), enc(spki_a), enc(spki_h), enc(mode),
                     enc(name), enc(sdp_hash(offer_sdp)), enc(fp_offer)))


def transcript_headset(na: bytes, nh: bytes, spki_a: bytes, spki_h: bytes, mode: str, name: str, offer_sdp: str,
                       answer_sdp: str, fp_offer: str, fp_answer: str) -> bytes:
    return b"".join((enc(PROTOCOL_LABEL), enc("headset"), enc(na), enc(nh), enc(spki_a), enc(spki_h), enc(mode),
                     enc(name), enc(sdp_hash(offer_sdp)), enc(sdp_hash(answer_sdp)), enc(fp_offer), enc(fp_answer)))


def done_message(t_h: bytes) -> bytes:
    return enc("agent-done") + t_h


def check_spki(spki: bytes) -> None:
    from cryptography.hazmat.primitives import serialization
    if not 0 < len(spki) <= MAX_SPKI:
        raise HandshakeError("bad public key")
    try:
        pub = serialization.load_der_public_key(spki)
    except Exception:
        raise HandshakeError("bad public key") from None
    if (not isinstance(pub, rsa.RSAPublicKey) or pub.key_size != 2048
            or pub.public_numbers().e != 65537):
        raise HandshakeError("bad public key")


def _b64(msg: dict, key: str, exact: int | None = None, maximum: int | None = None) -> bytes:
    value = msg.get(key)
    if not isinstance(value, str):
        raise HandshakeError(f"missing {key}")
    try:
        data = base64.b64decode(value, validate=True)
        canonical = base64.b64encode(data).decode("ascii") == value
    except (binascii.Error, ValueError):
        raise HandshakeError(f"malformed {key}") from None
    if not canonical:
        raise HandshakeError(f"malformed {key}")
    if (exact is not None and len(data) != exact) or (maximum is not None and not 0 < len(data) <= maximum):
        raise HandshakeError(f"malformed {key}")
    return data


def _sdp(msg: dict) -> str:
    sdp = msg.get("sdp")
    if not isinstance(sdp, str):
        raise HandshakeError("missing sdp")
    try:
        size = len(sdp.encode("utf-8"))
    except UnicodeEncodeError:
        raise HandshakeError("malformed sdp") from None
    if not 0 < size <= MAX_SDP:
        raise HandshakeError("sdp too large")
    return sdp


def validate_name(name) -> str:
    """The name rules from the module docstring: at most 32 code points, none in the refused categories."""
    if (not isinstance(name, str) or len(name) > MAX_NAME
            or any(unicodedata.category(c) in _BAD_NAME_CATEGORIES and c != " " for c in name)):
        raise HandshakeError("bad name")
    return name


def default_name(spki_h: bytes) -> str:
    return "headset-" + identity.fingerprint(spki_h)[:4]


def _name(msg: dict) -> str:
    return validate_name(msg.get("name"))


def _b64s(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


# --- Framing ---------------------------------------------------------------------------------

def _refuse_constant(text: str):
    raise ValueError(text)


def _depth(value) -> int:
    """Nesting depth of parsed JSON (a scalar is 0, the top-level object 1), without recursion."""
    deepest = 0
    stack = [(value, 0)]
    while stack:
        item, d = stack.pop()
        if isinstance(item, dict):
            children = item.values()
        elif isinstance(item, list):
            children = item
        else:
            continue
        d += 1
        deepest = max(deepest, d)
        stack.extend((c, d) for c in children)
    return deepest


class Channel:
    """JSON frames over a StreamReader/StreamWriter, with the size cap, per-read timeout and frame budget."""

    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter,
                 read_timeout: float, max_frames: int = MAX_FRAMES, version: int = VERSION):
        self.version = version
        self.reader = reader
        self.writer = writer
        self.read_timeout = read_timeout
        self.max_frames = max_frames
        self.frames = 0

    def _count(self) -> None:
        self.frames += 1
        if self.frames > self.max_frames:
            raise HandshakeError("too many frames")

    async def read(self, expected: str) -> dict:
        self._count()
        try:
            head = await asyncio.wait_for(self.reader.readexactly(4), self.read_timeout)
            (length,) = struct.unpack(">I", head)
            if length == 0 or length > MAX_FRAME:
                raise HandshakeError("bad frame length")
            body = await asyncio.wait_for(self.reader.readexactly(length), self.read_timeout)
        except asyncio.TimeoutError:
            raise HandshakeError("read timeout") from None
        except (asyncio.IncompleteReadError, ConnectionError, OSError):
            raise HandshakeError("connection closed") from None
        try:
            msg = json.loads(body.decode("utf-8"), parse_constant=_refuse_constant)
        except Exception:  # ValueError, UnicodeDecodeError, RecursionError, ...
            raise HandshakeError("malformed frame") from None
        if (not isinstance(msg, dict) or "type" not in msg or not isinstance(msg["type"], str)
                or _depth(msg) > MAX_JSON_DEPTH):
            raise HandshakeError("malformed frame")
        v = msg.get("v")
        if type(v) is not int or v != self.version:
            raise HandshakeError("unsupported version")
        if msg["type"] == "error":
            reason = msg.get("reason")
            reason = reason if isinstance(reason, str) and reason.isprintable() else "?"
            raise HandshakeError(f"peer refused: {reason[:100]}", remote=True)
        if msg["type"] != expected:
            raise HandshakeError("unexpected message")
        return msg

    async def write(self, msg_type: str, **fields) -> None:
        self._count()
        await self.write_raw({"v": self.version, "type": msg_type, **fields})

    async def write_raw(self, msg: dict) -> None:
        body = json.dumps(msg, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        if len(body) > MAX_FRAME:
            raise HandshakeError("frame too large")
        self.writer.write(struct.pack(">I", len(body)) + body)
        try:
            await self.writer.drain()
        except (ConnectionError, OSError):
            raise HandshakeError("connection closed") from None


async def _finish(ch: Channel, error: HandshakeError | None) -> None:
    """Tells the peer why (when we are the one refusing), then closes TCP."""
    if error is not None and not error.remote:
        try:
            await asyncio.wait_for(ch.write_raw({"v": ch.version, "type": "error", "reason": error.reason}), 1.0)
        except Exception:
            pass
    try:
        ch.writer.close()
        await asyncio.wait_for(ch.writer.wait_closed(), 1.0)
    except Exception:
        pass


async def _run(ch: Channel, body, total_timeout: float, authed: asyncio.Event | None = None,
               authed_total: float = 0.0):
    """Runs body within total_timeout. With `authed`, total_timeout only applies until the event is set; after that
    the whole run may last authed_total seconds from the start."""
    error = None
    task = asyncio.ensure_future(body)
    try:
        if authed is None:
            return await asyncio.wait_for(task, total_timeout)
        loop = asyncio.get_running_loop()
        start = loop.time()
        waiter = asyncio.ensure_future(authed.wait())
        try:
            await asyncio.wait({task, waiter}, timeout=total_timeout, return_when=asyncio.FIRST_COMPLETED)
        finally:
            waiter.cancel()
        if task.done():
            return task.result()
        if not authed.is_set():
            raise asyncio.TimeoutError
        return await asyncio.wait_for(task, max(0.0, authed_total - (loop.time() - start)))
    except asyncio.TimeoutError:
        error = HandshakeError("handshake timeout")
        raise error from None
    except HandshakeError as e:
        error = e
        raise
    finally:
        if not task.done():
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)
        await _finish(ch, error)


# --- Agent (client) --------------------------------------------------------------------------

async def agent_handshake(reader, writer, ident: identity.Identity, store: pairing.PairStore,
                          make_offer,
                          *, read_timeout: float = AGENT_READ_TIMEOUT,
                          total_timeout: float = AGENT_TOTAL_TIMEOUT) -> AgentResult:
    """Runs M1-M5 as the connecting agent (paired headsets only). make_offer() is awaited once the headset is acceptable and returns the
    offer SDP; the caller applies the returned answer_sdp only because this function returned."""
    ch = Channel(reader, writer, read_timeout)
    return await _run(ch, _agent_body(ch, ident, store, make_offer), total_timeout)


async def _agent_body(ch: Channel, ident, store, make_offer) -> AgentResult:
    na = secrets.token_bytes(NONCE_LEN)
    await ch.write("hello", nonce=_b64s(na), spki=_b64s(ident.spki_der))

    m2 = await ch.read("hello")
    nh = _b64(m2, "nonce", exact=NONCE_LEN)
    spki_h = _b64(m2, "spki", maximum=MAX_SPKI)
    check_spki(spki_h)
    mode = m2.get("mode")
    wire_name = _name(m2)
    name = wire_name.strip() or default_name(spki_h)
    if hmac.compare_digest(nh, na):
        raise HandshakeError("replayed nonce", auth=True)
    if mode != "paired":
        raise HandshakeError("unsupported mode")
    if pairing.pinned(store, spki_h) is None:
        raise HandshakeError("not paired")

    offer_sdp = await make_offer()
    if not isinstance(offer_sdp, str):
        raise HandshakeError("no offer")
    offer_sdp = _sdp({"sdp": offer_sdp})
    fp_offer = sdp_fingerprint(offer_sdp)
    t_a = transcript_agent(na, nh, ident.spki_der, spki_h, mode, wire_name, offer_sdp, fp_offer)
    await ch.write("offer", sdp=offer_sdp, sig=_b64s(identity.sign(ident.key, t_a)))

    m4 = await ch.read("answer")
    answer_sdp = _sdp(m4)
    sig = _b64(m4, "sig", exact=SIG_LEN)
    fp_answer = sdp_fingerprint(answer_sdp)
    t_h = transcript_headset(na, nh, ident.spki_der, spki_h, mode, wire_name, offer_sdp, answer_sdp, fp_offer,
                             fp_answer)
    if not identity.verify(spki_h, sig, t_h):
        raise HandshakeError("bad signature", auth=True)

    await ch.write("done", sig=_b64s(identity.sign(ident.key, done_message(t_h))))
    return AgentResult(answer_sdp, spki_h, mode, name)


# --- Headset (server) reference role ---------------------------------------------------------

async def headset_handshake(reader, writer, headset: identity.Identity, name: str, pinned_agents,
                            make_answer,
                            *, read_timeout: float = HEADSET_TOTAL_TIMEOUT,
                            total_timeout: float = HEADSET_TOTAL_TIMEOUT,
                            authed_timeout: float = HEADSET_AUTHED_TIMEOUT) -> HeadsetResult:
    """Runs M1-M5 as the listening headset. pinned_agents is an iterable of SPKI DER. make_answer(offer_sdp) is
    awaited only after the offer's signature verified.

    total_timeout (and read_timeout) apply only until the offer signature has verified; from then on the whole
    handshake, including make_answer and the M5 read, may last authed_timeout seconds."""
    ch = Channel(reader, writer, read_timeout)
    authed = asyncio.Event()
    body = _headset_body(ch, headset, name, list(pinned_agents), make_answer, authed, authed_timeout)
    return await _run(ch, body, total_timeout, authed, authed_timeout)


async def _headset_body(ch: Channel, headset, name, pinned_agents, make_answer, authed,
                        authed_timeout) -> HeadsetResult:
    name = validate_name(name.strip() or default_name(headset.spki_der))
    m1 = await ch.read("hello")
    na = _b64(m1, "nonce", exact=NONCE_LEN)
    spki_a = _b64(m1, "spki", maximum=MAX_SPKI)
    check_spki(spki_a)
    if not any(hmac.compare_digest(spki_a, bytes(p)) for p in pinned_agents):
        raise HandshakeError("unknown laptop")
    mode = "paired"

    nh = secrets.token_bytes(NONCE_LEN)
    await ch.write("hello", nonce=_b64s(nh), spki=_b64s(headset.spki_der), mode=mode, name=name)

    m3 = await ch.read("offer")
    offer_sdp = _sdp(m3)
    sig_a = _b64(m3, "sig", exact=SIG_LEN)
    fp_offer = sdp_fingerprint(offer_sdp)
    t_a = transcript_agent(na, nh, spki_a, headset.spki_der, mode, name, offer_sdp, fp_offer)
    if not identity.verify(spki_a, sig_a, t_a):
        raise HandshakeError("bad signature", auth=True)
    ch.read_timeout = authed_timeout
    authed.set()

    answer_sdp = await make_answer(offer_sdp)
    if not isinstance(answer_sdp, str):
        raise HandshakeError("no answer")
    answer_sdp = _sdp({"sdp": answer_sdp})
    fp_answer = sdp_fingerprint(answer_sdp)
    t_h = transcript_headset(na, nh, spki_a, headset.spki_der, mode, name, offer_sdp, answer_sdp, fp_offer, fp_answer)
    await ch.write("answer", sdp=answer_sdp, sig=_b64s(identity.sign(headset.key, t_h)))

    m5 = await ch.read("done")
    if not identity.verify(spki_a, _b64(m5, "sig", exact=SIG_LEN), done_message(t_h)):
        raise HandshakeError("bad signature", auth=True)
    return HeadsetResult(offer_sdp, spki_a, mode)


# --- Agent connection limits -----------------------------------------------------------------

class HandshakeLimiter:
    """At most `limit` handshakes at once: `async with limiter:`."""

    def __init__(self, limit: int = MAX_CONCURRENT_HANDSHAKES):
        self._sem = asyncio.Semaphore(limit)

    async def __aenter__(self):
        await self._sem.acquire()

    async def __aexit__(self, *exc):
        self._sem.release()


class AuthBlocklist:
    """(address, port) pairs blocked for a while after an authentication failure."""

    def __init__(self, clock=time.monotonic, seconds: float = BLOCK_SECONDS):
        self._clock = clock
        self._seconds = seconds
        self._until: dict[tuple[str, int], float] = {}

    def block(self, key: tuple[str, int]) -> None:
        self._until[key] = self._clock() + self._seconds

    def blocked(self, key: tuple[str, int]) -> bool:
        now = self._clock()
        for k in [k for k, t in self._until.items() if t <= now]:
            del self._until[k]
        return key in self._until


async def agent_connect(host: str, port: int, ident, store, make_offer, *,
                        limiter: HandshakeLimiter, blocklist: AuthBlocklist,
                        connect_timeout: float = CONNECT_TIMEOUT, **timeouts) -> AgentResult:
    """Connects to a headset and runs the handshake, within the concurrency limit and the block list."""
    key = (host, port)
    if blocklist.blocked(key):
        raise HandshakeError("blocked after a failed authentication")
    async with limiter:
        try:
            reader, writer = await asyncio.wait_for(asyncio.open_connection(host, port), connect_timeout)
        except (asyncio.TimeoutError, OSError):
            raise HandshakeError("can't connect") from None
        try:
            return await agent_handshake(reader, writer, ident, store, make_offer, **timeouts)
        except HandshakeError as e:
            if e.auth:
                blocklist.block(key)
            raise


# --- Headset connection limits ---------------------------------------------------------------

class RateLimiter:
    """Token bucket per source: `attempts` per `window` seconds."""

    def __init__(self, clock=time.monotonic, attempts: int = RATE_ATTEMPTS, window: float = RATE_WINDOW):
        self._clock = clock
        self._cap = float(attempts)
        self._rate = attempts / window
        self._buckets: dict[str, tuple[float, float]] = {}

    def allow(self, source: str) -> bool:
        now = self._clock()
        tokens, last = self._buckets.get(source, (self._cap, now))
        tokens = min(self._cap, tokens + (now - last) * self._rate)
        allowed = tokens >= 1.0
        if allowed:
            tokens -= 1.0
        self._buckets[source] = (tokens, now)
        if len(self._buckets) > 1024:  # forget sources that have fully recovered
            self._buckets = {k: v for k, v in self._buckets.items()
                             if min(self._cap, v[0] + (now - v[1]) * self._rate) < self._cap}
        return allowed


class HeadsetGate:
    """Per-source-IP pre-authentication limits: a rate limit and one handshake at a time."""

    def __init__(self, clock=time.monotonic, attempts: int = RATE_ATTEMPTS, window: float = RATE_WINDOW):
        self._rate = RateLimiter(clock, attempts, window)
        self._busy: set[str] = set()

    def enter(self, source: str) -> bool:
        if source in self._busy or not self._rate.allow(source):
            return False
        self._busy.add(source)
        return True

    def leave(self, source: str) -> None:
        self._busy.discard(source)


async def serve_headset_connection(reader, writer, gate: HeadsetGate, headset, name, pinned_agents,
                                   make_answer, **timeouts) -> HeadsetResult:
    """asyncio.start_server callback body: applies the gate, then the handshake. Refused sources are dropped
    without a reply (nothing to learn from a flood)."""
    peer = writer.get_extra_info("peername")
    source = peer[0] if peer else "?"
    if not gate.enter(source):
        writer.close()
        raise HandshakeError("rate limited")
    try:
        return await headset_handshake(reader, writer, headset, name, pinned_agents, make_answer, **timeouts)
    finally:
        gate.leave(source)
