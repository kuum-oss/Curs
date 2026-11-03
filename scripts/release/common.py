"""Shared, dependency-free release packaging helpers."""
from __future__ import annotations

import hashlib
import os
from pathlib import Path
import re
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = "kuum-oss/Curs"
RIDS = {"win-x64": "amd64", "osx-arm64": "arm64", "osx-x64": "amd64"}


def version_text(value: str) -> str:
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?", value):
        raise ValueError("Use a semantic release version, for example 0.2.0.")
    return value


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def run(*args: object, **kwargs: object) -> subprocess.CompletedProcess:
    return subprocess.run([str(a) for a in args], cwd=ROOT, check=True, **kwargs)


def dotnet() -> str:
    local = ROOT / ".tools" / "dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
    if local.is_file():
        return str(local)
    executable = shutil.which("dotnet")
    if executable is None:
        raise RuntimeError("The .NET SDK selected by global.json is required to build release packages.")
    return executable


def sql_image() -> str:
    for line in (ROOT / "compose.yaml").read_text().splitlines():
        match = re.fullmatch(r"\s*image:\s*(mcr\.microsoft\.com/mssql/server:[^\s]+)\s*", line)
        if match:
            return match.group(1)
    raise RuntimeError("The SQL Server image is missing from compose.yaml.")


def require_clean_output(path: Path) -> None:
    """Keep arbitrary existing folders outside the packaging scope intact."""
    if path.exists() and any(path.iterdir()):
        raise RuntimeError(f"Output directory is not empty: {path}. Choose a new packaging directory.")
    path.mkdir(parents=True, exist_ok=True)
