"""First-time pairing between the laptop agent (A) and the headset (H) with a one-time code the headset shows. Wire
protocol v2, op "pair". It is separate from the paired-mode handshake in handshake.py (wire v1) and reuses its framing,
encodings, name rules, SPKI checks and error frames. The C# headset mirrors this file; change both together.

The user reads the code off the headset and types it on the laptop (`agent.py --pair`). The code never crosses the
wire. Both sides prove they know it with HMACs under keys stretched from it with PBKDF2, bound to a transcript that
contains both public keys, both nonces and both names, so a man in the middle who does not know the code cannot get
either side to pin his key.

Transport. The headset listens on its usual TCP link port and advertises Bonjour `_spatiallauncher._tcp.local.` with TXT
`v=1` and `m=pair` for as long as a code is active (discovery.browse(pairing=True) finds exactly those). Framing: u32
big-endian length, then a UTF-8 JSON object; MAX_FRAME 64 KiB; every message has "v":2 and "type". Everything in
handshake.py about frames holds here unchanged: the JSON rules, depth limit, strict base64 (canonical, '=' padding),
nonce exactly 32 bytes, spki DER SubjectPublicKeyInfo of at most 1 KiB (RSA 2048, e=65537), sig exactly 256 bytes
RSA-PSS (identity.sign / identity.verify), the name rules (at most 32 code points, none in Cc Cf Cs Co Cn Zl Zp Zs
except U+0020, "bad name" otherwise), enc(x) = u32 BE length || bytes (strings as UTF-8), and error frames
{"v":2,"type":"error","reason":...} followed by close. A "v" other than the integer 2 is "unsupported version".

The code. 12 characters of Crockford base32 (alphabet 0123456789ABCDEFGHJKMNPQRSTVWXYZ, 60 bits) from a CSPRNG, shown
as XXXX-XXXX-XXXX. When typed: spaces and dashes are dropped, case is ignored, I and L read as 1, O reads as 0, U and
anything else outside the alphabet is refused; exactly 12 characters must remain. The canonical form (12 uppercase
alphabet characters, no dashes) is what feeds the key derivation, as ASCII. A code lasts 120 s, works once (it is
consumed by the first correct proof, whatever happens afterwards, including a decline or a lost message), and is
invalidated by 3 wrong proofs in total, from any address. Issuing a new code invalidates the old one.

Messages, in order:
  P1 A->H {type:"pair", nonce: na, spki: spki_a, name: name_a}
       The agent sends name_a = its name .strip()ped and refuses an empty result ("bad name") before sending anything.
       The headset refuses a name_a that is empty after .strip() ("bad name"), checked right after the name rules and
       BEFORE the "not pairing" check, so it costs no strike.
  P2 H->A {type:"pair-hello", nonce: nh, spki: spki_h, name: name_h}
       or error "not pairing" if no code is active (no strike). name_h = name.strip() or "headset-" + fp[:4]
       (handshake.default_name), validated before sending. The agent refuses nh == na ("replayed nonce"), and
       refuses ("name taken") before it sends any proof if name_h would clash in its pair store.
  Both sides now compute
    T_P = enc("spatiallauncher-pair-v2") enc(na) enc(nh) enc(spki_a) enc(spki_h) enc(name_a) enc(name_h)
    K   = PBKDF2-HMAC-SHA256(password = canonical code ASCII, salt = enc("sl-pair-v2") || na || nh,
                             iterations = 100000, dklen = 32)          (enc of the label, raw na and nh)
    k_a = HMAC-SHA256(K, b"agent")        k_h = HMAC-SHA256(K, b"headset")      (the ASCII bytes, no enc)
  P3 A->H {type:"pair-proof", sig: Sign_A(enc("agent") || T_P), mac: HMAC-SHA256(k_a, enc("pair-mac-agent") || T_P)}
       The headset checks, in this order: a code is active and unexpired ("not pairing"); sig verifies with spki_a
       ("bad signature", no strike, and PBKDF2 has not run yet); mac ("wrong code": it counts a strike, the 3rd kills
       the code, then it closes). A right mac consumes the code.
       C# requirements: PBKDF2 must run off the main thread. (a) Once P3's signature has verified, the mac result is
       applied (strike or consume) even if the socket closes, or the exchange times out or is cancelled, during the
       derivation. (b) "The same code is still active" plus the strike or consume is one atomic step under a lock.
       This Python reference runs the derivation inline, deliberately: there is no await between reading P3 and the
       strike/consume, so (a) and (b) hold trivially.
  P4 H->A {type:"pair-proof", sig: Sign_H(enc("headset") || T_P), mac: HMAC-SHA256(k_h, enc("pair-mac-headset") || T_P)}
       The agent verifies sig with spki_h, then mac; a bad one is an error and the agent aborts. It does NOT pin yet.
       The headset now asks the user "Pair with laptop <name_a> (<fingerprint of spki_a, identity.format_fp>)?" and
       waits at most 60 s for the answer.
  P5 H->A {type:"pair-confirm", ok: bool, sig: Sign_H(enc("headset-confirm") || enc(ok ? "yes" : "no") || T_P)}
       ok is false for a decline and for a timeout; nothing is pinned then. On a verified ok=true the agent pins
       (name_h.strip() or the default name, spki_h) with PairStore.add; a clash is "name taken" and nothing is pinned.
  P6 A->H {type:"pair-done", sig: Sign_A(enc("agent-pinned") || T_P)}
       The headset pins (name_a, spki_a) only after P6 verifies, then closes. If P6 is lost the agent has pinned and
       the headset has not: pairing again with a new code fixes it (PairStore.add with the same name and key updates).
  All MAC and signature checks use constant-time comparison / the library verifier. MACs are 32 bytes (base64).

Limits. Agent: connect 5 s, each frame read 10 s except the wait for P5 (70 s), the whole exchange 90 s, at most 8
frames. Headset: frames at most 64 KiB; before P3's mac has verified the whole exchange (and each read) is limited to 5 s,
then 75 s in total; at most 5 attempts per source IP per 60 s and one exchange at a time per source IP (the same
RateLimiter and HeadsetGate as handshake.py). The strike counter belongs to the code, not to an address.

Error reasons. Sent: those of handshake.py (including "missing <field>" / "malformed <field>", where field here also
includes mac; "bad name" also covers a name_a that is empty after strip), plus "not pairing", "wrong code", "name taken",
"malformed ok" and "replayed nonce". Local only: "bad code" (the typed code is unusable), "declined" (the headset's user
said no, or P5 timed out) and the local-only reasons of handshake.py.

Known-answer vectors for K, k_a, k_h and the two MACs are in test_pair_protocol.py.
"""
import asyncio
import hashlib
import hmac
import inspect
import secrets
from dataclasses import dataclass

import identity
import pairing
from handshake import (HandshakeError, Channel, HeadsetGate, NONCE_LEN, MAX_SPKI, SIG_LEN, CONNECT_TIMEOUT,
                       _b64, _b64s, _run, check_spki, default_name, enc, validate_name)

VERSION = 2
PROTOCOL_LABEL = "spatiallauncher-pair-v2"
SALT_LABEL = "sl-pair-v2"
PAIR_ITERATIONS = 100_000
MAX_FRAMES = 8

AGENT_READ_TIMEOUT = 10.0
AGENT_CONFIRM_TIMEOUT = 70.0
AGENT_TOTAL_TIMEOUT = 90.0
HEADSET_PREAUTH_TIMEOUT = 5.0
HEADSET_TOTAL_TIMEOUT = 75.0
CONFIRM_TIMEOUT = 60.0


@dataclass(frozen=True)
class PairResult:
    headset_spki: bytes
    name: str


@dataclass(frozen=True)
class PairHeadsetResult:
    agent_spki: bytes
    name: str


# --- Pure helpers ----------------------------------------------------------------------------

def transcript(na: bytes, nh: bytes, spki_a: bytes, spki_h: bytes, name_a: str, name_h: str) -> bytes:
    return b"".join((enc(PROTOCOL_LABEL), enc(na), enc(nh), enc(spki_a), enc(spki_h), enc(name_a), enc(name_h)))


def derive_keys(code: str, na: bytes, nh: bytes, iterations: int = PAIR_ITERATIONS) -> tuple[bytes, bytes, bytes]:
    """(K, k_a, k_h) for a canonical code."""
    k = hashlib.pbkdf2_hmac("sha256", code.encode("ascii"), enc(SALT_LABEL) + na + nh, iterations, 32)
    return k, hmac.new(k, b"agent", "sha256").digest(), hmac.new(k, b"headset", "sha256").digest()


def agent_sig_message(t_p: bytes) -> bytes:
    return enc("agent") + t_p


def headset_sig_message(t_p: bytes) -> bytes:
    return enc("headset") + t_p


def confirm_message(ok: bool, t_p: bytes) -> bytes:
    return enc("headset-confirm") + enc("yes" if ok else "no") + t_p


def pinned_message(t_p: bytes) -> bytes:
    return enc("agent-pinned") + t_p


def agent_mac(k_a: bytes, t_p: bytes) -> bytes:
    return hmac.new(k_a, enc("pair-mac-agent") + t_p, "sha256").digest()


def headset_mac(k_h: bytes, t_p: bytes) -> bytes:
    return hmac.new(k_h, enc("pair-mac-headset") + t_p, "sha256").digest()


def confirm_prompt(name_a: str, spki_a: bytes) -> str:
    return f"Pair with laptop {name_a.strip()} ({identity.format_fp(identity.fingerprint(spki_a))})?"


def _channel(reader, writer, read_timeout: float) -> Channel:
    return Channel(reader, writer, read_timeout, MAX_FRAMES, VERSION)


# --- Agent (laptop) --------------------------------------------------------------------------

async def agent_pair(reader, writer, ident: identity.Identity, store: pairing.PairStore, code_text: str,
                     name_a: str, *, iterations: int = PAIR_ITERATIONS, read_timeout: float = AGENT_READ_TIMEOUT,
                     confirm_timeout: float = AGENT_CONFIRM_TIMEOUT,
                     total_timeout: float = AGENT_TOTAL_TIMEOUT) -> PairResult:
    """Runs P1-P6 as the connecting agent with the code the user typed. Pins the headset only after the user
    confirmed on the headset (P5). Raises HandshakeError."""
    try:
        code = pairing.parse_code(code_text)
    except ValueError:
        writer.close()  # nothing has been sent
        raise HandshakeError("bad code") from None
    try:
        name_a = validate_name(name_a.strip() if isinstance(name_a, str) else name_a)
        if not name_a:
            raise HandshakeError("bad name")
    except HandshakeError:
        writer.close()
        raise
    ch = _channel(reader, writer, read_timeout)
    body = _agent_body(ch, ident, store, code, name_a, iterations, confirm_timeout)
    return await _run(ch, body, total_timeout)


async def _agent_body(ch: Channel, ident, store, code, name_a, iterations, confirm_timeout):
    na = secrets.token_bytes(NONCE_LEN)
    await ch.write("pair", nonce=_b64s(na), spki=_b64s(ident.spki_der), name=name_a)

    m2 = await ch.read("pair-hello")
    nh = _b64(m2, "nonce", exact=NONCE_LEN)
    spki_h = _b64(m2, "spki", maximum=MAX_SPKI)
    check_spki(spki_h)
    name_h = validate_name(m2.get("name"))
    if hmac.compare_digest(nh, na):
        raise HandshakeError("replayed nonce", auth=True)
    name = name_h.strip() or default_name(spki_h)
    try:
        store.check_can_add(name, spki_h)  # before any proof, so a clash costs nobody a code
    except ValueError:
        raise HandshakeError("name taken") from None

    t_p = transcript(na, nh, ident.spki_der, spki_h, name_a, name_h)
    _, k_a, k_h = derive_keys(code, na, nh, iterations)
    await ch.write("pair-proof", sig=_b64s(identity.sign(ident.key, agent_sig_message(t_p))),
                   mac=_b64s(agent_mac(k_a, t_p)))

    m4 = await ch.read("pair-proof")
    sig = _b64(m4, "sig", exact=SIG_LEN)
    mac = _b64(m4, "mac", exact=32)
    if not identity.verify(spki_h, sig, headset_sig_message(t_p)):
        raise HandshakeError("bad signature", auth=True)
    if not hmac.compare_digest(mac, headset_mac(k_h, t_p)):
        raise HandshakeError("wrong code", auth=True)

    ch.read_timeout = confirm_timeout  # the user answers on the headset
    m5 = await ch.read("pair-confirm")
    ok = m5.get("ok")
    if type(ok) is not bool:
        raise HandshakeError("malformed ok")
    if not identity.verify(spki_h, _b64(m5, "sig", exact=SIG_LEN), confirm_message(ok, t_p)):
        raise HandshakeError("bad signature", auth=True)
    if not ok:
        raise HandshakeError("declined", remote=True)
    try:
        store.add(name, spki_h)
    except ValueError:
        raise HandshakeError("name taken") from None

    await ch.write("pair-done", sig=_b64s(identity.sign(ident.key, pinned_message(t_p))))
    return PairResult(spki_h, name)


async def agent_pair_connect(host: str, port: int, ident, store, code_text: str, name_a: str, *,
                             connect_timeout: float = CONNECT_TIMEOUT, **kw) -> PairResult:
    """Connects to a headset that is showing a code and runs agent_pair."""
    try:
        reader, writer = await asyncio.wait_for(asyncio.open_connection(host, port), connect_timeout)
    except (asyncio.TimeoutError, OSError):
        raise HandshakeError("can't connect") from None
    return await agent_pair(reader, writer, ident, store, code_text, name_a, **kw)


# --- Headset reference role ------------------------------------------------------------------

async def headset_pair(reader, writer, headset_key: identity.Identity, issuer: pairing.CodeIssuer,
                       pinned_agents_add_cb, confirm_cb, name_h: str, *, iterations: int = PAIR_ITERATIONS,
                       preauth_timeout: float = HEADSET_PREAUTH_TIMEOUT, total_timeout: float = HEADSET_TOTAL_TIMEOUT,
                       confirm_timeout: float = CONFIRM_TIMEOUT) -> PairHeadsetResult:
    """Runs P1-P6 as the listening headset.

    confirm_cb(prompt: str) is awaited and returns True if the user agreed; pinned_agents_add_cb(spki_a, name_a)
    is called (sync or async) only after P6 verified. preauth_timeout bounds the exchange, and each read, until P3's
    mac has verified; after that total_timeout bounds the whole exchange. Raises HandshakeError."""
    ch = _channel(reader, writer, preauth_timeout)
    authed = asyncio.Event()
    body = _headset_body(ch, headset_key, issuer, pinned_agents_add_cb, confirm_cb, name_h, iterations, authed,
                         total_timeout, confirm_timeout)
    return await _run(ch, body, preauth_timeout, authed, total_timeout)


async def _headset_body(ch: Channel, headset, issuer, add_cb, confirm_cb, name_h, iterations, authed, total_timeout,
                        confirm_timeout) -> PairHeadsetResult:
    name_h = validate_name(name_h.strip() or default_name(headset.spki_der))
    m1 = await ch.read("pair")
    na = _b64(m1, "nonce", exact=NONCE_LEN)
    spki_a = _b64(m1, "spki", maximum=MAX_SPKI)
    check_spki(spki_a)
    name_a = validate_name(m1.get("name"))
    if not name_a.strip():
        raise HandshakeError("bad name")  # before "not pairing": costs no strike
    if not issuer.active:
        raise HandshakeError("not pairing")

    nh = secrets.token_bytes(NONCE_LEN)
    await ch.write("pair-hello", nonce=_b64s(nh), spki=_b64s(headset.spki_der), name=name_h)
    t_p = transcript(na, nh, spki_a, headset.spki_der, name_a, name_h)

    m3 = await ch.read("pair-proof")
    sig = _b64(m3, "sig", exact=SIG_LEN)
    mac = _b64(m3, "mac", exact=32)
    code = issuer.code
    if code is None:
        raise HandshakeError("not pairing")
    if not identity.verify(spki_a, sig, agent_sig_message(t_p)):  # cheap, and before PBKDF2
        raise HandshakeError("bad signature", auth=True)
    _, k_a, k_h = derive_keys(code, na, nh, iterations)
    if issuer.code != code:  # consumed or replaced by another exchange meanwhile
        raise HandshakeError("not pairing")
    if not hmac.compare_digest(mac, agent_mac(k_a, t_p)):
        issuer.strike()
        raise HandshakeError("wrong code", auth=True)
    issuer.consume()
    ch.read_timeout = total_timeout
    authed.set()

    await ch.write("pair-proof", sig=_b64s(identity.sign(headset.key, headset_sig_message(t_p))),
                   mac=_b64s(headset_mac(k_h, t_p)))

    try:
        ok = bool(await asyncio.wait_for(confirm_cb(confirm_prompt(name_a, spki_a)), confirm_timeout))
    except asyncio.TimeoutError:
        ok = False
    await ch.write("pair-confirm", ok=ok, sig=_b64s(identity.sign(headset.key, confirm_message(ok, t_p))))
    if not ok:
        raise HandshakeError("declined", remote=True)  # remote: the agent has been told already

    m6 = await ch.read("pair-done")
    if not identity.verify(spki_a, _b64(m6, "sig", exact=SIG_LEN), pinned_message(t_p)):
        raise HandshakeError("bad signature", auth=True)
    added = add_cb(spki_a, name_a)
    if inspect.isawaitable(added):
        await added
    return PairHeadsetResult(spki_a, name_a)


async def serve_pair_connection(reader, writer, gate: HeadsetGate, *args, **kw) -> PairHeadsetResult:
    """asyncio.start_server callback body: applies the per-IP gate, then headset_pair(reader, writer, *args, **kw).
    Refused sources are dropped without a reply."""
    peer = writer.get_extra_info("peername")
    source = peer[0] if peer else "?"
    if not gate.enter(source):
        writer.close()
        raise HandshakeError("rate limited")
    try:
        return await headset_pair(reader, writer, *args, **kw)
    finally:
        gate.leave(source)
