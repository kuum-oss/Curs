#!/usr/bin/env python3
"""Loopback shared-card acceptance with disposable synthetic accounts."""
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
state=root/'TestResults/chat-card-ui-local.json'
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
    return subprocess.run(['docker','compose','exec','-T','sqlserver','sh','-c','SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec /opt/mssql-tools18/bin/sqlcmd "$@"','sharing-check','-S','localhost','-U','sa','-d',values.get('KANBAN_DATABASE','KanbanDB'),'-C','-I','-b','-Q',query],cwd=root,check=True,capture_output=True,text=True,timeout=30)

def cleanup(saved):
    users=saved['users'];ids=[str(uuid.UUID(u['id'])) for u in users]
    assert all(u['email'].startswith('sharing-check-') and u['email'].endswith('@example.test') for u in users)
    literal=','.join("'"+uid+"'" for uid in ids);bid=str(uuid.UUID(saved['boardId']))
    sql(f"""SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRAN;
IF (SELECT COUNT(*) FROM Users WHERE Id IN ({literal}) AND Email LIKE 'sharing-check-%@example.test' AND IsProtectedDemo=0)<>{len(ids)} THROW 50008,'Unsafe shared-card cleanup',1;
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
    cleanup(json.loads(state.read_text()));state.unlink();print('Disposable shared-card accounts cleaned; demos untouched.');raise SystemExit(0)
assert not state.exists(),'Clean previous native QA identities first.'
keep=False
saved=None
password='Test.'+secrets.token_hex(12)+'!'
try:
    for name in ['Олена · картки','Дмитро · картки','Сторонній · картки']:
        accounts.append(call('POST','/api/auth/register',{'email':'sharing-check-'+uuid.uuid4().hex+'@example.test','name':name,'password':password},expected=201))
    a,b,c=accounts
    board=call('POST','/api/boards',{'name':'Картки в чаті','description':'Спільне планування та обговорення'},a,201);bid=board['id'];saved={'users':[x['user'] for x in accounts],'boardId':bid}
    def version():return call('GET',f'/api/boards/{bid}',auth=a)['version']
    call('POST',f'/api/boards/{bid}/members',{'email':b['user']['email'],'expectedBoardVersion':version()},a,201)
    col=call('POST',f'/api/boards/{bid}/columns',{'name':'Заплановано','expectedBoardVersion':version()},a,201)
    cards=[]
    for title in ['Підготувати презентацію','Перевірити інтерфейс']:
        cards.append(call('POST',f'/api/boards/{bid}/cards',{'title':title,'columnId':col['id'],'expectedBoardVersion':version()},a,201))
    chat=call('POST','/api/chats',{'recipientId':b['user']['id']},a);path=f"/api/chats/{chat['id']}/messages"
    body={'sharedCardId':cards[0]['card']['id'],'clientMessageId':str(uuid.uuid4())}
    first=call('POST',path,body,a);retry=call('POST',path,body,a);assert first['id']==retry['id'];assert first['sharedCard']['canOpen'];assert len(call('GET',path,auth=b)['items'])==1;assert call('GET','/api/notifications',auth=b)['unreadCount']==1;checks.append('textless card share and idempotent message/notification')
    target=call('GET',path+f"/{first['id']}/card",auth=b);assert target['cardId']==body['sharedCardId'] and target['boardId']==bid;checks.append('recipient opens the exact authorized card')
    call('POST',path,dict(body,sharedCardId=cards[1]['card']['id']),a,409)
    import urllib.parse
    call('DELETE',f"/api/boards/{bid}/members/{b['user']['id']}?"+urllib.parse.urlencode({'expectedBoardVersion':version()}),auth=a,expected=204)
    hidden=call('GET',path,auth=b)['items'][0]['sharedCard'];assert not hidden['canOpen'] and hidden['cardId'] is None and hidden['boardId'] is None;call('GET',path+f"/{first['id']}/card",auth=b,expected=404)
    second=call('POST',path,{'sharedCardId':cards[1]['card']['id'],'clientMessageId':str(uuid.uuid4())},a)
    call('GET',f'/api/boards/{bid}',auth=b,expected=404);assert not call('GET',path,auth=b)['items'][-1]['sharedCard']['canOpen'];checks.append('revocation hides metadata and sharing does not grant access')
    call('POST',f'/api/boards/{bid}/members',{'email':b['user']['email'],'expectedBoardVersion':version()},a,201)
    assert call('GET',path+f"/{second['id']}/card",auth=b)['cardId']==cards[1]['card']['id'];checks.append('existing link works after explicit access grant')
    call('GET',path+f"/{first['id']}/card",auth=c,expected=404);call('GET',path+f"/{first['id']}/card",expected=401)
    report={'verifiedAtUtc':datetime.now(timezone.utc).isoformat(),'httpRequests':count,'checks':checks,'randomAccounts':3,'demoAccountsChanged':False}
    if args.prepare_ui:
        saved.update({'password':password,'sender':a['user'],'recipient':b['user'],'cardId':cards[0]['card']['id'],'secondCardId':cards[1]['card']['id'],'chatId':chat['id']})
        state.write_text(json.dumps(saved));state.chmod(0o600);keep=True
    (root/'TestResults/chat-card-http.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n');print(json.dumps(report,ensure_ascii=False))
finally:
    if saved and not keep:cleanup(saved)
