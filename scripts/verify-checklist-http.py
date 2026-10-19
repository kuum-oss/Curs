#!/usr/bin/env python3
"""Verify Docker checklist behavior with a disposable account; optionally keep it for native UI QA."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import secrets
import subprocess
import urllib.error
import urllib.request
import uuid

root = Path(__file__).resolve().parent.parent
state_file = root / 'TestResults/checklist-ui-local.json'
base = 'http://127.0.0.1:5000'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--prepare-ui', action='store_true')
parser.add_argument('--cleanup-ui', action='store_true')
args = parser.parse_args()
account = None
requests = 0


def call(method, path, data=None, expected=200):
    global requests
    headers = {'Content-Type': 'application/json'}
    if account:
        headers['Authorization'] = 'Bearer ' + account['accessToken']
    request = urllib.request.Request(base + path, headers=headers, method=method,
                                    data=None if data is None else json.dumps(data).encode())
    requests += 1
    try:
        response = urllib.request.urlopen(request, timeout=20)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        payload = response.read()
        assert response.status == expected, (method, path, response.status, expected)
    return json.loads(payload) if payload else None


def cleanup(user):
    uid = str(uuid.UUID(user['id']))
    # Only an exact identity from this verifier may be deleted. Never target demos.
    assert user['email'].startswith('checklist-check-') and user['email'].endswith('@example.test')
    sql = f"""SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRAN;
IF NOT EXISTS(SELECT 1 FROM Users WHERE Id='{uid}' AND Email LIKE 'checklist-check-%@example.test' AND IsProtectedDemo=0)
 THROW 50007,'Unsafe checklist test cleanup',1;
SELECT Id INTO #boards FROM Boards WHERE OwnerId='{uid}';
SELECT c.Id INTO #cards FROM Cards c JOIN Columns col ON c.ColumnId=col.Id WHERE col.BoardId IN (SELECT Id FROM #boards);
DELETE FROM ChecklistItems WHERE CardId IN (SELECT Id FROM #cards);
DELETE FROM CardTags WHERE CardId IN (SELECT Id FROM #cards);
DELETE FROM CardImages WHERE CardId IN (SELECT Id FROM #cards);
DELETE FROM Comments WHERE CardId IN (SELECT Id FROM #cards);
DELETE FROM Cards WHERE Id IN (SELECT Id FROM #cards);
DELETE FROM Tags WHERE BoardId IN (SELECT Id FROM #boards);
DELETE FROM BoardMembers WHERE BoardId IN (SELECT Id FROM #boards);
DELETE FROM Columns WHERE BoardId IN (SELECT Id FROM #boards);
DELETE FROM Boards WHERE Id IN (SELECT Id FROM #boards);
DELETE FROM Notifications WHERE UserId='{uid}'; UPDATE Notifications SET ActorId=NULL WHERE ActorId='{uid}';
DELETE FROM Users WHERE Id='{uid}'; COMMIT;"""
    values = {}
    for line in (root / '.env').read_text().splitlines():
        if line and not line.startswith('#') and '=' in line:
            key, value = line.split('=', 1)
            values[key] = value.strip().strip(chr(34) + chr(39))
    subprocess.run(['docker', 'compose', 'exec', '-T', 'sqlserver', 'sh', '-c',
                    'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec /opt/mssql-tools18/bin/sqlcmd "$@"',
                    'checklist-check', '-S', 'localhost', '-U', 'sa', '-d',
                    values.get('KANBAN_DATABASE', 'KanbanDB'), '-C', '-I', '-b', '-Q', sql],
                   cwd=root, check=True, capture_output=True, text=True, timeout=30)


if args.cleanup_ui:
    saved = json.loads(state_file.read_text())
    cleanup(saved['user'])
    state_file.unlink()
    print('Disposable checklist UI identity and its boards cleaned; demo accounts untouched.')
    raise SystemExit(0)

assert not state_file.exists(), 'Finish/clean the previous native QA identity first.'
keep = False
checks = []
try:
    password = 'Test.' + secrets.token_hex(12) + '!'
    account = call('POST', '/api/auth/register', {
        'email': 'checklist-check-' + uuid.uuid4().hex + '@example.test',
        'password': password, 'name': 'Перевірка чек-листів'}, 201)
    board = call('POST', '/api/boards', {'name': 'Чек-листи · перевірка', 'description': 'Нативна перевірка пунктів і прогресу'}, 201)
    bid = board['id']

    def version():
        return call('GET', f'/api/boards/{bid}')['version']

    column = call('POST', f'/api/boards/{bid}/columns', {'name': 'Заплановано', 'expectedBoardVersion': version()}, 201)
    titles = ['Уточнити вимоги', 'Підготувати дизайн', 'Реалізувати API', 'Перевірити інтерфейс', 'Оновити документацію']
    items = [{'id': str(uuid.uuid4()), 'title': title, 'isCompleted': i < 3} for i, title in enumerate(titles)]
    card = call('POST', f'/api/boards/{bid}/cards', {'columnId': column['id'], 'title': 'Підготувати реліз',
        'description': 'Усі кроки завдання в одному чек-листі.', 'checklist': items, 'expectedBoardVersion': version()}, 201)
    cid = card['card']['id']
    assert (card['card']['checklistCompleted'], card['card']['checklistTotal']) == (3, 5)
    assert [i['title'] for i in card['checklist']] == titles
    checks.append('create five ordered items, three completed')

    def body(card, checklist):
        return {'title': card['card']['title'], 'description': card['description'], 'checklist': checklist,
                'expectedVersion': card['card']['version'], 'expectedBoardVersion': version()}

    changed = list(reversed(items))
    changed[0] = dict(changed[0], title='Оновити інструкцію', isCompleted=True)
    request = body(card, changed)
    updated = call('PUT', f'/api/cards/{cid}', request)
    assert updated['checklist'][0]['title'] == 'Оновити інструкцію'
    assert updated['card']['checklistCompleted'] == 4
    call('PUT', f'/api/cards/{cid}', request, 409)
    checks.append('edit, check, reorder, and reject a stale concurrent save')
    invalid = body(updated, [dict(changed[0], title='   ')])
    call('PUT', f'/api/cards/{cid}', invalid, 400)
    assert call('GET', f'/api/cards/{cid}')['card']['version'] == updated['card']['version']
    checks.append('invalid checklist cannot partially save')
    updated = call('PUT', f'/api/cards/{cid}', body(updated, items))
    summary = call('GET', f'/api/boards/{bid}/cards')['items'][0]
    assert (summary['checklistCompleted'], summary['checklistTotal']) == (3, 5)
    checks.append('board summary agrees with saved details')
    legacy = body(updated, None)
    del legacy['checklist']
    updated = call('PUT', f'/api/cards/{cid}', legacy)
    assert len(updated['checklist']) == 5
    checks.append('older clients preserve checklist when field omitted')
    report = {'verifiedAtUtc': datetime.now(timezone.utc).isoformat(), 'httpRequests': requests,
              'checks': checks, 'existingAccountsChanged': False, 'nativeIdentityKept': args.prepare_ui}
    (root / 'TestResults/checklist-http.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
    if args.prepare_ui:
        state_file.write_text(json.dumps({'user': account['user'], 'password': password, 'boardId': bid, 'cardId': cid}))
        state_file.chmod(0o600)
        keep = True
    print(json.dumps(report, ensure_ascii=False))
finally:
    if account and not keep:
        cleanup(account['user'])
