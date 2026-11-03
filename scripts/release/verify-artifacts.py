#!/usr/bin/env python3
"""Validate release package structure and write SHA-256 checksums."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import tarfile
import zipfile

from common import RIDS, sha256, version_text
from importlib.util import module_from_spec, spec_from_file_location

# The command-line packaging module has a hyphenated filename.
spec = spec_from_file_location("package_client", Path(__file__).with_name("package-client.py"))
assert spec is not None and spec.loader is not None
packaging = module_from_spec(spec)
spec.loader.exec_module(packaging)


def verify_zip(path: Path, version: str, rid: str, metadata: dict) -> None:
    with zipfile.ZipFile(path) as stream:
        damaged = stream.testzip()
        if damaged is not None:
            raise ValueError(f"Damaged ZIP member: {damaged}")
        names = set(stream.namelist())
        if any(name.startswith("/") or ".." in Path(name).parts for name in names):
            raise ValueError("Unsafe archive path.")
        prefix = "KanbanBoard/" if rid == "win-x64" else "KanbanBoard.app/Contents/MacOS/"
        required = ["Curs.dll", "Curs.runtimeconfig.json", "System.Private.CoreLib.dll", "setup/runtime.json"]
        required += ["KanbanBoard.exe", "coreclr.dll", "hostfxr.dll"] if rid == "win-x64" else ["Curs", "libcoreclr.dylib", "libhostfxr.dylib"]
        if any(prefix + name not in names for name in required):
            raise ValueError(f"Missing self-contained files in {path.name}")
        manifest = json.loads(stream.read(prefix + "setup/runtime.json"))
        expected_url = f"https://github.com/kuum-oss/Curs/releases/download/v{version}/{metadata['archive']}"
        if manifest["apiImage"] != metadata["apiImage"] or manifest["serverArchiveSha256"] != metadata["sha256"] or manifest["serverArchiveUrl"] != expected_url or manifest["version"] != version:
            raise ValueError("Packaged runtime manifest does not match its published server archive.")
        if not re.fullmatch(r"mcr\.microsoft\.com/mssql/server:[^\s]+@sha256:[a-f0-9]{64}", manifest["sqlImage"]):
            raise ValueError("SQL Server image is not pinned to a digest.")
        if any(name.endswith(("/.env", "/ph08-demo.json")) or "/.tools/" in name for name in names):
            raise ValueError("Local state must not be present in release packages.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--version", type=version_text, required=True)
    parser.add_argument("--write-checksums", action="store_true")
    args = parser.parse_args()
    folder = args.directory.resolve()
    metadata = {}
    expected = []
    for arch in ("amd64", "arm64"):
        source = folder / f"server-linux-{arch}.json"
        data = packaging.load_server_metadata(source, args.version, arch)
        archive = folder / data["archive"]
        if not archive.is_file() or sha256(archive) != data["sha256"]:
            raise ValueError(f"Docker archive integrity failed: {archive.name}")
        with tarfile.open(archive, "r:gz") as stream:
            descriptor = stream.extractfile("manifest.json")
            if descriptor is None:
                raise ValueError("Docker archive manifest missing.")
            saved = json.load(descriptor)
            if not any(data["apiImage"] in row.get("RepoTags", []) for row in saved):
                raise ValueError("Docker archive does not contain the required image tag.")
        metadata[arch] = data
        expected.extend([source, archive])
    for rid, suffix in (("win-x64", "windows-x64"), ("osx-arm64", "macos-apple-silicon"), ("osx-x64", "macos-intel")):
        path = folder / f"KanbanBoard-{args.version}-{suffix}.zip"
        verify_zip(path, args.version, rid, metadata[RIDS[rid]])
        expected.append(path)
        native = folder / f"KanbanBoard-{args.version}-{suffix}{'-setup.exe' if rid == 'win-x64' else '.dmg'}"
        if not native.is_file() or native.stat().st_size < 1024:
            raise ValueError(f"Native package missing: {native.name}")
        if rid == "win-x64" and native.read_bytes()[:2] != b"MZ":
            raise ValueError("Windows installer is not a PE executable.")
        expected.append(native)
    # GitHub allows at most 2 GiB per release asset.
    for path in expected:
        if path.stat().st_size >= 2 * 1024**3:
            raise ValueError(f"Release asset exceeds GitHub's size limit: {path.name}")
    checksum = "".join(f"{sha256(path)}  {path.name}\n" for path in sorted(expected))
    if args.write_checksums:
        (folder / "SHA256SUMS").write_text(checksum)
    else:
        if (folder / "SHA256SUMS").read_text() != checksum:
            raise ValueError("Release SHA256SUMS mismatch.")
    print(f"Verified {len(expected)} release assets; all embedded manifests and archive hashes agree.")


if __name__ == "__main__":
    main()
