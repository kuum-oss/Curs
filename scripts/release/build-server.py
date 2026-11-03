#!/usr/bin/env python3
"""Build one ready-to-load Docker API archive for a GitHub release."""
from __future__ import annotations

import argparse
import gzip
import json
from pathlib import Path
import subprocess
import sys

from common import REPOSITORY, require_clean_output, run, sha256, version_text


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arch", choices=("amd64", "arm64"), required=True)
    parser.add_argument("--version", type=version_text, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    require_clean_output(output)
    image = f"kanbanboard-api:{args.version}-{args.arch}"
    run("docker", "buildx", "build", "--platform", f"linux/{args.arch}", "--load",
        "--label", f"org.opencontainers.image.version={args.version}",
        "--label", f"org.opencontainers.image.source=https://github.com/{REPOSITORY}",
        "--tag", image, "--file", "KanbanApi/Dockerfile", ".")
    inspected = run("docker", "image", "inspect", image, "--format", "{{.Os}}/{{.Architecture}}",
                    capture_output=True, text=True).stdout.strip()
    if inspected != f"linux/{args.arch}":
        raise RuntimeError(f"Expected linux/{args.arch}, got {inspected}.")
    archive = output / f"kanbanboard-server-linux-{args.arch}.tar.gz"
    temporary = archive.with_suffix(".partial")
    process = subprocess.Popen(["docker", "image", "save", image], stdout=subprocess.PIPE)
    try:
        assert process.stdout is not None
        with temporary.open("wb") as target:
            # Stable gzip headers; never load the complete image into memory.
            with gzip.GzipFile(filename="", mode="wb", compresslevel=1, fileobj=target, mtime=0) as zipped:
                for chunk in iter(lambda: process.stdout.read(1024 * 1024), b""):
                    zipped.write(chunk)
        process.stdout.close()
        if process.wait() != 0:
            raise RuntimeError("docker image save failed.")
        temporary.replace(archive)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        temporary.unlink(missing_ok=True)
    metadata = {"version": args.version, "architecture": args.arch, "apiImage": image,
                "archive": archive.name, "sha256": sha256(archive)}
    (output / f"server-linux-{args.arch}.json").write_text(json.dumps(metadata, indent=2) + "\n")
    print(f"Verified {image}: {archive.name}; SHA-256 {metadata['sha256']}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"Server packaging failed: {error}", file=sys.stderr)
        raise SystemExit(1)
