#!/usr/bin/env python3
"""Exercise a native API over HTTP with an isolated database; optionally keep it for GUI checks."""
from pathlib import Path
import json
from datetime import date, timedelta
import re
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
env['ASPNETCORE_URLS'] = 'http://127.0.0.1:5046'
env['ConnectionStrings__Default'] = f'Server=localhost,3038;Database={database};User Id=sa;Password="{password}";TrustServerCertificate=True;Connect Timeout=5'
results = []
today = date.today()
# Generate disposable binary fixtures from the pinned server imaging package.
# This helper is deliberately outside the solution and remains ignored in TestResults.
fixture_tool = output / 'FixtureMaker'
fixture_tool.mkdir(exist_ok=True)
(fixture_tool / 'FixtureMaker.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include="../../KanbanApi/KanbanApi.csproj" /></ItemGroup></Project>\n')
(fixture_tool / 'Program.cs').write_text('using ImageMagick;\nusing ImageMagick.Drawing;\nvar folder=Path.GetFullPath("TestResults/ph06-fixtures");Directory.CreateDirectory(folder);\nforeach(var format in new[]{MagickFormat.Jpeg,MagickFormat.Png,MagickFormat.WebP})\n{\n using var image=new MagickImage(MagickColors.CornflowerBlue,900,600);\n new Drawables().FillColor(MagickColors.Gold).Rectangle(90,90,420,510).FillColor(MagickColors.Teal).Circle(660,300,810,300).Draw(image);\n image.Strip();File.WriteAllBytes(Path.Combine(folder,"cover."+(format==MagickFormat.Jpeg?"jpg":format==MagickFormat.Png?"png":"webp")),image.ToByteArray(format));\n}\nFile.WriteAllText(Path.Combine(folder,"corrupt.png"),"not an image");File.WriteAllBytes(Path.Combine(folder,"oversized.png"),new byte[5*1024*1024+1]);Console.WriteLine("Disposable GUI image fixtures created.");\n')
with (output / 'ph06-fixture-build.log').open('w') as fixture_log:
    generated = subprocess.run([str(root / 'scripts/dotnet.sh'), 'run', '--project', str(fixture_tool / 'FixtureMaker.csproj')],cwd=root,stdout=fixture_log,stderr=subprocess.STDOUT)
if generated.returncode:
    raise RuntimeError('Image fixture generation failed; see ignored TestResults/ph06-fixture-build.log.')


def request(method, path, data=None, token=None, expected=200, extra_headers=None):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    headers.update(extra_headers or {})
    payload = data if isinstance(data, bytes) else None if data is None else json.dumps(data).encode()
    req = urllib.request.Request('http://127.0.0.1:5046' + path, data=payload, headers=headers, method=method)
    started = time.monotonic()
    if payload is not None and len(payload) > 5*1024*1024+64*1024:
        # curl reads an early 413 while the server closes the upload stream.
        # Authorization stays on stdin, not in process arguments or reports.
        upload_path=output/'ph06-large-body.bin';head_path=output/'ph06-large-response.headers';reply_path=output/'ph06-large-response.json'
        upload_path.write_bytes(payload)
        config=['url = "http://127.0.0.1:5046'+path+'"','request = "'+method+'"','data-binary = "@'+str(upload_path)+'"']
        config += ['header = "'+key+': '+value+'"' for key,value in headers.items()]
        result=subprocess.run(['curl','--silent','--show-error','--config','-','--dump-header',str(head_path),'--output',str(reply_path)],input=('\n'.join(config)+'\n').encode(),stdout=subprocess.PIPE,stderr=subprocess.PIPE)
        try:
            statuses=re.findall(r'^HTTP/\S+ (\d+)',head_path.read_text(),re.M)
            assert statuses and int(statuses[-1])==expected, 'Oversized native upload did not return expected status.'
            body=json.loads(reply_path.read_text());results.append({'method':method,'route':path,'status':int(statuses[-1]),'duration_ms':round((time.monotonic()-started)*1000),'code':body.get('code')});return body
        finally:
            upload_path.unlink(missing_ok=True);head_path.unlink(missing_ok=True);reply_path.unlink(missing_ok=True)
    try:
        response = urllib.request.urlopen(req, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    raw = response.read()
    body = raw if response.headers.get_content_type().startswith('image/') else json.loads(raw) if raw else {}
    assert response.status == expected, f'{path}: expected {expected}, got {response.status}'
    # Never write access tokens or request passwords to results.
    results.append({'method': method, 'route': path, 'status': response.status,
                    'duration_ms': round((time.monotonic() - started) * 1000), 'code': body.get('code') if isinstance(body, dict) else None})
    return body


with (output / 'ph06-api.log').open('w') as log:
    process = subprocess.Popen([str(root / 'scripts/dotnet.sh'), str(root / 'KanbanApi/bin/Debug/net10.0/KanbanApi.dll')],
                               cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
    try:
        for attempt in range(100):
            if process.poll() is not None:
                raise RuntimeError('API stopped; see ignored TestResults/ph06-api.log.')
            try:
                urllib.request.urlopen('http://127.0.0.1:5046/health/ready', timeout=1)
                break
            except (OSError, urllib.error.URLError):
                time.sleep(0.3)
        else:
            raise RuntimeError('API did not become ready.')
        def register(email, name):
            return request('POST', '/api/auth/register', {'email':email, 'name':name, 'password':'test-password123!'}, expected=201)
        owner = register('ph06-admin@example.test', 'Олена Адміністратор')
        person = register('ph06-member@example.test', 'Іван Учасник')
        outsider = register('ph06-outsider@example.test', 'Сторонній')
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
        # PH-06 content operations, preserving one synthetic board for optional GUI verification.
        board = request('POST','/api/boards',{'name':'Курсова PH-06','description':'Колонки, картки та коментарі'},admin,201)
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
        fields={'title':'Реалізувати редактор картки','description':'Перевірити всі поля та збереження','dueDate':(today+timedelta(days=30)).isoformat(),'priority':3,'assigneeId':owner['user']['id'],'tagIds':[tag['id']]}
        card=request('POST',base+'/cards',dict(fields,columnId=col['id'],expectedBoardVersion=fresh()),admin,201)
        detail='/api/cards/'+card['card']['id']
        request('GET',detail,token=stranger,expected=404)
        request('GET',detail+'/comments',expected=401)
        request('PUT',detail,dict(fields,assigneeId=outsider['user']['id'],expectedBoardVersion=fresh(),expectedVersion=card['card']['version']),admin,400)
        request('POST',detail+'/comments',{'text':'Початковий коментар з авторами й датою','expectedBoardVersion':fresh(),'expectedVersion':card['card']['version']},admin,201)
        latest=request('GET',detail,token=admin)
        assert latest['card']['version']!=card['card']['version']
        request('PUT',detail,dict(fields,expectedBoardVersion=fresh(),expectedVersion=card['card']['version']),admin,409)
        card=request('PUT',detail,dict(fields,title='Редактор картки — PH-06',expectedBoardVersion=fresh(),expectedVersion=latest['card']['version']),admin)
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

        # Filters combine using the local calendar day and retain stored positions.
        tags=request('GET',base+'/tags',token=admin)
        combined='&'.join(['search='+urllib.parse.quote('РЕДАКТОР'), 'priority=Critical','tagId='+tags[0]['id'],'assigneeId='+owner['user']['id'],'deadline=Today','today='+fields['dueDate']])
        assert request('GET',base+'/cards?'+combined,token=admin)['totalCount']==1
        assert request('GET',base+'/cards?'+combined.replace('Critical','Low'),token=admin)['totalCount']==0
        assert request('GET',base+'/cards?search='+urllib.parse.quote('%_'),token=admin)['totalCount']==0
        for query in ['deadline=Today','priority=9','deadline=9&today=2026-10-14','today=bad','search='+'x'*201]:request('GET',base+'/cards?'+query,token=admin,expected=400)
        for name,day in [('Прострочене завдання',(today-timedelta(days=1)).isoformat()),('Завдання на сьогодні',today.isoformat()),('Майбутнє завдання',(today+timedelta(days=1)).isoformat())]:
            request('POST',base+'/cards',{'title':name,'columnId':col['id'],'priority':2,'dueDate':day,'expectedBoardVersion':fresh()},admin,201)
        for mode,title in [('Overdue','Прострочене завдання'),('Today','Завдання на сьогодні')]:
            assert request('GET',base+'/cards?deadline='+mode+'&today='+today.isoformat(),token=admin)['items'][0]['title']==title
        assert request('GET',base+'/cards?deadline=Upcoming&today='+today.isoformat(),token=admin)['totalCount']==2
        image_path=detail+'/image'
        def upload(data,who=admin,expected=200,cv=None,bv=None,duplicates=False):
            current=request('GET',detail,token=admin)
            boundary='KanbanPh06Boundary'
            body=b''
            for key,value in [('ExpectedVersion',cv or current['card']['version']),('ExpectedBoardVersion',bv or fresh())]:
                body+=('--'+boundary+'\r\nContent-Disposition: form-data; name="'+key+'"\r\n\r\n'+value+'\r\n').encode()
            for _ in range(2 if duplicates else 1):
                body+=('--'+boundary+'\r\nContent-Disposition: form-data; name="File"; filename="renamed.txt"\r\nContent-Type: application/octet-stream\r\n\r\n').encode()+data+b'\r\n'
            body+=('--'+boundary+'--\r\n').encode()
            return request('PUT',image_path,body,who,expected,{'Content-Type':'multipart/form-data; boundary='+boundary})
        request('GET',image_path,token=admin,expected=404)
        samples=output/'ph06-fixtures'
        for extension in ['jpg','png','webp']:
            data=(samples/('cover.'+extension)).read_bytes();upload(data)
            assert request('GET',image_path,token=admin)==data
            preview=request('GET',image_path+'/thumbnail',token=admin);assert preview.startswith(b'\x89PNG\r\n\x1a\n')
            assert request('GET',detail,token=admin)['card']['hasImage']
        current=request('GET',detail,token=admin);version=fresh();original=request('GET',image_path,token=admin)
        for data,status in [(b'',400),(b'not image',415),((samples/'cover.png').read_bytes()[:-1],400),((samples/'oversized.png').read_bytes(),413)]:
            upload(data,expected=status);assert fresh()==version;assert request('GET',image_path,token=admin)==original
        upload(bytes(5*1024*1024+64*1024+1),expected=413);assert fresh()==version
        upload(original,expected=409,cv='AAAAAAAAAAA=');assert fresh()==version
        upload(original,expected=400,duplicates=True);assert fresh()==version
        upload(original,who=stranger,expected=404)
        upload(original,who=None,expected=401)
        for route in [image_path,image_path+'/thumbnail']:
            request('GET',route,token=stranger,expected=404);request('GET',route,expected=401)
        upload(original,who=member)
        latest=request('GET',detail,token=admin)
        image_query='?expectedBoardVersion='+urllib.parse.quote(fresh(),safe='')+'&expectedVersion='+urllib.parse.quote(latest['card']['version'],safe='')
        request('DELETE',image_path+image_query,token=stranger,expected=404)
        request('DELETE',image_path+image_query,expected=401)
        request('DELETE',image_path+image_query,token=admin,expected=204)
        request('GET',image_path,token=admin,expected=404)
        assert not request('GET',detail,token=admin)['card']['hasImage']
        upload((samples/'cover.png').read_bytes())
        (output / 'ph06-http.json').write_text(json.dumps({'database':database,'checks':results},indent=2))
        print(f'Native API board/content/ordering/filter/image checks passed ({len(results)}); credentials omitted from report.', flush=True)
        if '--gui' in sys.argv:
            print('GUI verification API ready at http://127.0.0.1:5046; interrupt to stop and remove its temporary database.', flush=True)
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
