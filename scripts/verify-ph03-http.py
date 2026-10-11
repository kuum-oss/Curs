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
import urllib.parse
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
database = 'KanbanPh03_' + uuid.uuid4().hex
env = os.environ.copy()
env['ASPNETCORE_ENVIRONMENT'] = 'Development'
env['ASPNETCORE_URLS'] = 'http://127.0.0.1:5043'
env['ConnectionStrings__Default'] = f'Server=localhost,3038;Database={database};User Id=sa;Password="{password}";TrustServerCertificate=True;Connect Timeout=5'
results = []


def request(method, path, data=None, token=None, expected=200):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    payload = None if data is None else json.dumps(data).encode()
    req = urllib.request.Request('http://127.0.0.1:5043' + path, data=payload, headers=headers, method=method)
    started = time.monotonic()
    try:
        response = urllib.request.urlopen(req, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    raw = response.read()
    body = json.loads(raw) if raw else {}
    assert response.status == expected, f'{path}: expected {expected}, got {response.status}'
    # Never write access tokens or request passwords to results.
    results.append({'method': method, 'route': path, 'status': response.status,
                    'duration_ms': round((time.monotonic() - started) * 1000), 'code': body.get('code') if isinstance(body, dict) else None})
    return body


with (output / 'ph03-api.log').open('w') as log:
    process = subprocess.Popen([str(root / 'scripts/dotnet.sh'), str(root / 'KanbanApi/bin/Debug/net10.0/KanbanApi.dll')],
                               cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
    try:
        for attempt in range(100):
            if process.poll() is not None:
                raise RuntimeError('API stopped; see ignored TestResults/ph03-api.log.')
            try:
                urllib.request.urlopen('http://127.0.0.1:5043/health/ready', timeout=1)
                break
            except (OSError, urllib.error.URLError):
                time.sleep(0.3)
        else:
            raise RuntimeError('API did not become ready.')
        def register(email, name):
            return request('POST', '/api/auth/register', {'email':email, 'name':name, 'password':'test-password123!'}, expected=201)
        owner = register('ph03-admin@example.test', 'Олена Адміністратор')
        person = register('ph03-member@example.test', 'Іван Учасник')
        outsider = register('ph03-outsider@example.test', 'Сторонній')
        admin = owner['accessToken']; member = person['accessToken']; stranger = outsider['accessToken']
        board = request('POST', '/api/boards', {'name':'HTTP дошка','description':'Перевірка'}, admin, 201)
        base = '/api/boards/' + board['id']
        assert request('GET','/api/boards',token=admin)[0]['id'] == board['id']
        request('GET',base,token=stranger,expected=404)
        assert request('GET','/api/boards',token=stranger) == []
        request('GET','/api/boards',expected=401)
        request('POST',base+'/members',{'email':person['user']['email'],'expectedBoardVersion':board['version']},admin,201)
        board = request('GET',base,token=admin)
        assert request('GET',base,token=member)['role'] == 1
        request('PUT',base,{'name':'Forbidden','expectedBoardVersion':board['version']},member,403)
        request('DELETE',base+'?expectedBoardVersion='+urllib.parse.quote(board['version'],safe=''),token=member,expected=403)
        request('POST',base+'/members',{'email':person['user']['email'],'expectedBoardVersion':board['version']},admin,409)
        request('PATCH',base+'/members/'+person['user']['id'],{'canCreateCards':False,'canDeleteCards':False,'expectedBoardVersion':board['version']},admin)
        current = request('GET',base,token=member)
        assert not current['canCreateCards'] and not current['canDeleteCards']
        request('PUT',base,{'name':'Stale','expectedBoardVersion':board['version']},admin,409)
        request('PUT',base,{'name':'Оновлена HTTP дошка','expectedBoardVersion':current['version']},admin)
        current = request('GET',base,token=admin)
        request('GET',base+'/members',token=admin)
        request('DELETE',base+'/members/'+owner['user']['id']+'?expectedBoardVersion='+urllib.parse.quote(current['version'],safe=''),token=admin,expected=409)
        request('DELETE',base+'/members/'+person['user']['id']+'?expectedBoardVersion='+urllib.parse.quote(current['version'],safe=''),token=admin,expected=204)
        request('GET',base,token=member,expected=404)
        assert request('GET','/api/boards',token=member) == []
        current = request('GET',base,token=admin)
        request('DELETE',base+'?expectedBoardVersion='+urllib.parse.quote(current['version'],safe=''),token=admin,expected=204)
        assert request('GET','/api/boards',token=admin) == []
        (output / 'ph03-http.json').write_text(json.dumps({'database':database,'checks':results},indent=2))
        print(f'Native API board checks passed ({len(results)}); credentials omitted from report.', flush=True)
        if '--gui' in sys.argv:
            print('GUI verification API ready at http://127.0.0.1:5043; interrupt to stop and remove its temporary database.', flush=True)
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
