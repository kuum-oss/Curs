#!/usr/bin/env python3
"""Run PH-01 tests against the local SQL service without printing credentials."""
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
result = subprocess.run([str(root/'scripts/dotnet.sh'), 'test', str(root/'KanbanApi.Tests/KanbanApi.Tests.csproj'),
    '--nologo', '--logger', 'trx;LogFileName=ph01.trx', '--results-directory', str(root/'TestResults')],
    cwd=root, env=env)
sys.exit(result.returncode)
