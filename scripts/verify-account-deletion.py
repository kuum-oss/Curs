#!/usr/bin/env python3
"""Verify self-deletion on isolated throwaway accounts in the local Compose API.

No existing account is changed. Random test credentials stay in memory, and both
throwaway accounts plus their test board are removed through the tested endpoint.
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


def delete(account):
    preview = call('GET', '/api/profile/deletion-preview', account=account)
    call('DELETE', '/api/profile', {'currentPassword': account['password'], 'confirmationEmail': preview['email'],
         'expectedVersion': preview['expectedVersion'], 'confirmationToken': preview['confirmationToken']}, account, expected=204)
    account['deleted'] = True


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
    delete(owner)
    call('GET', '/api/auth/me', account=owner, expected=401)
    call('POST', '/api/auth/login', {'email': owner['user']['email'], 'password': owner['password']}, expected=401)
    checks['deletedAccountAndJwtRejected'] = True
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
    delete(peer)
    call('GET', '/api/auth/me', account=peer, expected=401)
    values = {}
    for line in (root/'.env').read_text().splitlines():
        if line and not line.startswith('#') and '=' in line:
            key, value = line.split('=', 1)
            values[key] = value.strip().strip("\"'")
    database = values.get('KANBAN_DATABASE', 'KanbanDB')
    # Read only our generated IDs. The SQL password remains in the container env.
    query = f"SET NOCOUNT ON; SELECT (SELECT COUNT(*) FROM Users WHERE Id IN ('{owner['user']['id']}','{peer['user']['id']}')) + (SELECT COUNT(*) FROM Boards WHERE Id = '{bid}') + (SELECT COUNT(*) FROM Cards WHERE Id = '{cid}')"
    remaining = subprocess.check_output(['docker', 'compose', 'exec', '-T', 'sqlserver', 'sh', '-c',
        'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec /opt/mssql-tools18/bin/sqlcmd "$@"', 'account-check',
        '-S', 'localhost', '-U', 'sa', '-d', database, '-C', '-b', '-W', '-h', '-1', '-Q', query], cwd=root, text=True, timeout=30)
    assert remaining.strip() == '0'
    checks['lastOwnerAndSoloBoardRemoved'] = True
finally:
    errors = 0
    for account in accounts:
        if account.get('deleted'):
            continue
        try:
            delete(account)
        except Exception:
            errors += 1
    if errors:
        raise RuntimeError('Could not clean up one or more isolated test accounts; inspect the local test run.')

report = {'checkedAtUtc': datetime.now(timezone.utc).isoformat(), 'requests': requests, 'checks': checks,
          'throwawayAccountsRemoved': all(a.get('deleted') for a in accounts), 'existingAccountsChanged': False}
path = root/'TestResults/account-deletion-http.json'
path.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n')
print(f'Passed {requests} local API requests; isolated accounts and test board removed.')
