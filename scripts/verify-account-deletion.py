#!/usr/bin/env python3
"""Verify self-deletion on isolated throwaway accounts in the local Compose API.

No existing account is changed. Random test credentials stay in memory. The API
schedules deletion and login restores it. SQL advances only these throwaway IDs
past their deadline; the real background worker removes them and their test board.
"""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import secrets
import subprocess
import urllib.error
import urllib.request
import uuid
import time

root = Path(__file__).resolve().parent.parent
base = 'http://127.0.0.1:5000'
accounts = []
requests = 0
checks = {}


def call(method, path, data=None, account=None, expected=200, raw=False, content_type=None):
    global requests
    headers = {'Content-Type': content_type or 'application/json'}
    if account:
        headers['Authorization'] = 'Bearer '+account['accessToken']
    body = data if isinstance(data, bytes) else None if data is None else json.dumps(data).encode()
    request = urllib.request.Request(base+path, data=body, method=method, headers=headers)
    requests += 1
    try:
        response = urllib.request.urlopen(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        status = response.status
        payload = response.read()
    if status != expected:
        raise AssertionError(f'{method} returned {status}, expected {expected}')
    return payload if raw else json.loads(payload) if payload else None


def register(name):
    password = 'Test.'+secrets.token_hex(15)+'!'
    auth = call('POST', '/api/auth/register', {'name': name, 'email': 'delete-check-'+uuid.uuid4().hex+'@example.test', 'password': password}, expected=201)
    auth['password'] = password
    accounts.append(auth)
    return auth


def sql(query):
    values = {}
    for line in (root/'.env').read_text().splitlines():
        if line and not line.startswith('#') and '=' in line:
            key, value = line.split('=', 1)
            values[key] = value.strip().strip("\"'")
    return subprocess.check_output(['docker', 'compose', 'exec', '-T', 'sqlserver', 'sh', '-c',
        'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec /opt/mssql-tools18/bin/sqlcmd "$@"', 'account-check',
        '-S', 'localhost', '-U', 'sa', '-d', values.get('KANBAN_DATABASE', 'KanbanDB'), '-C', '-b', '-W', '-h', '-1', '-Q', 'SET NOCOUNT ON; '+query], cwd=root, text=True, timeout=30).strip()


def schedule(account):
    preview = call('GET', '/api/profile/deletion-preview', account=account)
    result = call('DELETE', '/api/profile', {'currentPassword': account['password'], 'confirmationEmail': preview['email'],
         'expectedVersion': preview['expectedVersion'], 'confirmationToken': preview['confirmationToken']}, account, expected=202)
    account['scheduled'] = True
    assert 'deletionDueAtUtc' in result


def expire_own_fixture(account):
    # Time travel is restricted to the random throwaway ID created by this run.
    # Never changes a real/demo user or bypasses the application's demo protection.
    assert account['user']['email'].startswith('delete-check-')
    uid = str(uuid.UUID(account['user']['id']))
    sql(f"DECLARE @requested datetime2 = DATEADD(day,-8,SYSUTCDATETIME()); UPDATE Users SET DeletionRequestedAtUtc=@requested, DeletionDueAtUtc=DATEADD(day,7,@requested) WHERE Id='{uid}' AND Email LIKE 'delete-check-%@example.test' AND IsProtectedDemo=0 AND DeletionDueAtUtc IS NOT NULL;")


def wait_removed(account):
    uid = str(uuid.UUID(account['user']['id']))
    deadline = time.monotonic()+85
    while time.monotonic()<deadline:
        if sql(f"SELECT COUNT(*) FROM Users WHERE Id='{uid}'") == '0':
            account['deleted'] = True
            return
        time.sleep(1)
    raise RuntimeError('Cleanup worker did not remove an expired throwaway account in 85 seconds.')


try:
    owner = register('Тест видалення')
    peer = register('Новий власник тесту')
    board = call('POST', '/api/boards', {'name': 'Тимчасова перевірка видалення'}, owner, expected=201)
    bid = board['id']
    def version():
        return call('GET', '/api/boards/'+bid, account=owner)['version']
    column = call('POST', f'/api/boards/{bid}/columns', {'name': 'План', 'expectedBoardVersion': version()}, owner, expected=201)
    card = call('POST', f'/api/boards/{bid}/cards', {'columnId': column['id'], 'title': 'Збережена спільна картка',
         'priority': 1, 'tagIds': [], 'assigneeId': owner['user']['id'], 'expectedBoardVersion': version()}, owner, expected=201)['card']
    cid = card['id']
    call('POST', f'/api/cards/{cid}/comments', {'text': 'Історія зберігається', 'expectedBoardVersion': version(), 'expectedVersion': card['version']}, owner, expected=201)
    card = call('GET', '/api/cards/'+cid, account=owner)['card']
    boundary = uuid.uuid4().hex
    body = b''
    for key, value in {'ExpectedBoardVersion': version(), 'ExpectedVersion': card['version']}.items():
        body += (f'--{boundary}\r\nContent-Disposition: form-data; name="{key}"\r\n\r\n{value}\r\n').encode()
    image = (root/'Curs/Assets/kanbanboard.png').read_bytes()
    body += (f'--{boundary}\r\nContent-Disposition: form-data; name="File"; filename="icon.png"\r\nContent-Type: image/png\r\n\r\n').encode()+image+f'\r\n--{boundary}--\r\n'.encode()
    call('PUT', '/api/cards/'+cid+'/image', body, owner, content_type='multipart/form-data; boundary='+boundary)
    call('POST', f'/api/boards/{bid}/members', {'email': peer['user']['email'], 'expectedBoardVersion': version()}, owner, expected=201)
    preview = call('GET', '/api/profile/deletion-preview', account=owner)
    assert preview['ownedBoards'][0]['newOwnerName'] == peer['user']['name']
    checks['previewNamesSuccessor'] = True
    old_token = owner['accessToken']
    schedule(owner)
    call('GET', '/api/auth/me', account=owner, expected=401)
    retained = call('GET', '/api/boards/'+bid, account=peer)
    assert retained['ownerId'] == owner['user']['id']
    restored = call('POST', '/api/auth/login', {'email': owner['user']['email'], 'password': owner['password']})
    assert restored['accountRestored'] and restored['user']['id'] == owner['user']['id']
    owner.update(restored)
    owner['scheduled'] = False
    call('GET', '/api/auth/me', account={'accessToken': old_token}, expected=401)
    retained = call('GET', '/api/cards/'+cid, account=owner)['card']
    assert retained['assignee']['id'] == owner['user']['id'] and retained['hasImage']
    checks['sevenDayRetentionAndLoginRecovery'] = True
    schedule(owner)
    expire_own_fixture(owner)
    print('Recovery verified; waiting for background expiry cleanup of isolated owner.', flush=True)
    wait_removed(owner)
    call('POST', '/api/auth/login', {'email': owner['user']['email'], 'password': owner['password']}, expected=401)
    checks['expiredAccountAndOldJwtRejected'] = True
    changed = call('GET', '/api/boards/'+bid, account=peer)
    assert changed['ownerId'] == peer['user']['id'] and changed['role'] == 0
    card = call('GET', '/api/cards/'+cid, account=peer)['card']
    comments = call('GET', '/api/cards/'+cid+'/comments', account=peer)
    assert card['assignee'] is None and card['hasImage']
    assert comments['totalCount'] == 1 and comments['items'][0]['author']['name'] == 'Видалений користувач'
    assert comments['items'][0]['author']['id'] == str(uuid.UUID(int=0))
    stored = call('GET', '/api/cards/'+cid+'/image', account=peer, raw=True)
    thumbnail = call('GET', '/api/cards/'+cid+'/image/thumbnail', account=peer, raw=True)
    assert hashlib.sha256(stored).digest() == hashlib.sha256(image).digest() and thumbnail.startswith(b'\x89PNG\r\n\x1a\n')
    checks['sharedBoardCardsCommentsAndMediaPreserved'] = True
    checks['linuxNativeImageDecoderWorks'] = True
    schedule(peer)
    expire_own_fixture(peer)
    print('Shared board transfer verified; waiting for expiry cleanup of isolated final owner.', flush=True)
    wait_removed(peer)
    call('GET', '/api/auth/me', account=peer, expected=401)
    remaining = sql(f"SELECT (SELECT COUNT(*) FROM Users WHERE Id IN ('{owner['user']['id']}','{peer['user']['id']}')) + (SELECT COUNT(*) FROM Boards WHERE Id='{bid}') + (SELECT COUNT(*) FROM Cards WHERE Id='{cid}')")
    assert remaining == '0'
    protected = sql("SELECT COUNT(*) FROM Users WHERE IsProtectedDemo=1 AND DeletionDueAtUtc IS NULL AND NormalizedEmail IN ('DEMO.ANASTASIIA@KANBAN.LOCAL','DEMO.OLENA@KANBAN.LOCAL','DEMO.DMYTRO@KANBAN.LOCAL','DEMO.IRYNA@KANBAN.LOCAL')")
    assert protected == '4'
    checks['fourProtectedDemoAccountsIntact'] = True
    checks['lastOwnerAndSoloBoardRemoved'] = True
finally:
    errors = 0
    for account in accounts:
        if account.get('deleted'):
            continue
        try:
            if not account.get('scheduled'): schedule(account)
            expire_own_fixture(account)
            wait_removed(account)
        except Exception:
            errors += 1
    if errors:
        raise RuntimeError('Could not clean up one or more isolated test accounts; inspect the local test run.')

report = {'checkedAtUtc': datetime.now(timezone.utc).isoformat(), 'requests': requests, 'checks': checks,
          'throwawayAccountsRemoved': all(a.get('deleted') for a in accounts), 'existingAccountsChanged': False, 'fixtureClockOnly': 'SQL shifts timestamps only on random accounts created by this run; real background worker performs cleanup'}
path = root/'TestResults/account-deletion-http.json'
path.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n')
print(f'Passed {requests} local API requests; isolated accounts and test board removed.')
