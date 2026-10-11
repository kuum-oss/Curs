#!/usr/bin/env python3
"""Exercise a native API over HTTP with an isolated database; optionally keep it for GUI checks."""
from pathlib import Path
import json
import os
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

root = Path(__file__).resolve().parent.parent
output = root / 'TestResults'
output.mkdir(exist_ok=True)
values = {}
for line in (root / '.env').read_text().splitlines():
    if line and not line.startswith('#') and '=' in line:
        key, value = line.split('=', 1)
        values[key] = value.strip().strip('\"\'')
password = values['MSSQL_SA_PASSWORD'].replace('"', '""')
database = 'KanbanPh02_' + uuid.uuid4().hex
env = os.environ.copy()
env['ASPNETCORE_ENVIRONMENT'] = 'Development'
env['ASPNETCORE_URLS'] = 'http://127.0.0.1:5042'
env['ConnectionStrings__Default'] = f'Server=localhost,3038;Database={database};User Id=sa;Password="{password}";TrustServerCertificate=True;Connect Timeout=5'
results = []


def request(method, path, data=None, token=None, expected=200):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    payload = None if data is None else json.dumps(data).encode()
    req = urllib.request.Request('http://127.0.0.1:5042' + path, data=payload, headers=headers, method=method)
    started = time.monotonic()
    try:
        response = urllib.request.urlopen(req, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    body = json.loads(response.read())
    assert response.status == expected, f'{path}: expected {expected}, got {response.status}'
    # Never write access tokens or request passwords to results.
    results.append({'method': method, 'route': path, 'status': response.status,
                    'duration_ms': round((time.monotonic() - started) * 1000), 'code': body.get('code')})
    return body


with (output / 'ph02-api.log').open('w') as log:
    process = subprocess.Popen([str(root / 'scripts/dotnet.sh'), str(root / 'KanbanApi/bin/Debug/net10.0/KanbanApi.dll')],
                               cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
    try:
        for attempt in range(100):
            if process.poll() is not None:
                raise RuntimeError('API stopped; see ignored TestResults/ph02-api.log.')
            try:
                urllib.request.urlopen('http://127.0.0.1:5042/health/ready', timeout=1)
                break
            except (OSError, urllib.error.URLError):
                time.sleep(0.3)
        else:
            raise RuntimeError('API did not become ready.')
        email = 'http-' + uuid.uuid4().hex + '@example.test'
        account = {'email': email, 'name': 'HTTP перевірка', 'password': 'test-password123!'}
        auth = request('POST', '/api/auth/register', account, expected=201)
        request('GET', '/api/auth/me', token=auth['accessToken'])
        request('POST', '/api/auth/register', account, expected=409)
        request('POST', '/api/auth/login', {'email': email, 'password': 'wrong-password'}, expected=401)
        login = request('POST', '/api/auth/login', {'email': email.upper(), 'password': account['password']})
        request('GET', '/api/auth/me', token=login['accessToken'])
        request('GET', '/api/auth/me', expected=401)
        request('GET', '/api/auth/me', token='malformed', expected=401)
        (output / 'ph02-http.json').write_text(json.dumps({'database': database, 'checks': results}, indent=2))
        print('Native API HTTP checks passed (8); no credentials written to the report.', flush=True)
        if '--gui' in sys.argv:
            print('GUI verification API ready at http://127.0.0.1:5042; interrupt to stop and remove its temporary database.', flush=True)
            signal.pause()
    except KeyboardInterrupt:
        pass
    finally:
        process.send_signal(signal.SIGINT)
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        # The generated identifier has a fixed prefix and UUID, never a user's database name.
        query = f'IF DB_ID(N\'{database}\') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END'
        cleanup = subprocess.run(['docker', 'compose', 'exec', '-T', 'sqlserver', 'sh', '-c',
            'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -Q "$1"', 'cleanup', query],
            cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if cleanup.returncode:
            raise RuntimeError('Temporary SQL database cleanup failed.')
        print('Temporary HTTP/GUI database removed.', flush=True)
