#!/usr/bin/env python3
"""Read an explicitly seeded local board, restart API/SQL, and compare all data/media.

Requires ignored seed-demo.py credentials and an explicit --board UUID. Never deletes
containers, databases or volumes. Run only against the local coursework Compose stack.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import time
import urllib.request
import uuid

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--board', required=True, type=uuid.UUID)
parser.add_argument('--credentials', type=Path, default=root/'TestResults/ph08-demo.json')
parser.add_argument('--base-url', default='http://127.0.0.1:5000')
args = parser.parse_args()
if not args.base_url.startswith('http://127.0.0.1:'):
    raise SystemExit('Only the local IPv4 Compose API is supported.')
state = json.loads(args.credentials.read_text())
token = None


def request(path, body=None):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer '+token
    req = urllib.request.Request(args.base_url+path, headers=headers,
        data=None if body is None else json.dumps(body).encode())
    with urllib.request.urlopen(req, timeout=15) as response:
        data = response.read()
        return data if response.headers.get_content_type().startswith('image/') else json.loads(data)


def digest(value):
    data = value if isinstance(value, bytes) else json.dumps(value, sort_keys=True, ensure_ascii=False).encode()
    return hashlib.sha256(data).hexdigest()


def snapshot():
    board = '/api/boards/'+str(args.board)
    result = {name: request(board+suffix) for name, suffix in
        [('board', ''), ('columns', '/columns'), ('tags', '/tags'), ('members', '/members')]}
    cards = []
    for suffix in ['', '?archived=true']:
        offset = 0
        while True:
            query = suffix+('&' if suffix else '?')+f'offset={offset}&limit=100'
            rows = request(board+'/cards'+query)
            cards.extend(rows['items'])
            if offset+len(rows['items']) >= rows['totalCount']:
                break
            offset += len(rows['items'])
    result['cards'] = sorted(cards, key=lambda c: c['id'])
    result['details'] = {}
    media = {}
    for card in result['cards']:
        path = '/api/cards/'+card['id']
        result['details'][card['id']] = request(path)
        comments = []
        offset = 0
        while True:
            rows = request(path+f'/comments?offset={offset}&limit=100')
            comments.extend(rows['items'])
            if offset+len(rows['items']) >= rows['totalCount']:
                break
            offset += len(rows['items'])
        result['details'][card['id']]['allComments'] = comments
        if card['hasImage']:
            for suffix in ['/image', '/image/thumbnail']:
                media[path+suffix] = digest(request(path+suffix))
    if result['board']['hasCover']:
        for suffix in ['/cover', '/cover/thumbnail']:
            media[board+suffix] = digest(request(board+suffix))
    for member in result['members']:
        user = member['user']
        if user['hasAvatar']:
            path = '/api/users/'+user['id']+'/avatar'
            media[path] = digest(request(path))
    result['profile'] = request('/api/auth/me')
    result['media'] = media
    return result


auth = request('/api/auth/login', {'email': state['users'][0]['email'], 'password': state['password']})
token = auth['accessToken']
before = snapshot()
subprocess.run(['docker', 'compose', 'restart'], cwd=root, check=True, timeout=90)
deadline = time.monotonic()+180
while time.monotonic() < deadline:
    try:
        if request('/health/ready')['status'] == 'ready':
            break
    except (OSError, ValueError):
        pass
    time.sleep(1)
else:
    raise RuntimeError('Compose API did not recover after restart.')
after = snapshot()
assert before == after, 'Data or media changed after restart; phase acceptance failed.'
report = {'phase': 'PH-08', 'verified_at_utc': datetime.now(timezone.utc).isoformat(),
    'revision': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
    'checks': {'api_and_sql_restarted': True, 'readiness_recovered': True,
        'same_jwt_accepted_after_restart': True, 'all_fields_versions_order_comments_preserved': True,
        'profile_roles_permissions_favorite_archive_preserved': True, 'all_originals_thumbnails_avatars_preserved': True,
        'application_volume_preserved': True},
    'snapshot_sha256_before': digest(before), 'snapshot_sha256_after': digest(after),
    'cards': len(before['cards']), 'columns': len(before['columns']), 'members': len(before['members']),
    'media_resources': len(before['media']), 'passed': True}
path = root/'TestResults/ph08-persistence-results.json'
path.write_text(json.dumps(report, indent=2)+'\n')
print(json.dumps(report, indent=2))
