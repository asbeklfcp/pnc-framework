"""Prevent an approved-character ZIP/checksum mismatch from shipping.

The loader checks *both* SHA256 digests before its clean-install transaction.
Validate the exact checked-in byte streams, the source constants, and all
per-file checksums. Run before building/signing a public release.
"""
from __future__ import annotations

import hashlib
from pathlib import Path
import re
import zipfile

ROOT = Path(__file__).resolve().parents[1]
CUSTOM = ROOT / "src/PncCustomEnemies"
CODE = (CUSTOM / "SignedCharacterContent.cs").read_text(encoding="utf-8")
PROJECT = (CUSTOM / "PncCustomEnemies.csproj").read_text(encoding="utf-8")
FOLDER = CUSTOM / "resources"


def source_constant(name: str) -> str:
    matches = re.findall(rf'const string {name}\s*=\s*"([a-f0-9]{{64}})";', CODE)
    assert len(matches) == 1, (name, matches)
    return matches[0]


def hash_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def run() -> None:
    zip_name = "PNC-Public-Characters-0.1.8.zip"
    checks_name = "PNC-Public-Characters-0.1.8-SHA256SUMS.txt"
    assert f'<EmbeddedResource Include="resources/{zip_name}"' in PROJECT
    assert f'<EmbeddedResource Include="resources/{checks_name}"' in PROJECT

    archive_bytes = (FOLDER / zip_name).read_bytes()
    checks_bytes = (FOLDER / checks_name).read_bytes()
    assert hash_bytes(archive_bytes) == source_constant("ZipHash"), "Publisher ZIP digest mismatches actual release"
    assert hash_bytes(checks_bytes) == source_constant("ChecksHash"), "Publisher manifest digest mismatches actual release"

    manifest = {}
    for line in checks_bytes.decode("utf-8").splitlines():
        if not line:
            continue
        assert len(line) >= 68 and line[64:66] == "  ", "Invalid checksum manifest format"
        digest, path = line[:64], line[66:]
        assert re.fullmatch(r"[a-f0-9]{64}", digest)
        assert path.startswith("BepInEx/custom-enemies/")
        pieces = path.removeprefix("BepInEx/custom-enemies/").split("/")
        assert 2 <= len(pieces) <= 6
        assert pieces[0] in ("femboy-witch", "joker-wall")
        assert all(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", part) for part in pieces)
        assert path not in manifest, f"Duplicate checksum manifest item {path}"
        manifest[path] = digest
    assert len(manifest) == 80, f"Expected 80 approved files, found {len(manifest)}"

    with zipfile.ZipFile(FOLDER / zip_name) as archive:
        entries = archive.namelist()
        assert len(entries) == len(set(entries)) == 80, "Duplicate/missing ZIP entry"
        assert set(entries) == set(manifest), "ZIP members differ from manifest"
        for name in entries:
            assert hash_bytes(archive.read(name)) == manifest[name], f"File checksum mismatch {name}"
    print("PASS: character ZIP digest and manifest digest match hardcoded source constants")
    print("PASS: 80/80 approved character payload hashes and package paths match")
    print("PASS: embedded-resource csproj mappings match the audited files")


if __name__ == "__main__":
    run()