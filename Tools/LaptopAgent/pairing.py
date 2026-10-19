"""Pairing: one-time codes, the store of paired headsets, and the QR / typed pairing offer.

pairs.json lives in the agent's home folder next to identity.key. A headset is trusted only if its full public key
(SPKI DER) matches a stored pair; the fingerprint is always recomputed, never read back as proof.
"""
import base64
import hmac
import io
import ipaddress
import json
import re
import secrets
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import parse_qsl, quote

import identity

PAIRS_FILE = "pairs.json"
_B32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
_CODE_RE = re.compile(r"^[A-Z2-7]{8}$")
MAX_WRONG_ATTEMPTS = 5


# --- One-time pairing code -------------------------------------------------------------------

class CodeIssuer:
    """Issues one 8-character code at a time. It works once, expires, and is burned after 5 wrong guesses."""

    def __init__(self, clock=time.monotonic, ttl: float = 300):
        self._clock = clock
        self._ttl = ttl
        self._code: str | None = None
        self._expires = 0.0
        self._wrong = 0

    def issue(self) -> str:
        self._code = "".join(secrets.choice(_B32) for _ in range(8))
        self._expires = self._clock() + self._ttl
        self._wrong = 0
        return self._code

    def redeem(self, code: str) -> bool:
        if self._code is None:
            return False
        if self._clock() >= self._expires:
            self._code = None
            return False
        guess = re.sub(r"[\s-]", "", str(code)).upper()
        if hmac.compare_digest(guess.encode("utf-8"), self._code.encode("ascii")):
            self._code = None
            return True
        return self._wrong_guess()

    @property
    def active(self) -> bool:
        """True while a code is issued, unexpired and not burned."""
        return self._code is not None and self._clock() < self._expires

    def redeem_mac(self, msg: bytes, mac: bytes) -> bool:
        """Checks mac == HMAC-SHA256(key=the code, msg) without the code crossing the wire. Same expiry, single use
        and 5-wrong-attempts burn as redeem."""
        if self._code is None:
            return False
        if self._clock() >= self._expires:
            self._code = None
            return False
        expected = hmac.new(self._code.encode("ascii"), bytes(msg), "sha256").digest()
        if hmac.compare_digest(bytes(mac), expected):
            self._code = None
            return True
        return self._wrong_guess()

    def _wrong_guess(self) -> bool:
        self._wrong += 1
        if self._wrong >= MAX_WRONG_ATTEMPTS:
            self._code = None
        return False


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


# --- Pairing offer (QR and typed) ------------------------------------------------------------

@dataclass(frozen=True)
class PairOffer:
    fingerprint: str
    code: str
    host: str | None = None
    port: int | None = None
    name: str | None = None


def _norm_code(text: str) -> str:
    code = re.sub(r"[\s-]", "", text).upper()
    if not _CODE_RE.match(code):
        raise ValueError("A pairing code is 8 characters from A-Z and 2-7.")
    return code


def _norm_port(value) -> int:
    text = str(value)
    if not text.isascii() or not text.isdigit() or not 1 <= int(text) <= 65535:
        raise ValueError("Port must be 1-65535.")
    return int(text)


def encode_payload(fp: str, code: str, host: str | None = None, port: int | None = None,
                   name: str | None = None) -> str:
    out = f"slpair:1?f={identity.normalize_fp(fp)}&c={_norm_code(code)}"
    if host:
        out += f"&h={ipaddress.ip_address(host)}"
    if port is not None:
        out += f"&p={_norm_port(port)}"
    if name:
        out += f"&n={quote(name, safe='')}"
    return out


def parse_payload(text: str) -> PairOffer:
    text = text.strip()
    prefix = "slpair:1?"
    if not text.startswith(prefix):
        raise ValueError("Not a Spatial Launcher pairing code (wrong scheme or version).")
    fields: dict[str, str] = {}
    for k, v in parse_qsl(text[len(prefix):], keep_blank_values=True, separator="&"):
        fields.setdefault(k, v)
    if "f" not in fields or "c" not in fields:
        raise ValueError("Pairing code is missing the fingerprint or code.")
    host = str(ipaddress.ip_address(fields["h"])) if "h" in fields else None
    port = _norm_port(fields["p"]) if "p" in fields else None
    return PairOffer(identity.normalize_fp(fields["f"]), _norm_code(fields["c"]), host, port, fields.get("n"))


def typed_text(offer: PairOffer) -> str:
    return f"{identity.format_fp(offer.fingerprint)} {offer.code[:4]}-{offer.code[4:]}"


def parse_typed(text: str) -> tuple[str, str]:
    """Reads 'XXXX-XXXX-XXXX-XXXX ABCD-EFGH' (any case, spacing or dashes) into (fingerprint, code)."""
    chars = re.sub(r"[\s-]", "", text).upper()
    if len(chars) != 24:
        raise ValueError("Expected 16 fingerprint characters followed by 8 code characters.")
    return identity.normalize_fp(chars[:16]), _norm_code(chars[16:])


def render_qr(payload: str) -> str:
    import segno
    buf = io.StringIO()
    segno.make(payload, error="m").terminal(out=buf, compact=True, border=2)
    return buf.getvalue()
