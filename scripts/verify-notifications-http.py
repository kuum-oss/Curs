#!/usr/bin/env python3
"""Loopback notification acceptance; only disposable synthetic accounts are mutated."""
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
state=root/'TestResults/notifications-ui-local.json'
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
    return subprocess.run(['docker','compose','exec','-T','sqlserver','sh','-c','SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec /opt/mssql-tools18/bin/sqlcmd "$@"','notification-check','-S','localhost','-U','sa','-d',values.get('KANBAN_DATABASE','KanbanDB'),'-C','-I','-b','-Q',query],cwd=root,check=True,capture_output=True,text=True,timeout=30)

def cleanup(saved):
    users=saved['users'];ids=[str(uuid.UUID(u['id'])) for u in users]
    assert all(u['email'].startswith('notification-check-') and u['email'].endswith('@example.test') for u in users)
    literal=','.join("'"+uid+"'" for uid in ids);bid=str(uuid.UUID(saved['boardId']))
    sql(f"""SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRAN;
IF (SELECT COUNT(*) FROM Users WHERE Id IN ({literal}) AND Email LIKE 'notification-check-%@example.test' AND IsProtectedDemo=0)<>{len(ids)} THROW 50008,'Unsafe notification cleanup',1;
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
    cleanup(json.loads(state.read_text()));state.unlink();print('Disposable notification identities cleaned; demos untouched.');raise SystemExit(0)
assert not state.exists(),'Clean previous native QA identities first.'
keep=False
password='Test.'+secrets.token_hex(12)+'!'
saved=None
try:
    for name in ['Олена · перевірка','Центр сповіщень','Сторонній · перевірка']:
        accounts.append(call('POST','/api/auth/register',{'email':'notification-check-'+uuid.uuid4().hex+'@example.test','name':name,'password':password},expected=201))
    a,b,c=accounts
    board=call('POST','/api/boards',{'name':'Перевірка сповіщень','description':'Події карток і особистих повідомлень'},a,201);bid=board['id']
    saved={'users':[x['user'] for x in accounts],'boardId':bid}
    def version():return call('GET',f'/api/boards/{bid}',auth=a)['version']
    call('POST',f'/api/boards/{bid}/members',{'email':b['user']['email'],'expectedBoardVersion':version()},a,201)
    col=call('POST',f'/api/boards/{bid}/columns',{'name':'Заплановано','expectedBoardVersion':version()},a,201)
    tomorrow=(datetime.now(timezone.utc)+timedelta(days=1)).date().isoformat()
    card=call('POST',f'/api/boards/{bid}/cards',{'title':'Підготувати презентацію','columnId':col['id'],'assigneeId':b['user']['id'],'dueDate':tomorrow,'expectedBoardVersion':version()},a,201);cid=card['card']['id']
    comment=call('POST',f'/api/cards/{cid}/comments',{'text':'@'+b['user']['email']+' Перегляньте матеріали презентації.','expectedBoardVersion':version(),'expectedVersion':card['card']['version']},a,201)
    chat=call('POST','/api/chats',{'recipientId':b['user']['id']},a)
    message={'text':'Можеш переглянути зміни?','clientMessageId':str(uuid.uuid4())}
    first=call('POST',f"/api/chats/{chat['id']}/messages",message,a);retry=call('POST',f"/api/chats/{chat['id']}/messages",message,a);assert first['id']==retry['id']
    rows=call('GET','/api/notifications',auth=b);assert rows['unreadCount']==4 and {n['kind'] for n in rows['items']}=={0,1,2,3};checks.append('four persisted notification types without message retry duplicates')
    again=call('GET','/api/notifications',auth=b);assert len(again['items'])==4;checks.append('deadline generation is idempotent')
    assignment=next(n for n in rows['items'] if n['kind']==0)
    opened=call('POST',f"/api/notifications/{assignment['id']}/open",auth=b);assert opened['cardId']==cid and opened['boardId']==bid and opened['isRead'];assert call('GET','/api/notifications?unreadOnly=true',auth=b)['unreadCount']==3;checks.append('card deep link and read state')
    call('POST',f"/api/notifications/{assignment['id']}/open",auth=c,expected=404);call('POST',f"/api/notifications/{assignment['id']}/read",auth=c,expected=404);call('GET','/api/notifications',expected=401);assert call('GET','/api/notifications',auth=c)['items']==[];checks.append('recipient isolation and anonymous rejection')
    msg=next(n for n in rows['items'] if n['kind']==3);assert call('POST',f"/api/notifications/{msg['id']}/open",auth=b)['chatId']==chat['id'];checks.append('chat deep link')
    call('POST','/api/notifications/read-all',{'throughId':rows['latestId']},b,204);assert call('GET','/api/notifications',auth=b)['unreadCount']==0;checks.append('read all persisted')
    report={'verifiedAtUtc':datetime.now(timezone.utc).isoformat(),'httpRequests':count,'checks':checks,'demoAccountsChanged':False,'randomAccounts':len(accounts)}
    if args.prepare_ui:
        uid=str(uuid.UUID(b['user']['id']))
        sql(f"UPDATE Notifications SET ReadAtUtc=NULL WHERE UserId='{uid}';")
        saved.update({'password':password,'recipient':b['user'],'actor':a['user'],'cardId':cid,'chatId':chat['id']})
        state.write_text(json.dumps(saved));state.chmod(0o600);keep=True
    (root/'TestResults/notifications-http.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n');print(json.dumps(report,ensure_ascii=False))
finally:
    if saved and not keep:cleanup(saved)
