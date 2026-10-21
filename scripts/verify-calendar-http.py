#!/usr/bin/env python3
"""Loopback calendar acceptance with disposable synthetic accounts."""
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
state=root/'TestResults/calendar-ui-local.json'
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
    return subprocess.run(['docker','compose','exec','-T','sqlserver','sh','-c','SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec /opt/mssql-tools18/bin/sqlcmd "$@"','calendar-check','-S','localhost','-U','sa','-d',values.get('KANBAN_DATABASE','KanbanDB'),'-C','-I','-b','-Q',query],cwd=root,check=True,capture_output=True,text=True,timeout=30)

def cleanup(saved):
    users=saved['users'];ids=[str(uuid.UUID(u['id'])) for u in users]
    assert all(u['email'].startswith('calendar-check-') and u['email'].endswith('@example.test') for u in users)
    literal=','.join("'"+uid+"'" for uid in ids);bid=str(uuid.UUID(saved['boardId']))
    sql(f"""SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRAN;
IF (SELECT COUNT(*) FROM Users WHERE Id IN ({literal}) AND Email LIKE 'calendar-check-%@example.test' AND IsProtectedDemo=0)<>{len(ids)} THROW 50008,'Unsafe calendar cleanup',1;
IF EXISTS(SELECT 1 FROM Boards WHERE Id='{bid}' AND OwnerId NOT IN ({literal})) THROW 50009,'Unsafe board cleanup',1;
DELETE FROM Notifications WHERE UserId IN ({literal}); UPDATE Notifications SET ActorId=NULL WHERE ActorId IN ({literal});
DELETE FROM DirectMessages WHERE ChatId IN (SELECT Id FROM DirectChats WHERE FirstUserId IN ({literal}) OR SecondUserId IN ({literal}));
DELETE FROM DirectChats WHERE FirstUserId IN ({literal}) OR SecondUserId IN ({literal});
SELECT c.Id INTO #cards FROM Cards c JOIN Columns col ON c.ColumnId=col.Id WHERE col.BoardId='{bid}';
DELETE FROM ChecklistItems WHERE CardId IN (SELECT Id FROM #cards); DELETE FROM CardTags WHERE CardId IN (SELECT Id FROM #cards);
DELETE FROM CardImages WHERE CardId IN (SELECT Id FROM #cards); DELETE FROM Comments WHERE CardId IN (SELECT Id FROM #cards);
DELETE FROM Cards WHERE Id IN (SELECT Id FROM #cards); DELETE FROM Tags WHERE BoardId='{bid}'; DELETE FROM BoardMembers WHERE BoardId='{bid}';
DELETE FROM Columns WHERE BoardId='{bid}'; DELETE FROM Boards WHERE Id='{bid}'; DELETE FROM Users WHERE Id IN ({literal}); COMMIT;""")

if args.cleanup_ui:
    cleanup(json.loads(state.read_text()));state.unlink();print('Disposable calendar accounts cleaned; demos untouched.');raise SystemExit(0)
assert not state.exists(),'Clean previous native QA identities first.'
saved=None;keep=False;password='Test.'+secrets.token_hex(12)+'!'
try:
    for name in ['Календар · перевірка','Виконавець · перевірка']:
        accounts.append(call('POST','/api/auth/register',{'email':'calendar-check-'+uuid.uuid4().hex+'@example.test','name':name,'password':password},expected=201))
    a,b=accounts
    board=call('POST','/api/boards',{'name':'Календар · перевірка','description':'Нативне приймання дедлайнів'},a,201);bid=board['id'];saved={'users':[x['user'] for x in accounts],'boardId':bid}
    def version():return call('GET',f'/api/boards/{bid}',auth=a)['version']
    call('POST',f'/api/boards/{bid}/members',{'email':b['user']['email'],'expectedBoardVersion':version()},a,201)
    col=call('POST',f'/api/boards/{bid}/columns',{'name':'Заплановано','expectedBoardVersion':version()},a,201)
    today=datetime.now(timezone.utc).date();due=today+timedelta(days=1)
    card=call('POST',f'/api/boards/{bid}/cards',{'title':'Підготувати презентацію','columnId':col['id'],'dueDate':today.isoformat(),'assigneeId':b['user']['id'],'expectedBoardVersion':version()},a,201)['card']
    undated=call('POST',f'/api/boards/{bid}/cards',{'title':'Оновити документацію','columnId':col['id'],'expectedBoardVersion':version()},a,201)['card']
    path=f'/api/me/calendar?from={today.isoformat()}&to={(today+timedelta(days=7)).isoformat()}'
    page=call('GET',path,auth=a);assert len(page['items'])==2
    row=next(c for c in page['items'] if c['id']==card['id'])
    request={'dueDate':due.isoformat(),'expectedVersion':row['version'],'expectedBoardVersion':row['boardVersion']}
    call('PUT',f"/api/cards/{card['id']}/deadline",request,a,204);call('PUT',f"/api/cards/{card['id']}/deadline",request,a,409)
    updated=call('GET',f"/api/cards/{card['id']}",auth=a)['card'];assert updated['dueDate']==due.isoformat() and updated['title']==card['title']
    assert len(call('GET',path+'&assigneeId='+b['user']['id'],auth=a)['items'])==1
    call('GET',path,expected=401);checks.extend(['range includes scheduled and undated cards','assignee filter','deadline-only change and stale conflict','anonymous denied'])
    report={'verifiedAtUtc':datetime.now(timezone.utc).isoformat(),'httpRequests':count,'checks':checks,'demoAccountsChanged':False}
    if args.prepare_ui:
        saved.update({'password':password,'sender':a['user'],'recipient':b['user'],'cardId':card['id'],'undatedId':undated['id'],'dueDate':due.isoformat()});state.write_text(json.dumps(saved));state.chmod(0o600);keep=True
    (root/'TestResults/calendar-http.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n');print(json.dumps(report,ensure_ascii=False))
finally:
    if saved and not keep:cleanup(saved)
