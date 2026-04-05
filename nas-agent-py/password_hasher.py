from __future__ import annotations

import base64
import hashlib
import hmac
import secrets
import struct
from dataclasses import dataclass
from enum import Enum


class PasswordVerificationResult(str, Enum):
    FAILED = "Failed"
    SUCCESS = "Success"
    SUCCESS_REHASH_NEEDED = "SuccessRehashNeeded"


class KeyDerivationPrf(int, Enum):
    HMACSHA1 = 0
    HMACSHA256 = 1
    HMACSHA512 = 2


_PRF_MAP = {
    KeyDerivationPrf.HMACSHA1: "sha1",
    KeyDerivationPrf.HMACSHA256: "sha256",
    KeyDerivationPrf.HMACSHA512: "sha512",
}


@dataclass(slots=True)
class AspNetPasswordHasher:
    iteration_count: int = 100_000
    salt_size: int = 16
    subkey_length: int = 32
    prf: KeyDerivationPrf = KeyDerivationPrf.HMACSHA512

    def hash_password(self, password: str) -> str:
        salt = secrets.token_bytes(self.salt_size)
        subkey = hashlib.pbkdf2_hmac(
            _PRF_MAP[self.prf],
            password.encode("utf-8"),
            salt,
            self.iteration_count,
            dklen=self.subkey_length,
        )
        payload = bytearray()
        payload.append(0x01)
        payload.extend(struct.pack(">I", int(self.prf)))
        payload.extend(struct.pack(">I", self.iteration_count))
        payload.extend(struct.pack(">I", len(salt)))
        payload.extend(salt)
        payload.extend(subkey)
        return base64.b64encode(bytes(payload)).decode("ascii")

    def verify_hashed_password(self, hashed_password: str, provided_password: str) -> PasswordVerificationResult:
        if not hashed_password:
            return PasswordVerificationResult.FAILED

        try:
            decoded = base64.b64decode(hashed_password.encode("ascii"), validate=True)
        except Exception:
            return PasswordVerificationResult.FAILED

        if not decoded:
            return PasswordVerificationResult.FAILED

        format_marker = decoded[0]
        if format_marker == 0x00:
            return self._verify_v2(decoded, provided_password)
        if format_marker == 0x01:
            return self._verify_v3(decoded, provided_password)
        return PasswordVerificationResult.FAILED

    def _verify_v2(self, decoded: bytes, provided_password: str) -> PasswordVerificationResult:
        if len(decoded) != 49:
            return PasswordVerificationResult.FAILED

        salt = decoded[1:17]
        expected_subkey = decoded[17:]
        actual_subkey = hashlib.pbkdf2_hmac(
            "sha1",
            provided_password.encode("utf-8"),
            salt,
            1000,
            dklen=32,
        )
        if not hmac.compare_digest(expected_subkey, actual_subkey):
            return PasswordVerificationResult.FAILED
        return PasswordVerificationResult.SUCCESS_REHASH_NEEDED

    def _verify_v3(self, decoded: bytes, provided_password: str) -> PasswordVerificationResult:
        if len(decoded) < 13:
            return PasswordVerificationResult.FAILED

        try:
            prf_value = struct.unpack(">I", decoded[1:5])[0]
            iter_count = struct.unpack(">I", decoded[5:9])[0]
            salt_length = struct.unpack(">I", decoded[9:13])[0]
        except struct.error:
            return PasswordVerificationResult.FAILED

        if salt_length < 16 or len(decoded) < 13 + salt_length + 16:
            return PasswordVerificationResult.FAILED

        salt = decoded[13 : 13 + salt_length]
        expected_subkey = decoded[13 + salt_length :]

        try:
            prf = KeyDerivationPrf(prf_value)
        except ValueError:
            return PasswordVerificationResult.FAILED

        actual_subkey = hashlib.pbkdf2_hmac(
            _PRF_MAP[prf],
            provided_password.encode("utf-8"),
            salt,
            iter_count,
            dklen=len(expected_subkey),
        )
        if not hmac.compare_digest(expected_subkey, actual_subkey):
            return PasswordVerificationResult.FAILED

        if iter_count < self.iteration_count or prf != self.prf:
            return PasswordVerificationResult.SUCCESS_REHASH_NEEDED
        return PasswordVerificationResult.SUCCESS
