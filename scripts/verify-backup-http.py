#!/usr/bin/env python3
"""Loopback backup acceptance with disposable synthetic accounts."""
import argparse
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import secrets
import subprocess
import urllib.error
import urllib.request
import uuid
root=Path(__file__).resolve().parent.parent
state=root/'TestResults/backup-ui-local.json'
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--prepare-ui',action='store_true')
parser.add_argument('--cleanup-ui',action='store_true')
args=parser.parse_args()
accounts=[]
board=None
count=0
checks=[]

def call(method,path,data=None,auth=None,expected=200):
    global count
    headers={'Content-Type':'application/json'}
    if auth:headers['Authorization']='Bearer '+auth['accessToken']
    req=urllib.request.Request('http://127.0.0.1:5000'+path,headers=headers,method=method,data=None if data is None else json.dumps(data).encode())
    count+=1
    try:r=urllib.request.urlopen(req,timeout=20)
    except urllib.error.HTTPError as error:r=error
    with r:
        data=r.read()
        assert r.status==expected,(method,path,r.status,expected)
    return json.loads(data) if data else None

def sql(query):
    values={}
    for line in (root/'.env').read_text().splitlines():
        if line and not line.startswith('#') and '=' in line:
            k,v=line.split('=',1);values[k]=v.strip().strip(chr(34)+chr(39))
    return subprocess.run(['docker','compose','exec','-T','sqlserver','sh','-c','SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec /opt/mssql-tools18/bin/sqlcmd "$@"','backup-check','-S','localhost','-U','sa','-d',values.get('KANBAN_DATABASE','KanbanDB'),'-C','-I','-b','-Q',query],cwd=root,check=True,capture_output=True,text=True,timeout=30)

def cleanup(saved):
    users=saved['users'];ids=[str(uuid.UUID(u['id'])) for u in users]
    assert all(u['email'].startswith('backup-check-') and u['email'].endswith('@example.test') for u in users)
    literal=','.join("'"+uid+"'" for uid in ids)
    sql(f"""SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRAN;
IF (SELECT COUNT(*) FROM Users WHERE Id IN ({literal}) AND Email LIKE 'backup-check-%@example.test' AND IsProtectedDemo=0)<>{len(ids)} THROW 50010,'Unsafe backup cleanup',1;
SELECT Id INTO #boards FROM Boards WHERE OwnerId IN ({literal});
DELETE FROM Notifications WHERE UserId IN ({literal}); UPDATE Notifications SET ActorId=NULL WHERE ActorId IN ({literal});
DELETE FROM DirectMessages WHERE ChatId IN (SELECT Id FROM DirectChats WHERE FirstUserId IN ({literal}) OR SecondUserId IN ({literal}));
DELETE FROM DirectChats WHERE FirstUserId IN ({literal}) OR SecondUserId IN ({literal});
SELECT c.Id INTO #cards FROM Cards c JOIN Columns col ON c.ColumnId=col.Id WHERE col.BoardId IN (SELECT Id FROM #boards);
DELETE FROM ChecklistItems WHERE CardId IN (SELECT Id FROM #cards); DELETE FROM CardTags WHERE CardId IN (SELECT Id FROM #cards);
DELETE FROM CardImages WHERE CardId IN (SELECT Id FROM #cards); DELETE FROM Comments WHERE CardId IN (SELECT Id FROM #cards);
DELETE FROM Cards WHERE Id IN (SELECT Id FROM #cards); DELETE FROM Tags WHERE BoardId IN (SELECT Id FROM #boards); DELETE FROM BoardMembers WHERE BoardId IN (SELECT Id FROM #boards);
DELETE FROM Columns WHERE BoardId IN (SELECT Id FROM #boards); DELETE FROM Boards WHERE Id IN (SELECT Id FROM #boards); DELETE FROM Users WHERE Id IN ({literal}); COMMIT;""")

def binary(path,auth):
    global count
    count+=1
    with urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:5000'+path,headers={'Authorization':'Bearer '+auth['accessToken']}),timeout=30) as r:return r.read()
if args.cleanup_ui:
    cleanup(json.loads(state.read_text()));state.unlink();print('Disposable backup boards/accounts cleaned; demos untouched.');raise SystemExit(0)
assert not state.exists(),'Clean previous native QA identities first.'
saved=None;keep=False;password='Test.'+secrets.token_hex(12)+'!'
try:
    for name in ['Копія · перевірка','Колега · перевірка']:
        accounts.append(call('POST','/api/auth/register',{'email':'backup-check-'+uuid.uuid4().hex+'@example.test','name':name,'password':password},expected=201))
    a,b=accounts;saved={'users':[x['user'] for x in accounts]}
    board=call('POST','/api/boards',{'name':'Резервні копії · перевірка','description':'Дані залишаються після відновлення'},a,201);bid=board['id']
    def version():return call('GET',f'/api/boards/{bid}',auth=a)['version']
    call('POST',f'/api/boards/{bid}/members',{'email':b['user']['email'],'expectedBoardVersion':version()},a,201)
    col=call('POST',f'/api/boards/{bid}/columns',{'name':'Заплановано','expectedBoardVersion':version()},a,201)
    tag=call('POST',f'/api/boards/{bid}/tags',{'name':'Документація','color':'#168C98','expectedBoardVersion':version()},a,201)
    card=call('POST',f'/api/boards/{bid}/cards',{'title':'Підготувати презентацію','description':'Опис, з комою.\nДругий рядок','columnId':col['id'],'dueDate':'2026-10-23','assigneeId':b['user']['id'],'tagIds':[tag['id']],'checklist':[{'id':str(uuid.uuid4()),'title':'Перевірити матеріали','isCompleted':True}],'expectedBoardVersion':version()},a,201)['card']
    call('POST',f"/api/cards/{card['id']}/comments",{'text':'Коментар має зберегтися','expectedVersion':card['version'],'expectedBoardVersion':version()},a,201)
    # Upload an existing project-owned generated concept as synthetic image data.
    image=(root/'docs/concepts/task-calendar-2026-10-21.png').read_bytes();card=call('GET',f"/api/cards/{card['id']}",auth=a)['card'];boundary='Kanban'+uuid.uuid4().hex
    parts=[]
    for key,value in {'ExpectedVersion':card['version'],'ExpectedBoardVersion':version()}.items():parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{key}"\r\n\r\n{value}\r\n'.encode())
    parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="File"; filename="test.png"\r\nContent-Type: image/png\r\n\r\n'.encode()+image+f'\r\n--{boundary}--\r\n'.encode())
    count+=1
    with urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:5000'+f"/api/cards/{card['id']}/image",headers={'Authorization':'Bearer '+a['accessToken'],'Content-Type':'multipart/form-data; boundary='+boundary},data=b''.join(parts),method='PUT'),timeout=30) as r:assert r.status==200
    copy=call('GET',f'/api/boards/{bid}/backup',auth=a);encoded=json.dumps(copy,ensure_ascii=False);assert password not in encoded and a['user']['email'] not in encoded
    operation=str(uuid.uuid4());restored=call('POST','/api/backups/restore',{'operationId':operation,'backup':copy},a);retry=call('POST','/api/backups/restore',{'operationId':operation,'backup':copy},a);assert restored['boardId']==retry['boardId'] and retry['alreadyRestored']
    recovered=call('GET',f"/api/boards/{restored['boardId']}/backup",auth=a);c=recovered['data']['cards'][0];source=copy['data']['cards'][0];assert c['title']==source['title'] and c['description']==source['description'] and c['image']==source['image'] and c['checklist']==source['checklist'] and c['comments']==source['comments'] and c['assigneeName']==source['assigneeName']
    call('GET',f"/api/boards/{restored['boardId']}",auth=b,expected=404);assert len(call('GET',f"/api/boards/{restored['boardId']}/members",auth=a))==1
    csv=binary(f'/api/boards/{bid}/export.csv',a);assert csv.startswith(bytes([239,187,191]));import csv as csv_module,io;rows=list(csv_module.reader(io.StringIO(csv.decode('utf-8-sig'))));assert len(rows)==2 and rows[1][2]==source['description']
    broken=dict(copy,checksum='0'*64);call('POST','/api/backups/restore',{'operationId':str(uuid.uuid4()),'backup':broken},a,400);call('GET',f'/api/boards/{bid}/backup',expected=401)
    checks.extend(['whole board image/checklist/comments round trip','retry creates one board','no credential export or implicit membership','UTF-8 CSV newline and comma round trip','corruption and anonymous rejection'])
    report={'verifiedAtUtc':datetime.now(timezone.utc).isoformat(),'httpRequests':count,'checks':checks,'demoAccountsChanged':False}
    if args.prepare_ui:
        saved.update({'password':password,'sender':a['user'],'boardId':bid,'restoredId':restored['boardId'],'cardId':card['id']});state.write_text(json.dumps(saved));state.chmod(0o600);keep=True
    (root/'TestResults/backup-http.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n');print(json.dumps(report,ensure_ascii=False))
finally:
    if saved and not keep:cleanup(saved)
