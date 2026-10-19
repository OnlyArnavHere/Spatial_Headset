"""Pairing: the one-time code the headset shows, and the store of paired headsets.

pairs.json lives in the agent's home folder next to identity.key. A headset is trusted only if its full public key
(SPKI DER) matches a stored pair; the fingerprint is always recomputed, never read back as proof.
"""
import base64
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
CODE_LEN = 12  # 60 bits
CODE_TTL = 120.0
MAX_WRONG_ATTEMPTS = 3
_CONFUSABLE = str.maketrans("ILO", "110")


# --- One-time pairing code -------------------------------------------------------------------

def new_code() -> str:
    """A fresh canonical code: CODE_LEN characters from the Crockford base32 alphabet (60 bits), from a CSPRNG."""
    return "".join(secrets.choice(ALPHABET) for _ in range(CODE_LEN))


def format_code(code: str) -> str:
    """'ABCDEFGHJKMN' -> 'ABCD-EFGH-JKMN'."""
    return "-".join(code[i:i + 4] for i in range(0, CODE_LEN, 4))


def parse_code(text: str) -> str:
    """What the user typed into the canonical 12 characters: spaces and dashes are dropped, case is ignored, I and L
    read as 1, O reads as 0. U (and anything else outside the alphabet) is refused. Raises ValueError."""
    chars = re.sub(r"[\s\-]", "", str(text)).upper().translate(_CONFUSABLE)
    if len(chars) != CODE_LEN or any(c not in ALPHABET for c in chars):
        raise ValueError(f"A pairing code is {CODE_LEN} characters: digits and letters, without U.")
    return chars


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

    def issue(self) -> str:
        self._code = new_code()
        self._expires = self._clock() + self._ttl
        self._wrong = 0
        return self._code

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
            return [Pair(p["name"], p["fingerprint"], base64.b64decode(p["public_key"], validate=True), p["added"])
                    for p in doc["pairs"]]
        except (ValueError, KeyError, TypeError) as e:
            raise ValueError(f"{self.path} can't be read ({e}); fix or delete it. It was not changed.") from e

    def _write(self, pairs) -> None:
        doc = {"version": 1, "pairs": [
            {"name": p.name, "fingerprint": p.fingerprint,
             "public_key": base64.b64encode(p.public_key).decode("ascii"), "added": p.added} for p in pairs]}
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
                pairs[i] = Pair(existing.name, pair.fingerprint, pair.public_key, pair.added)
                self._write(pairs)
                return pairs[i]
        pairs.append(pair)
        self._write(pairs)
        return pair

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
