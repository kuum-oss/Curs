#!/usr/bin/env python3
"""Verify a disposable Compose stack; never remove the application's data volume."""
from pathlib import Path
from datetime import date, timedelta
import hashlib
import json
import os
import secrets
import signal
import socket
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

root = Path(__file__).resolve().parent.parent
output = root / 'TestResults'
output.mkdir(exist_ok=True)
project = 'kanbanph07' + uuid.uuid4().hex[:12]
volume = project + '_sqlserver-data'
log = (output / 'ph07-compose.log').open('w')
env = os.environ.copy()
# Values live only in the child environment; no credentials in argv or the report.
env.update(MSSQL_SA_PASSWORD='Ph07!' + secrets.token_hex(16), JWT_KEY=secrets.token_hex(32),
           KANBAN_DATABASE='KanbanPh07', COMPOSE_PROJECT_NAME=project)

def free_ports():
    sockets = [socket.socket() for _ in range(2)]
    try:
        for item in sockets:
            item.bind(('127.0.0.1', 0))
        return [str(item.getsockname()[1]) for item in sockets]
    finally:
        for item in sockets:
            item.close()

env['API_PORT'], env['SQL_PORT'] = free_ports()
base_url = 'http://127.0.0.1:' + env['API_PORT']
report = {'phase': 'PH-07', 'project': project, 'checks': [], 'http': [], 'snapshots': {}}
created = False
gui_ready = False


def compose(*args, check=True, timeout=300):
    command = ['docker', 'compose', '-p', project, '-f', str(root / 'compose.yaml'), *args]
    result = subprocess.run(command, cwd=root, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            timeout=timeout, text=True)
    log.write(result.stdout); log.flush()
    if check and result.returncode:
        raise RuntimeError('Compose operation failed: ' + args[0] + '; see ignored TestResults/ph07-compose.log')
    return result


def record(name, condition=True):
    assert condition, name
    report['checks'].append(name)
    print('PASS ' + name, flush=True)


def http(method, path, data=None, token=None, expected=200, headers=None, timeout=90):
    request_headers = {'Content-Type': 'application/json', **(headers or {})}
    if token:
        request_headers['Authorization'] = 'Bearer ' + token
    payload = data if isinstance(data, bytes) else None if data is None else json.dumps(data).encode()
    req = urllib.request.Request(base_url + path, data=payload, headers=request_headers, method=method)
    start = time.monotonic()
    try:
        response = urllib.request.urlopen(req, timeout=timeout)
    except urllib.error.HTTPError as error:
        response = error
    raw = response.read()
    report['http'].append({'method': method, 'path': path, 'status': response.status,
                           'duration_ms': round((time.monotonic() - start) * 1000)})
    allowed = expected if isinstance(expected, tuple) else (expected,)
    assert response.status in allowed, f'{method} {path}: expected {expected}, got {response.status}'
    if response.headers.get_content_type().startswith('image/'):
        return raw
    return json.loads(raw) if raw else {}


def wait_ready():
    # SQL's master healthcheck can pass while KanbanPh07 is still recovering;
    # Compose may also retain an earlier healthy API status. Require a fresh HTTP probe.
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        try:
            if http('GET', '/health/ready', expected=(200, 503), timeout=10)['status'] == 'ready':
                return True
        except (OSError, TimeoutError):
            pass
        time.sleep(1)
    return False


def sql(query):
    return compose('exec', '-T', 'sqlserver', 'sh', '-c',
                   'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -d KanbanPh07 -h -1 -W -Q "$1"',
                   'verify', query).stdout.strip()


def status():
    raw = compose('ps', '--format', 'json').stdout.strip()
    return json.loads(raw) if raw.startswith('[') else [json.loads(line) for line in raw.splitlines() if line.strip()]


def snapshot(label):
    detail = http('GET', card_path, token=admin)
    columns = http('GET', board_path + '/columns', token=admin)
    cards = http('GET', board_path + '/cards', token=admin)
    tags = http('GET', board_path + '/tags', token=admin)
    comments = http('GET', card_path + '/comments', token=admin)
    members = http('GET', board_path + '/members', token=admin)
    original = http('GET', card_path + '/image', token=admin)
    thumbnail = http('GET', card_path + '/image/thumbnail', token=admin)
    assert original == samples['webp'] and thumbnail.startswith(b'\x89PNG\r\n\x1a\n')
    value = {'detail': detail, 'columns': columns, 'cards': cards, 'tags': tags,
             'comments': comments, 'members': members,
             'original_sha256': hashlib.sha256(original).hexdigest(),
             'thumbnail_sha256': hashlib.sha256(thumbnail).hexdigest()}
    report['snapshots'][label] = value
    return value


try:
    compose('config', '--quiet')
    record('Compose configuration validates with isolated secrets and ports')
    created = True
    compose('up', '--build', '-d', '--wait', '--wait-timeout', '240', timeout=900)
    state = status()
    record('Fresh SQL and API containers are healthy', len(state) == 2 and all(s['Health'] == 'healthy' for s in state))
    record('Live returns 200', http('GET', '/health/live')['status'] == 'live')
    record('Ready returns 200 after migrations', http('GET', '/health/ready')['status'] == 'ready')
    table_count = sql("SET NOCOUNT ON; SELECT COUNT(*) FROM sys.tables WHERE name <> '__EFMigrationsHistory';")
    record('Fresh database contains thirteen domain tables including notifications', table_count == '13')
    migration_count = sql('SET NOCOUNT ON; SELECT COUNT(*) FROM __EFMigrationsHistory;')
    record('All seven EF migrations applied exactly once', migration_count == '7')
    record('API runs as a non-root user', compose('exec', '-T', 'api', 'id', '-u').stdout.strip() != '0')
    api_id = compose('ps', '-q', 'api').stdout.strip()
    image_id = subprocess.check_output(['docker', 'inspect', '--format', '{{.Image}}', api_id], text=True).strip()
    report['image_id'] = image_id
    report['api_architecture'] = subprocess.check_output(['docker', 'image', 'inspect', '--format', '{{.Architecture}}', image_id], text=True).strip()
    record('SDK and local files are absent from the runtime', compose('exec', '-T', 'api', 'sh', '-c',
        'test ! -e /app/.env && test ! -e /app/Curs && test ! -d /usr/share/dotnet/sdk').returncode == 0)
    record('Image healthcheck succeeds', compose('exec', '-T', 'api', 'dotnet', 'KanbanApi.dll', '--healthcheck').returncode == 0)
    password = 'ph07-test-password123!'
    owner = http('POST', '/api/auth/register', {'email': 'ph07-admin@example.test', 'name': 'Олена Docker', 'password': password}, expected=201)
    peer = http('POST', '/api/auth/register', {'email': 'ph07-member@example.test', 'name': 'Іван Docker', 'password': password}, expected=201)
    admin = owner['accessToken']; member = peer['accessToken']
    board = http('POST', '/api/boards', {'name': 'Курсова PH-07 — Docker', 'description': 'Збереження після перезапуску'}, admin, 201)
    board_path = '/api/boards/' + board['id']
    def fresh():
        return http('GET', board_path, token=admin)['version']
    http('POST', board_path + '/members', {'email': peer['user']['email'], 'expectedBoardVersion': fresh()}, admin, 201)
    first = http('POST', board_path + '/columns', {'name': 'Заплановано', 'expectedBoardVersion': fresh()}, admin, 201)
    second = http('POST', board_path + '/columns', {'name': 'Готово', 'expectedBoardVersion': fresh()}, admin, 201)
    tag = http('POST', board_path + '/tags', {'name': 'Docker', 'color': '#2563EB', 'expectedBoardVersion': fresh()}, admin, 201)
    due = (date.today() + timedelta(days=30)).isoformat()
    fields = {'title': 'Перевірити збереження обкладинки', 'description': 'Усі дані у volume MSSQL', 'dueDate': due,
              'priority': 3, 'assigneeId': peer['user']['id'], 'tagIds': [tag['id']]}
    card = http('POST', board_path + '/cards', dict(fields, columnId=first['id'], expectedBoardVersion=fresh()), admin, 201)
    card_path = '/api/cards/' + card['card']['id']
    other = http('POST', board_path + '/cards', {'title': 'Друга картка', 'columnId': second['id'], 'expectedBoardVersion': fresh()}, admin, 201)
    http('POST', card_path + '/comments', {'text': 'Коментар із контейнерного API', 'expectedBoardVersion': fresh(), 'expectedVersion': card['card']['version']}, admin, 201)
    current = http('GET', card_path, token=admin)
    http('POST', card_path + '/move', {'targetColumnId': second['id'], 'beforeCardId': other['card']['id'],
         'expectedBoardVersion': fresh(), 'expectedVersion': current['card']['version']}, admin)
    http('PUT', board_path + '/columns/order', {'columnIds': [second['id'], first['id']], 'expectedBoardVersion': fresh()}, admin)
    samples = {ext: (root / 'KanbanApi.Tests/Fixtures' / ('cover.' + ext)).read_bytes() for ext in ['jpg', 'png', 'webp']}
    for ext, data in samples.items():
        current = http('GET', card_path, token=admin)
        boundary = 'ph07' + uuid.uuid4().hex
        body = b''
        for name, value in {'ExpectedVersion': current['card']['version'], 'ExpectedBoardVersion': fresh()}.items():
            body += (f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n').encode()
        body += (f'--{boundary}\r\nContent-Disposition: form-data; name="File"; filename="cover.{ext}"\r\nContent-Type: application/octet-stream\r\n\r\n').encode() + data + b'\r\n' + (f'--{boundary}--\r\n').encode()
        http('PUT', card_path + '/image', body, admin, headers={'Content-Type': 'multipart/form-data; boundary=' + boundary})
        original = http('GET', card_path + '/image', token=admin)
        thumbnail = http('GET', card_path + '/image/thumbnail', token=admin)
        record(ext.upper() + ' decodes in Linux and returns exact original plus PNG thumbnail', original == data and struct.unpack('>II', thumbnail[16:24]) == (480, 320))
    query = urllib.parse.urlencode({'search': 'збереження', 'priority': 'Critical', 'tagId': tag['id'], 'assigneeId': peer['user']['id'], 'deadline': 'Upcoming', 'today': date.today().isoformat()})
    record('All filters work through container API', http('GET', board_path + '/cards?' + query, token=admin)['totalCount'] == 1)
    http('GET', card_path + '/image', expected=401)
    baseline = snapshot('before_restart')
    record('Stored graph includes two ordered cards and a comment', len(baseline['cards']['items']) == 2 and baseline['comments']['totalCount'] == 1)
    compose('restart')
    compose('up', '-d', '--wait', '--wait-timeout', '240')
    record('All fields, ordering, versions, members, tags, comments and photo survive restart', snapshot('after_restart') == baseline)
    record('Migration remains idempotent after restart', sql('SET NOCOUNT ON; SELECT COUNT(*) FROM __EFMigrationsHistory;') == '7')
    compose('down')
    subprocess.run(['docker', 'volume', 'inspect', volume], check=True, stdout=subprocess.DEVNULL)
    record('Normal compose down retains the named volume')
    compose('up', '-d', '--wait', '--wait-timeout', '240')
    record('Recreated containers retain complete graph and exact photo bytes', snapshot('after_down_up') == baseline)
    login = http('POST', '/api/auth/login', {'email': owner['user']['email'], 'password': password})
    record('User credentials survive down/up', login['user']['id'] == owner['user']['id'])
    # Readiness checks SQL; liveness proves the HTTP process itself is still running.
    compose('stop', 'sqlserver')
    record('API stays live when SQL stops', http('GET', '/health/live')['status'] == 'live')
    record('API is not ready when SQL stops', http('GET', '/health/ready', expected=503)['status'] == 'not_ready')
    record('Image probe fails when SQL stops', compose('exec', '-T', 'api', 'dotnet', 'KanbanApi.dll', '--healthcheck', check=False).returncode != 0)
    compose('up', '-d', '--wait', '--wait-timeout', '240')
    record('Ready recovers after SQL starts', wait_ready())
    record('Recovered graph is unchanged', snapshot('after_sql_outage') == baseline)
    # Separate, unpublished one-off containers must fail before listening.
    invalid = compose('run', '--rm', '--no-deps', '-e', 'Jwt__Key=replace-with-invalid', 'api', check=False)
    record('Invalid JWT configuration exits before serving HTTP', invalid.returncode == 1 and 'Now listening' not in invalid.stdout)
    invalid_db = compose('run', '--rm', '--no-deps', '-e',
        'ConnectionStrings__Default=Server=127.0.0.1,1;Database=KanbanPh07;User Id=sa;Password=invalid;TrustServerCertificate=True;Connect Timeout=1',
        'api', check=False, timeout=150)
    record('Failed startup migration exits before serving HTTP', invalid_db.returncode == 1 and 'Now listening' not in invalid_db.stdout)
    report['verification_passed'] = True
    if '--gui' in sys.argv:
        gui_ready = True
        (output / 'ph07-gui.env').write_text('KANBAN_API_URL=' + base_url + '/\n')
        print('GUI ready: ' + base_url + '; synthetic ph07-admin@example.test / ph07-test-password123!; interrupt after closing client.', flush=True)
        signal.pause()
except KeyboardInterrupt:
    if not gui_ready:
        report['error'] = 'Interrupted before verification completed'
        raise
    report['gui_session_closed'] = True
except BaseException as error:
    report['error'] = type(error).__name__ + ': ' + str(error)
    raise
finally:
    if created:
        compose('down', check=False)
        # Only this run's randomly named, self-created disposable volume is removed.
        removed = subprocess.run(['docker', 'volume', 'rm', volume], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        log.write(removed.stdout); log.flush()
        report['temporary_volume_removed'] = removed.returncode == 0
    report['completed_at'] = time.strftime('%Y-%m-%dT%H:%M:%S%z')
    (output / 'ph07-docker.json').write_text(json.dumps(report, ensure_ascii=False, indent=2))
    log.close()
    if created and not report['temporary_volume_removed']:
        raise RuntimeError('Disposable volume cleanup failed; see ignored Compose log.')
    print('Disposable Compose cleanup complete; application volume untouched.', flush=True)
