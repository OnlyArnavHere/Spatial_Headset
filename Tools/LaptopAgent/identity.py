"""The agent's long-term identity: an RSA-2048 key pair kept encrypted for this OS user (DPAPI on Windows).

The private key never leaves identity.key in the agent's home folder. The public key is shared as DER
SubjectPublicKeyInfo (what .NET's ExportSubjectPublicKeyInfo returns) and summarised as a 16-character fingerprint.
Signatures are RSASSA-PKCS1-v1_5 with SHA-256 (what .NET's RSASignaturePadding.Pkcs1 uses; deterministic).
"""
import base64
import hashlib
import os
import re
import tempfile
from dataclasses import dataclass
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa

import hostos

KEY_FILE = "identity.key"
ENTROPY = b"SpatialLauncher.agent.identity.v1"  # the Windows backend uses the same value
_FP_ALPHABET = re.compile(r"^[A-Z2-7]{16}$")


class IdentityError(Exception):
    """identity.key exists but can't be used. It is never regenerated automatically."""


@dataclass(frozen=True)
class Identity:
    key: rsa.RSAPrivateKey
    spki_der: bytes

    @property
    def fingerprint(self) -> str:
        return fingerprint(self.spki_der)


def default_home() -> Path:
    override = os.environ.get("SPATIAL_LAUNCHER_HOME")
    if override:
        return Path(override)
    return hostos.default_home()


def atomic_write(path: Path, data: bytes) -> None:
    """Writes to a temp file in the same folder, fsyncs, then replaces, so a crash never leaves a half-written file."""
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix=path.name + ".", suffix=".tmp")
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def public_der(key: rsa.RSAPrivateKey) -> bytes:
    return key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)


def load_or_create(home: Path) -> Identity:
    path = Path(home) / KEY_FILE
    if path.exists():
        try:
            pkcs8 = hostos.unprotect(path.read_bytes())
            key = serialization.load_der_private_key(pkcs8, password=None)
        except (OSError, ValueError) as e:
            raise IdentityError(f"{path} can't be read ({e}). It is corrupt or belongs to another user or machine. "
                                "Delete it only if you accept re-pairing every headset.") from e
        if not isinstance(key, rsa.RSAPrivateKey):
            raise IdentityError(f"{path} doesn't hold an RSA key.")
        return Identity(key, public_der(key))
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    pkcs8 = key.private_bytes(serialization.Encoding.DER, serialization.PrivateFormat.PKCS8,
                              serialization.NoEncryption())
    atomic_write(path, hostos.protect(pkcs8))
    return Identity(key, public_der(key))


def sign(key: rsa.RSAPrivateKey, data: bytes) -> bytes:
    return key.sign(data, padding.PKCS1v15(), hashes.SHA256())


def verify(spki_der: bytes, sig: bytes, data: bytes) -> bool:
    try:
        pub = serialization.load_der_public_key(spki_der)
        if not isinstance(pub, rsa.RSAPublicKey):
            return False
        pub.verify(sig, data, padding.PKCS1v15(), hashes.SHA256())
        return True
    except Exception:  # InvalidSignature or a malformed key: both mean "not verified"
        return False


def fingerprint(spki_der: bytes) -> str:
    """First 10 bytes of SHA-256(SPKI DER) as unpadded RFC 4648 base32: 16 characters."""
    return base64.b32encode(hashlib.sha256(spki_der).digest()[:10]).decode("ascii")


def format_fp(fp: str) -> str:
    fp = normalize_fp(fp)
    return "-".join(fp[i:i + 4] for i in range(0, 16, 4))


def normalize_fp(text: str) -> str:
    fp = re.sub(r"[\s-]", "", text).upper()
    if not _FP_ALPHABET.match(fp):
        raise ValueError("A fingerprint is 16 characters from A-Z and 2-7 (dashes and spaces allowed).")
    return fp
