"""Package integrity checks that run without Docker or a platform installer."""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

from common import require_clean_output, sha256, version_text


def load(name: str):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), Path(__file__).with_name(name + '.py'))
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


packaging = load('package-client')
verification = load('verify-artifacts')


class PackagingTests(unittest.TestCase):
    def metadata(self, folder: Path, arch: str = 'arm64'):
        archive = folder / f'kanbanboard-server-linux-{arch}.tar.gz'
        archive.write_bytes(b'completed server archive')
        data = {'version': '0.2.0', 'architecture': arch, 'apiImage': f'kanbanboard-api:0.2.0-{arch}',
                'archive': archive.name, 'sha256': sha256(archive)}
        path = folder / f'server-linux-{arch}.json'
        path.write_text(json.dumps(data))
        return path, archive, data

    def test_wrong_server_bytes_and_cross_arch_metadata_are_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path, archive, _ = self.metadata(Path(tmp))
            packaging.load_server_metadata(path, '0.2.0', 'arm64')
            with self.assertRaises(ValueError):
                packaging.load_server_metadata(path, '0.2.0', 'amd64')
            archive.write_bytes(b'corrupted download')
            with self.assertRaises(ValueError):
                packaging.load_server_metadata(path, '0.2.0', 'arm64')

    def test_new_output_directory_is_required_and_existing_files_survive(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp) / 'release'
            require_clean_output(folder)
            marker = folder / 'keep.txt'
            marker.write_text('existing build')
            with self.assertRaises(RuntimeError):
                require_clean_output(folder)
            self.assertEqual('existing build', marker.read_text())

    def test_archive_runtime_manifest_is_bound_to_exact_server_asset(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            _, _, data = self.metadata(folder)
            archive = folder / 'client.zip'
            prefix = 'KanbanBoard.app/Contents/MacOS/'
            manifest = {'version': '0.2.0', 'apiImage': data['apiImage'],
                        'serverArchiveUrl': 'https://github.com/kuum-oss/Curs/releases/download/v0.2.0/' + data['archive'],
                        'serverArchiveSha256': data['sha256'],
                        'sqlImage': 'mcr.microsoft.com/mssql/server:2022-latest@sha256:' + 'a' * 64}
            def write(runtime, extra=None):
                with zipfile.ZipFile(archive, 'w') as z:
                    for name in ['Curs.dll', 'Curs.runtimeconfig.json', 'System.Private.CoreLib.dll', 'Curs', 'libcoreclr.dylib', 'libhostfxr.dylib']:
                        z.writestr(prefix + name, 'package fixture')
                    z.writestr(prefix + 'setup/runtime.json', json.dumps(runtime))
                    if extra:
                        z.writestr(extra, 'unexpected local data')
            write(manifest)
            verification.verify_zip(archive, '0.2.0', 'osx-arm64', data)
            write(dict(manifest, serverArchiveSha256='b' * 64))
            with self.assertRaises(ValueError):
                verification.verify_zip(archive, '0.2.0', 'osx-arm64', data)
            write(manifest, prefix + '.env')
            with self.assertRaises(ValueError):
                verification.verify_zip(archive, '0.2.0', 'osx-arm64', data)
            write(manifest, '../escape')
            with self.assertRaises(ValueError):
                verification.verify_zip(archive, '0.2.0', 'osx-arm64', data)

    def test_versions_cannot_be_paths_or_shell_fragments(self):
        for value in ['../0.2.0', '0.2.0; echo unexpected', 'v0.2.0', '', '0.2']:
            with self.assertRaises(ValueError):
                version_text(value)
        self.assertEqual('0.2.0', version_text('0.2.0'))


if __name__ == '__main__':
    unittest.main()
