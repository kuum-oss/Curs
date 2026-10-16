#!/usr/bin/env python3
"""Run solution tests against the local SQL service without printing credentials."""
from pathlib import Path
import os
import subprocess
import sys

root = Path(__file__).resolve().parent.parent
values = {}
for line in (root / '.env').read_text().splitlines():
    if line and not line.startswith('#') and '=' in line:
        key, value = line.split('=', 1)
        values[key] = value.strip().strip('\"\'')
password = values.get('MSSQL_SA_PASSWORD')
if not password:
    raise SystemExit('Prepare MSSQL_SA_PASSWORD in the local .env first.')
env = os.environ.copy()
# SQL connection string quoted values allow semicolons in local passwords.
quoted = password.replace('"', '""')
env['KANBAN_TEST_CONNECTION'] = f'Server=localhost,3038;Database=master;User Id=sa;Password="{quoted}";TrustServerCertificate=True;Connect Timeout=5'
configuration = os.environ.get('KANBAN_TEST_CONFIGURATION', 'Debug')
if configuration not in ('Debug', 'Release'):
    raise SystemExit('KANBAN_TEST_CONFIGURATION must be Debug or Release.')
result = subprocess.run([str(root/'scripts/dotnet.sh'), 'test', str(root/'KanbanApi.Tests/KanbanApi.Tests.csproj'), '-c', configuration,
    '--nologo', '--logger', 'trx;LogFileName=' + (sys.argv[1] if len(sys.argv) > 1 else 'ph01') + '.trx', '--results-directory', str(root/'TestResults')],
    cwd=root, env=env)
sys.exit(result.returncode)
