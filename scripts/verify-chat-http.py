#!/usr/bin/env python3
"""Loopback chat acceptance using only random, disposable test identities."""
import json
from pathlib import Path
import secrets
import subprocess
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone

root=Path(__file__).resolve().parent.parent
base='http://127.0.0.1:5000'
accounts=[]
board=None
count=0
checks=[]

def call(method,path,data=None,auth=None,expected=200):
    global count
    headers={'Content-Type':'application/json'}
    if auth:headers['Authorization']='Bearer '+auth['accessToken']
    request=urllib.request.Request(base+path,headers=headers,method=method,data=None if data is None else json.dumps(data).encode())
    count+=1
    try:r=urllib.request.urlopen(request,timeout=20)
    except urllib.error.HTTPError as e:r=e
    with r:
        payload=r.read()
        assert r.status==expected,(method,path,r.status,expected)
    return json.loads(payload) if payload else None

def register(name):
    a=call('POST','/api/auth/register',{'email':'chat-check-'+uuid.uuid4().hex+'@example.test','password':'Test.'+secrets.token_hex(12)+'!','name':name},expected=201)
    accounts.append(a)
    return a

def cleanup():
    # Validate both the exact IDs we created and their test-only email prefix.
    ids=[str(uuid.UUID(a['user']['id'])) for a in accounts]
    if not ids:return
    literals=','.join("'"+i+"'" for i in ids)
    sql=f"""SET NOCOUNT ON; BEGIN TRAN;
IF (SELECT COUNT(*) FROM Users WHERE Id IN ({literals}) AND Email LIKE 'chat-check-%@example.test' AND IsProtectedDemo=0)<>{len(ids)} THROW 50004,'Unsafe test cleanup',1;
DELETE FROM Notifications WHERE UserId IN ({literals}); UPDATE Notifications SET ActorId=NULL WHERE ActorId IN ({literals});
DELETE FROM DirectMessages WHERE ChatId IN (SELECT Id FROM DirectChats WHERE FirstUserId IN ({literals}) OR SecondUserId IN ({literals}));
DELETE FROM DirectChats WHERE FirstUserId IN ({literals}) OR SecondUserId IN ({literals});
"""
    if board:
        bid=str(uuid.UUID(board['id']))
        sql+=f"""IF EXISTS(SELECT 1 FROM Boards WHERE Id='{bid}' AND (OwnerId NOT IN ({literals}) OR Name<>'Chat HTTP acceptance')) THROW 50005,'Unsafe board cleanup',1;
DELETE FROM BoardMembers WHERE BoardId='{bid}'; DELETE FROM Columns WHERE BoardId='{bid}'; DELETE FROM Boards WHERE Id='{bid}';
"""
    sql+=f"DELETE FROM Users WHERE Id IN ({literals}); COMMIT;"
    values={}
    for line in (root/'.env').read_text().splitlines():
        if line and not line.startswith('#') and '=' in line:
            k,v=line.split('=',1);values[k]=v.strip().strip(chr(34)+chr(39))
    subprocess.run(['docker','compose','exec','-T','sqlserver','sh','-c','SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec /opt/mssql-tools18/bin/sqlcmd "$@"','chat-check','-S','localhost','-U','sa','-d',values.get('KANBAN_DATABASE','KanbanDB'),'-C','-I','-b','-Q',sql],cwd=root,check=True,capture_output=True,text=True,timeout=30)

try:
    a=register('Chat check A');b=register('Chat check B');outsider=register('Chat check outsider')
    board=call('POST','/api/boards',{'name':'Chat HTTP acceptance'},a,201)
    call('POST',f"/api/boards/{board['id']}/members",{'email':b['user']['email'],'expectedBoardVersion':board['version']},a,201)
    chat=call('POST','/api/chats',{'recipientId':b['user']['id']},a)
    reverse=call('POST','/api/chats',{'recipientId':a['user']['id']},b)
    assert chat['id']==reverse['id'];checks.append('single dialog in both directions')
    path=f"/api/chats/{chat['id']}/messages"
    nonce=str(uuid.uuid4())
    first=call('POST',path,{'text':'Live Docker chat A → B','clientMessageId':nonce},a)
    duplicate=call('POST',path,{'text':'Live Docker chat A → B','clientMessageId':nonce},a)
    assert first['id']==duplicate['id'];checks.append('idempotent retry')
    assert call('GET','/api/chats',auth=b)[0]['unreadCount']==1
    assert call('GET',path,auth=b)['items'][0]['text']==first['text']
    call('POST',f"/api/chats/{chat['id']}/read",{'lastReadMessageId':first['id']},b,204)
    assert call('GET','/api/chats',auth=b)[0]['unreadCount']==0;checks.append('recipient history and unread acknowledgement')
    reply=call('POST',path,{'text':'Live Docker reply B → A','clientMessageId':str(uuid.uuid4())},b)
    assert call('GET',path+f"?afterId={first['id']}",auth=a)['items'][0]['id']==reply['id'];checks.append('reply visible to original sender')
    assert call('GET','/api/chats',auth=outsider)==[]
    call('GET',path,auth=outsider,expected=404)
    call('POST',path,{'text':'Forbidden','clientMessageId':str(uuid.uuid4())},outsider,404)
    call('POST',f"/api/chats/{chat['id']}/read",{'lastReadMessageId':first['id']},outsider,404)
    call('GET','/api/chats',expected=401);checks.append('outsider and anonymous isolation')
finally:
    cleanup()
report={'verifiedAtUtc':datetime.now(timezone.utc).isoformat(),'httpRequests':count,'checks':checks,'randomAccountsCleaned':len(accounts),'existingAccountsChanged':False}
(root/'TestResults/chat-http.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n')
print(json.dumps(report,ensure_ascii=False))
