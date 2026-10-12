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
database = 'KanbanPh05_' + uuid.uuid4().hex
env = os.environ.copy()
env['ASPNETCORE_ENVIRONMENT'] = 'Development'
env['ASPNETCORE_URLS'] = 'http://127.0.0.1:5045'
env['ConnectionStrings__Default'] = f'Server=localhost,3038;Database={database};User Id=sa;Password="{password}";TrustServerCertificate=True;Connect Timeout=5'
results = []


def request(method, path, data=None, token=None, expected=200):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    payload = None if data is None else json.dumps(data).encode()
    req = urllib.request.Request('http://127.0.0.1:5045' + path, data=payload, headers=headers, method=method)
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


with (output / 'ph05-api.log').open('w') as log:
    process = subprocess.Popen([str(root / 'scripts/dotnet.sh'), str(root / 'KanbanApi/bin/Debug/net10.0/KanbanApi.dll')],
                               cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
    try:
        for attempt in range(100):
            if process.poll() is not None:
                raise RuntimeError('API stopped; see ignored TestResults/ph05-api.log.')
            try:
                urllib.request.urlopen('http://127.0.0.1:5045/health/ready', timeout=1)
                break
            except (OSError, urllib.error.URLError):
                time.sleep(0.3)
        else:
            raise RuntimeError('API did not become ready.')
        def register(email, name):
            return request('POST', '/api/auth/register', {'email':email, 'name':name, 'password':'test-password123!'}, expected=201)
        owner = register('ph05-admin@example.test', 'Олена Адміністратор')
        person = register('ph05-member@example.test', 'Іван Учасник')
        outsider = register('ph05-outsider@example.test', 'Сторонній')
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
        # PH-05 content operations, preserving one synthetic board for optional GUI verification.
        board = request('POST','/api/boards',{'name':'Курсова PH-05','description':'Колонки, картки та коментарі'},admin,201)
        base = '/api/boards/' + board['id']
        def fresh():
            return request('GET',base,token=admin)['version']
        def resource_query(version):
            return '?expectedBoardVersion='+urllib.parse.quote(fresh(),safe='')+'&expectedVersion='+urllib.parse.quote(version,safe='')
        col = request('POST',base+'/columns',{'name':'Заплановано','expectedBoardVersion':fresh()},admin,201)
        empty = request('POST',base+'/columns',{'name':'Порожня','expectedBoardVersion':fresh()},admin,201)
        col = request('PUT',base+'/columns/'+col['id'],{'name':'В роботі','expectedBoardVersion':fresh(),'expectedVersion':col['version']},admin)
        request('GET',base+'/columns',token=admin)
        tag = request('POST',base+'/tags',{'name':'Курсова','color':'#2563EB','expectedBoardVersion':fresh()},admin,201)
        request('GET',base+'/tags',token=admin)
        fields={'title':'Реалізувати редактор картки','description':'Перевірити всі поля та збереження','dueDate':'2026-12-31','priority':3,'assigneeId':owner['user']['id'],'tagIds':[tag['id']]}
        card=request('POST',base+'/cards',dict(fields,columnId=col['id'],expectedBoardVersion=fresh()),admin,201)
        detail='/api/cards/'+card['card']['id']
        request('GET',detail,token=stranger,expected=404)
        request('GET',detail+'/comments',expected=401)
        request('PUT',detail,dict(fields,assigneeId=outsider['user']['id'],expectedBoardVersion=fresh(),expectedVersion=card['card']['version']),admin,400)
        request('POST',detail+'/comments',{'text':'Початковий коментар з авторами й датою','expectedBoardVersion':fresh(),'expectedVersion':card['card']['version']},admin,201)
        latest=request('GET',detail,token=admin)
        assert latest['card']['version']!=card['card']['version']
        request('PUT',detail,dict(fields,expectedBoardVersion=fresh(),expectedVersion=card['card']['version']),admin,409)
        card=request('PUT',detail,dict(fields,title='Редактор картки — PH-05',expectedBoardVersion=fresh(),expectedVersion=latest['card']['version']),admin)
        request('GET',detail+'/comments?offset=0&limit=1',token=admin)
        page=request('GET',base+'/cards?offset=0&limit=1',token=admin)
        assert page['totalCount']==1 and page['items'][0]['title']==card['card']['title']
        request('POST',base+'/members',{'email':person['user']['email'],'expectedBoardVersion':fresh()},admin,201)
        request('PATCH',base+'/members/'+person['user']['id'],{'canCreateCards':False,'canDeleteCards':False,'expectedBoardVersion':fresh()},admin)
        request('POST',base+'/cards',{'title':'Заборонено','columnId':col['id'],'expectedBoardVersion':fresh()},member,403)
        request('DELETE',base+'/columns/'+col['id']+resource_query(col['version']),token=member,expected=403)
        request('DELETE',detail+resource_query(card['card']['version']),token=member,expected=403)
        request('DELETE',base+'/columns/'+empty['id']+resource_query(empty['version']),token=member,expected=204)
        request('POST',detail+'/comments',{'text':'Коментар другого автора','expectedBoardVersion':fresh(),'expectedVersion':card['card']['version']},member,201)
        card=request('GET',detail,token=admin)
        thread=request('GET',detail+'/comments',token=member)
        assert len({item['author']['id'] for item in thread['items']})==2
        disposable=request('POST',base+'/cards',{'title':'Тимчасова картка','columnId':col['id'],'expectedBoardVersion':fresh()},admin,201)
        request('DELETE','/api/cards/'+disposable['card']['id']+resource_query(disposable['card']['version']),token=admin,expected=204)
        assert request('GET',base+'/cards',token=admin)['totalCount']==1
        # Versioned ordering operations; leave populated columns for two native GUI clients.
        target=request('POST',base+'/columns',{'name':'Готово','expectedBoardVersion':fresh()},admin,201)
        review=request('POST',base+'/columns',{'name':'Огляд','expectedBoardVersion':fresh()},admin,201)
        second=request('POST',base+'/cards',{'title':'Друга картка','columnId':col['id'],'expectedBoardVersion':fresh()},admin,201)
        third=request('POST',base+'/cards',{'title':'Третя картка','columnId':col['id'],'expectedBoardVersion':fresh()},admin,201)
        def move(item,target_id,before=None,who=admin,expected=200):
            return request('POST','/api/cards/'+item['card']['id']+'/move',{'targetColumnId':target_id,'beforeCardId':before,'expectedBoardVersion':fresh(),'expectedVersion':item['card']['version']},who,expected)
        moved=move(second,target['id']);second=request('GET','/api/cards/'+second['card']['id'],token=admin)
        move(second,target['id'],second['card']['id'],expected=400)
        request('PUT',base+'/columns/order',{'columnIds':[target['id'],col['id'],review['id']],'expectedBoardVersion':fresh()},member)
        request('PUT',base+'/columns/order',{'columnIds':[col['id'],col['id'],review['id']],'expectedBoardVersion':fresh()},admin,400)
        request('PUT',base+'/columns/order',{'columnIds':[col['id'],target['id'],review['id']],'expectedBoardVersion':fresh()},admin)
        card=request('GET',detail,token=admin)
        moved=move(card,target['id'],second['card']['id'],who=member)
        card=request('GET',detail,token=admin)
        move(card,col['id'],third['card']['id'])
        card=request('GET',detail,token=admin)
        request('POST',detail+'/move',{'targetColumnId':target['id'],'expectedBoardVersion':fresh(),'expectedVersion':'AAAAAAAAAAA='},admin,409)
        other=request('POST','/api/boards',{'name':'Інша дошка'},admin,201)
        other_col=request('POST','/api/boards/'+other['id']+'/columns',{'name':'Чужа колонка','expectedBoardVersion':other['version']},admin,201)
        move(card,other_col['id'],expected=404)
        other=request('GET','/api/boards/'+other['id'],token=admin)
        request('DELETE','/api/boards/'+other['id']+'?expectedBoardVersion='+urllib.parse.quote(other['version'],safe=''),token=admin,expected=204)
        for name in ['Тестування','Архів','Черга']:
            request('POST',base+'/columns',{'name':name,'expectedBoardVersion':fresh()},admin,201)
        columns=request('GET',base+'/columns',token=admin)
        page=request('GET',base+'/cards',token=admin)
        for column in columns:
            rows=[item for item in page['items'] if item['columnId']==column['id']]
            assert [item['position'] for item in rows]==list(range(len(rows)))
        (output / 'ph05-http.json').write_text(json.dumps({'database':database,'checks':results},indent=2))
        print(f'Native API board/content/ordering checks passed ({len(results)}); credentials omitted from report.', flush=True)
        if '--gui' in sys.argv:
            print('GUI verification API ready at http://127.0.0.1:5045; interrupt to stop and remove its temporary database.', flush=True)
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
