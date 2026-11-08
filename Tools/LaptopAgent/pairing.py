"""Pairing: the one-time code the headset shows, and the store of paired headsets.

pairs.json lives in the agent's home folder next to identity.key. A headset is trusted only if its full public key
(SPKI DER) matches a stored pair; the fingerprint is always recomputed, never read back as proof.

A pair can be marked "always allow": the laptop then grants that headset's control requests without its Yes/No box
(approval.py). The mark is found by the full public key too, is off for every new pair, and is cleared with
set_always_allow(..., False), or by revoking the pair.
"""
import base64
import hashlib
import hmac
import json
import re
import secrets
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

import identity

PAIRS_FILE = "pairs.json"
ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"  # Crockford base32: no I, L, O, U
CODE_LEN = 8  # 40 bits: safe because a code lives 120 s and allows 3 wrong tries (see pair_protocol.py)
LEGACY_CODE_LEN = 12  # what headsets before 0.2 show; still accepted when typed
QR_CODE_LEN = 20  # 100 bits: the code inside the laptop's QR, never typed (pair_protocol.py, "QR pairing")
QR_PREFIX = "SLPAIR"
CODE_TTL = 120.0
MAX_WRONG_ATTEMPTS = 3
_CONFUSABLE = str.maketrans("ILO", "110")


# --- One-time pairing code -------------------------------------------------------------------

def new_code(length: int = CODE_LEN) -> str:
    """A fresh canonical code: `length` characters from the Crockford base32 alphabet (5 bits each), from a CSPRNG."""
    return "".join(secrets.choice(ALPHABET) for _ in range(length))


def format_code(code: str) -> str:
    """'ABCDEFGH' -> 'ABCD-EFGH' (a legacy 12-character code gets three groups)."""
    return "-".join(code[i:i + 4] for i in range(0, len(code), 4))


def parse_code(text: str) -> str:
    """What the user typed into the canonical code (8 characters, or 12 from an older headset): spaces and dashes are dropped, case is ignored, I and L
    read as 1, O reads as 0. U (and anything else outside the alphabet) is refused. Raises ValueError."""
    chars = re.sub(r"[\s\-]", "", str(text)).upper().translate(_CONFUSABLE)
    if len(chars) not in (CODE_LEN, LEGACY_CODE_LEN, QR_CODE_LEN) or any(c not in ALPHABET for c in chars):
        raise ValueError(f"A pairing code is {CODE_LEN} characters (XXXX-XXXX): digits and letters, without U.")
    return chars


# --- QR pairing: the laptop shows the code, the headset scans it --------------------------------

def qr_id(code: str) -> str:
    """16 lowercase hex characters naming a QR code without revealing it: the first 8 bytes of
    SHA-256("sl-qr-id:" || code). The armed headset advertises as "SL-" + qr_id, and P1 carries it as "qr"."""
    return hashlib.sha256(b"sl-qr-id:" + code.encode("ascii")).hexdigest()[:16]


def qr_instance(code: str) -> str:
    """The Bonjour instance name of a headset armed with this QR code."""
    return "SL-" + qr_id(code)


@dataclass(frozen=True)
class QrInvite:
    """What the laptop shows: payload is the QR's text, "SLPAIR:<code>:<laptop fingerprint>" (QR alphanumeric
    mode, so the symbol stays small)."""
    code: str
    fingerprint: str
    payload: str

    @property
    def qr_id(self) -> str:
        return qr_id(self.code)


def new_qr_invite(spki_der: bytes) -> QrInvite:
    code = new_code(QR_CODE_LEN)
    fp = identity.fingerprint(spki_der)
    return QrInvite(code, fp, f"{QR_PREFIX}:{code}:{fp}")


def parse_qr(text: str) -> tuple[str, str]:
    """(code, fingerprint) from a scanned QR's text. Raises ValueError. The C# headset mirrors this (PairCode.ParseQr)."""
    parts = str(text).strip().split(":")
    if len(parts) != 3 or parts[0] != QR_PREFIX:
        raise ValueError("not a Spatial Launcher pairing QR code")
    code, fp = parts[1], parts[2]
    if len(code) != QR_CODE_LEN or any(c not in ALPHABET for c in code):
        raise ValueError("bad code in the QR code")
    if not re.fullmatch(r"[A-Z2-7]{16}", fp):
        raise ValueError("bad fingerprint in the QR code")
    return code, fp


class CodeIssuer:
    """The headset's pairing-code policy (the reference for the C# side, used by tests and the fake headset).

    One code at a time. It lasts ttl seconds, is consumed by the first correct proof (whatever happens afterwards),
    and is invalidated by MAX_WRONG_ATTEMPTS wrong proofs in total, from any address. issue() replaces any old code.
    """

    def __init__(self, clock=time.monotonic, ttl: float = CODE_TTL):
        self._clock = clock
        self._ttl = ttl
        self._code: str | None = None
        self._expires = 0.0
        self._wrong = 0
        self._qr_fp: str | None = None

    def issue(self) -> str:
        self._code = new_code()
        self._expires = self._clock() + self._ttl
        self._wrong = 0
        self._qr_fp = None
        return self._code

    def arm_qr(self, qr_text: str) -> str:
        """Replaces any code with the one in a scanned QR. Only the laptop whose key has the QR's fingerprint, asking
        with its qr_id, can use it. Returns the code. Raises ValueError for a QR that isn't ours."""
        code, fp = parse_qr(qr_text)
        self._code = code
        self._expires = self._clock() + self._ttl
        self._wrong = 0
        self._qr_fp = fp
        return code

    @property
    def qr_fingerprint(self) -> str | None:
        """The laptop fingerprint from the scanned QR, or None for a shown code (or none at all)."""
        return self._qr_fp if self.active else None

    @property
    def qr_id(self) -> str | None:
        code = self.code
        return qr_id(code) if code is not None and self._qr_fp is not None else None

    @property
    def active(self) -> bool:
        """True while a code is issued, unexpired, unused and not invalidated."""
        if self._code is not None and self._clock() >= self._expires:
            self._code = None
        return self._code is not None

    @property
    def code(self) -> str | None:
        """The active canonical code, for the headset to show and to derive keys from. None when not active."""
        return self._code if self.active else None

    def correct(self, guess: str) -> bool:
        """Constant-time comparison, no side effects. Used by tests; the pairing exchange uses strike()/consume()."""
        code = self.code
        return code is not None and hmac.compare_digest(str(guess).encode("utf-8"), code.encode("ascii"))

    def strike(self) -> None:
        """A wrong proof: counts, and invalidates the code at MAX_WRONG_ATTEMPTS."""
        if not self.active:
            return
        self._wrong += 1
        if self._wrong >= MAX_WRONG_ATTEMPTS:
            self._code = None

    def consume(self) -> None:
        """The first correct proof: the code is spent."""
        self._code = None

    @property
    def strikes(self) -> int:
        return self._wrong


# --- Pair store ------------------------------------------------------------------------------

@dataclass(frozen=True)
class Pair:
    name: str
    fingerprint: str
    public_key: bytes
    added: str
    always_allow: bool = False


def _utc_iso(now: datetime | None) -> str:
    now = now or datetime.now(timezone.utc)
    if now.tzinfo is not None:
        now = now.astimezone(timezone.utc)
    return now.strftime("%Y-%m-%dT%H:%M:%SZ")


class PairStore:
    def __init__(self, home: Path):
        self.path = Path(home) / PAIRS_FILE

    def _read(self) -> list[Pair]:
        if not self.path.exists():
            return []
        try:
            doc = json.loads(self.path.read_text(encoding="utf-8"))
            if not isinstance(doc, dict) or doc.get("version") != 1:
                raise ValueError("unsupported version")
            return [Pair(p["name"], p["fingerprint"], base64.b64decode(p["public_key"], validate=True), p["added"],
                         p.get("always_allow") is True)
                    for p in doc["pairs"]]
        except (ValueError, KeyError, TypeError) as e:
            raise ValueError(f"{self.path} can't be read ({e}); fix or delete it. It was not changed.") from e

    def _write(self, pairs) -> None:
        doc = {"version": 1, "pairs": [
            {"name": p.name, "fingerprint": p.fingerprint,
             "public_key": base64.b64encode(p.public_key).decode("ascii"), "added": p.added,
             "always_allow": bool(p.always_allow)} for p in pairs]}
        identity.atomic_write(self.path, json.dumps(doc, indent=2).encode("utf-8"))

    def list(self) -> list[Pair]:
        return self._read()

    @staticmethod
    def _check(name: str, spki_der: bytes, pairs) -> None:
        for existing in pairs:
            if existing.public_key == bytes(spki_der) and existing.name.casefold() != name.casefold():
                raise ValueError(f"already paired as {existing.name}")
            if existing.name.casefold() == name.casefold() and existing.public_key != bytes(spki_der):
                raise ValueError(f"A headset named {existing.name!r} is already paired with a different key. "
                                 "Revoke it first.")

    def check_can_add(self, name: str, spki_der: bytes) -> None:
        """Raises ValueError if add(name, spki_der) would refuse. Writes nothing."""
        name = name.strip()
        if not name:
            raise ValueError("A pair needs a name.")
        self._check(name, spki_der, self._read())

    def add(self, name: str, spki_der: bytes, now: datetime | None = None) -> Pair:
        name = name.strip()
        if not name:
            raise ValueError("A pair needs a name.")
        pairs = self._read()
        pair = Pair(name, identity.fingerprint(spki_der), bytes(spki_der), _utc_iso(now))
        self._check(name, spki_der, pairs)
        for i, existing in enumerate(pairs):
            if existing.name.casefold() == name.casefold():
                # same name and (checked above) same key: a re-pair starts again without the always-allow mark
                pairs[i] = Pair(existing.name, pair.fingerprint, pair.public_key, pair.added)
                self._write(pairs)
                return pairs[i]
        pairs.append(pair)
        self._write(pairs)
        return pair

    def set_always_allow(self, who: "str | bytes", value: bool) -> bool:
        """Marks or unmarks a pair, given its name (case-insensitive) or its full public key. False if no pair matches."""
        pairs = self._read()
        for i, p in enumerate(pairs):
            if isinstance(who, (bytes, bytearray)):
                match = hmac.compare_digest(p.public_key, bytes(who))
            else:
                match = p.name.casefold() == str(who).strip().casefold()
            if match:
                if p.always_allow != bool(value):
                    pairs[i] = Pair(p.name, p.fingerprint, p.public_key, p.added, bool(value))
                    self._write(pairs)
                return True
        return False

    def always_allowed(self, spki_der: bytes) -> bool:
        """True only if the pair with exactly this public key is marked always allow."""
        p = pinned(self, spki_der)
        return p is not None and p.always_allow

    def revoke(self, name: str) -> bool:
        pairs = self._read()
        kept = [p for p in pairs if p.name.casefold() != name.strip().casefold()]
        if len(kept) == len(pairs):
            return False
        self._write(kept)
        return True


def pinned(store: PairStore, spki_der: bytes) -> Pair | None:
    """The pair whose full public key equals spki_der, or None. The fingerprint alone is never enough."""
    for p in store.list():
        if hmac.compare_digest(p.public_key, bytes(spki_der)):
            return p
    return None


def check_headset(store: PairStore, spki_der: bytes, expected_name: str | None = None) -> Pair:
    p = pinned(store, spki_der)
    if p is None:
        raise PermissionError("This headset is not paired with this laptop.")
    if expected_name is not None and p.name.casefold() != expected_name.casefold():
        raise PermissionError("This key is paired under a different headset name.")
    return p
