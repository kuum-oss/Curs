#!/usr/bin/env python3
"""Publish a self-contained desktop client and native release packages."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

from common import REPOSITORY, RIDS, ROOT, dotnet, require_clean_output, run, sha256, sql_image, version_text


def load_server_metadata(path: Path, version: str, arch: str) -> dict:
    data = json.loads(path.read_text())
    expected = {"version": version, "architecture": arch,
                "apiImage": f"kanbanboard-api:{version}-{arch}",
                "archive": f"kanbanboard-server-linux-{arch}.tar.gz"}
    if any(data.get(key) != value for key, value in expected.items()):
        raise ValueError("Server metadata does not match the requested release and runtime.")
    if not isinstance(data.get("sha256"), str) or re.fullmatch(r"[a-f0-9]{64}", data["sha256"]) is None:
        raise ValueError("Server metadata requires the SHA-256 of the completed Docker archive.")
    archive = path.parent / expected["archive"]
    if archive.exists() and sha256(archive) != data["sha256"]:
        raise ValueError("Server archive does not match its metadata checksum.")
    return data


def find_iscc(explicit: str | None) -> str:
    if explicit:
        if not Path(explicit).is_file():
            raise RuntimeError("The specified Inno Setup compiler does not exist.")
        return explicit
    found = shutil.which("ISCC.exe") or shutil.which("iscc")
    if found:
        return found
    for folder in (os.environ.get("ProgramFiles(x86)"), os.environ.get("ProgramFiles")):
        if folder:
            compiler = Path(folder) / "Inno Setup 6" / "ISCC.exe"
            if compiler.is_file():
                return str(compiler)
    raise RuntimeError("Install Inno Setup 6, or pass its ISCC.exe path with --iscc.")


def verify_startup(executable: Path) -> None:
    # This explicit probe creates the native layout and exits without provisioning.
    result = subprocess.run([str(executable), "--verify-startup"], cwd=executable.parent,
                            capture_output=True, text=True, timeout=60)
    if result.returncode != 0:
        raise RuntimeError(f"Native startup probe exited {result.returncode}.\n{result.stdout}\n{result.stderr}")
    print("Native startup probe passed.")


def publish(args: argparse.Namespace, target: Path, metadata: dict) -> Path:
    run(dotnet(), "publish", ROOT / "Curs" / "Curs.csproj", "-c", "Release", "-r", args.rid,
        "--self-contained", "true", "--output", target, "--nologo",
        "-p:PublishTrimmed=false", "-p:PublishSingleFile=false", "-p:UseAppHost=true",
        "-p:RuntimeFrameworkVersion=10.0.13", "-p:DebugType=None", "-p:DebugSymbols=false",
        f"-p:Version={args.version}")
    setup = target / "setup"
    setup.mkdir(exist_ok=True)
    manifest = {"version": args.version, "apiImage": metadata["apiImage"],
                "serverArchiveUrl": f"https://github.com/{REPOSITORY}/releases/download/v{args.version}/{metadata['archive']}",
                "serverArchiveSha256": metadata["sha256"], "sqlImage": sql_image()}
    (setup / "runtime.json").write_text(json.dumps(manifest, indent=2) + "\n")
    (target / "README-FIRST.txt").write_text(
        "KanbanBoard\n\n"
        "Відкрийте KanbanBoard. Майстер першого запуску перевірить Docker Desktop,\n"
        "завантажить сервер і підготує локальну базу. Потрібне з’єднання з інтернетом.\n"
        "Якщо Docker відсутній, майстер відкриє офіційний установник.\n"
        "Встановлення Docker, системні дозволи, ліцензійні умови й можливе\n"
        "перезавантаження Windows підтверджуються у штатних вікнах.\n"
        "SQL Server Developer призначений для розробки, тестування й демонстрацій.\n"
        "Дані залишаються локальними. Видалення програми не видаляє базу.\n\n"
        "Інструкція: https://github.com/kuum-oss/Curs/blob/main/docs/RELEASES.md\n",
        encoding="utf-8")
    if args.rid == "win-x64":
        executable = target / "KanbanBoard.exe"
        (target / "Curs.exe").rename(executable)
    else:
        executable = target / "Curs"
        executable.chmod(0o755)
    required = ["Curs.dll", "Curs.runtimeconfig.json", "Curs.deps.json", "System.Private.CoreLib.dll"]
    if args.rid == "win-x64":
        required.extend(["coreclr.dll", "hostfxr.dll"])
    else:
        required.extend(["libcoreclr.dylib", "libhostfxr.dylib"])
    for name in required:
        if not (target / name).is_file():
            raise RuntimeError(f"Self-contained output is incomplete: {name}.")
    return executable


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=RIDS, required=True)
    parser.add_argument("--version", type=version_text, required=True)
    parser.add_argument("--server-metadata", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--iscc", help="Windows Inno Setup 6 compiler path")
    parser.add_argument("--verify-startup", action="store_true", help="Run the native layout-only probe on this OS")
    args = parser.parse_args()
    if args.rid.startswith("osx-") and sys.platform != "darwin":
        raise RuntimeError("Mac packages, signing, and disk images must be built on macOS.")
    if args.rid.startswith("win-") and os.name != "nt":
        raise RuntimeError("The Windows installer must be built on Windows.")
    output = args.output.resolve()
    metadata = load_server_metadata(args.server_metadata.resolve(), args.version, RIDS[args.rid])
    require_clean_output(output)
    if args.rid == "win-x64":
        payload = output / "payload"
        executable = publish(args, payload, metadata)
        if args.verify_startup:
            verify_startup(executable)
        archive = output / f"KanbanBoard-{args.version}-windows-x64.zip"
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as stream:
            for path in sorted(payload.rglob("*")):
                if path.is_file():
                    stream.write(path, str(Path("KanbanBoard") / path.relative_to(payload)))
        run(find_iscc(args.iscc), f"/DAppVersion={args.version}", f"/DPublishDir={payload}",
            f"/DPackageDir={output}", f"/DIconFile={ROOT / 'Curs/Assets/kanbanboard.ico'}",
            ROOT / "installer" / "KanbanBoard.iss")
        expected = output / f"KanbanBoard-{args.version}-windows-x64-setup.exe"
        if not expected.is_file():
            raise RuntimeError("Inno Setup did not produce the expected installer.")
    else:
        bundle = output / "KanbanBoard.app"
        target = bundle / "Contents" / "MacOS"
        executable = publish(args, target, metadata)
        resources = bundle / "Contents" / "Resources"
        resources.mkdir(parents=True)
        shutil.copyfile(ROOT / "Curs/Assets/kanbanboard.icns", resources / "kanbanboard.icns")
        info = {"CFBundleExecutable": "Curs", "CFBundleIdentifier": "com.kuum.kanbanboard",
                "CFBundleName": "KanbanBoard", "CFBundleDisplayName": "KanbanBoard",
                "CFBundleIconFile": "kanbanboard.icns", "CFBundlePackageType": "APPL",
                "CFBundleShortVersionString": args.version.split("-", 1)[0],
                "CFBundleVersion": args.version.split("-", 1)[0],
                "LSMinimumSystemVersion": "15.0", "NSHighResolutionCapable": True}
        (bundle / "Contents" / "Info.plist").write_bytes(plistlib.dumps(info))
        # Ad-hoc signing preserves bundle integrity; it is not Apple notarization.
        run("codesign", "--force", "--deep", "--sign", "-", bundle)
        run("codesign", "--verify", "--deep", "--strict", bundle)
        if args.verify_startup:
            verify_startup(executable)
        suffix = "macos-apple-silicon" if args.rid == "osx-arm64" else "macos-intel"
        archive = output / f"KanbanBoard-{args.version}-{suffix}.zip"
        run("ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", bundle, archive)
        with tempfile.TemporaryDirectory(prefix="kanban-dmg-") as staging:
            stage = Path(staging)
            shutil.copytree(bundle, stage / bundle.name, symlinks=True)
            (stage / "Applications").symlink_to("/Applications", target_is_directory=True)
            run("hdiutil", "create", "-volname", "KanbanBoard", "-srcfolder", stage,
                "-format", "UDZO", "-ov", output / f"KanbanBoard-{args.version}-{suffix}.dmg")
    print(f"Release packages ready: {output}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as error:
        print(f"Client packaging failed: {error}", file=sys.stderr)
        raise SystemExit(1)
