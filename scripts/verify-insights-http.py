#!/usr/bin/env python3
"""Loopback insights acceptance; scoped disposable accounts, never demo data."""
from pathlib import Path
from datetime import datetime,timedelta,timezone
import json,secrets,uuid,urllib.request,sys
root=Path(__file__).resolve().parent.parent
# Reuse the existing authenticated HTTP/SQL helpers and their guarded cleanup transaction.
shared=root/'scripts/verify-backup-http.py';scope={'__file__':str(shared)}
exec(shared.read_text().split('\nif args.cleanup_ui:')[0].replace('backup-check','insights-check'),scope)
call=scope['call'];sql=scope['sql'];state=root/'TestResults/insights-ui-local.json'
if scope['args'].cleanup_ui:
    scope['cleanup'](json.loads(state.read_text()));state.unlink();print('Only disposable insights accounts and boards cleaned.');raise SystemExit(0)
assert not state.exists(),'Clean previous disposable insights fixture first.'
accounts=[];keep=False;password='Test.'+secrets.token_hex(12)+'!';today=datetime.now().date();now=datetime.now(timezone.utc)
saved={'users':[]}
try:
    for name in ['Анастасія Коваленко','Олена Мельник','Дмитро Бондар','Ірина Петренко']:
        account=call('POST','/api/auth/register',{'email':'insights-check-'+uuid.uuid4().hex+'@example.test','name':name,'password':password},expected=201);accounts.append(account);saved['users'].append(account['user'])
    a=accounts[0];board=call('POST','/api/boards',{'name':'Курсовий проєкт · перевірка','description':'Тимчасовий стенд історії та статистики'},a,201);bid=board['id']
    def version():return call('GET',f'/api/boards/{bid}',auth=a)['version']
    for peer in accounts[1:]:call('POST',f'/api/boards/{bid}/members',{'email':peer['user']['email'],'expectedBoardVersion':version()},a,201)
    columns=[call('POST',f'/api/boards/{bid}/columns',{'name':name,'expectedBoardVersion':version()},a,201) for name in ['Заплановано','У роботі','Перевірка','Готово']]
    call('PUT',f'/api/boards/{bid}/completion-column',{'columnId':columns[3]['id'],'expectedBoardVersion':version()},a,204)
    statements=[];card_ids=[];dates=[]
    for i,count in enumerate([1,2,1,2,3,2,1]):dates.extend([today-timedelta(days=6-i)]*count)
    dates.extend([today-timedelta(days=9)]*9)
    for i in range(29):
        finished=i<21;due=today+timedelta(days=2) if i>=24 else today-timedelta(days=9 if i in [21,22] else 1)
        title=(['Реєстрація користувачів','Модель бази даних','Завантаження фото','Перевірка доступу','JWT-авторизація','Опис вимог'][i%6]+f' · {i+1}')
        card=call('POST',f'/api/boards/{bid}/cards',{'title':title,'description':'Перевірити збереження даних та роботу інтерфейсу.','columnId':columns[1 if i%2 else 0]['id'],'dueDate':due.isoformat(),'priority':i%4,'assigneeId':accounts[i%3]['user']['id'],'expectedBoardVersion':version()},a,201)['card'];cid=card['id'];card_ids.append(cid)
        if finished:
            call('POST',f'/api/cards/{cid}/move',{'targetColumnId':columns[3]['id'],'expectedVersion':card['version'],'expectedBoardVersion':version()},accounts[i%3],200)
            end=datetime.combine(dates[i],datetime.min.time(),tzinfo=timezone.utc)+timedelta(hours=9)
            created=end-timedelta(days=2.4 if i<12 else 3.2)
        else:created=now-timedelta(days=4);end=now
        # Backdate ONLY this disposable fixture to exercise a known two-week time series.
        # Genuine API mutations create the audit records; normal user data is never rewritten.
        created_sql=created.strftime('%Y-%m-%dT%H:%M:%S');end_sql=end.strftime('%Y-%m-%dT%H:%M:%S')
        statements.append(f"UPDATE Cards SET CreatedAtUtc='{created_sql}' WHERE Id='{cid}' AND ColumnId IN (SELECT Id FROM Columns WHERE BoardId='{bid}'); UPDATE BoardActivities SET CardCreatedAtUtc='{created_sql}',CreatedAtUtc=CASE WHEN Field='created' THEN '{created_sql}' ELSE '{end_sql}' END,BeforeStateJson=JSON_MODIFY(BeforeStateJson,'$.Created','{created_sql}'),AfterStateJson=JSON_MODIFY(AfterStateJson,'$.Created','{created_sql}') WHERE BoardId='{bid}' AND CardId='{cid}';")
    statements.append(f"UPDATE Boards SET InsightsSinceUtc='{(now-timedelta(days=30)).strftime('%Y-%m-%dT%H:%M:%S')}' WHERE Id='{bid}';")
    sql('SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRAN; '+''.join(statements)+' COMMIT;')
    # The four most recent events exercise the exact fields in the requested audit view.
    cid=card_ids[-1];current=call('GET',f'/api/cards/{cid}',auth=a)['card'];
    call('PUT',f'/api/cards/{cid}/deadline',{'dueDate':(today+timedelta(days=3)).isoformat(),'expectedVersion':current['version'],'expectedBoardVersion':version()},accounts[1],204)
    current=call('GET',f'/api/cards/{cid}',auth=a)['card']
    call('PUT',f'/api/cards/{cid}',{'title':current['title'],'description':'Перевірити збереження даних та роботу інтерфейсу.','dueDate':current['dueDate'],'priority':3,'assigneeId':accounts[2]['user']['id'],'tagIds':[],'expectedVersion':current['version'],'expectedBoardVersion':version()},a,200)
    current=call('GET',f'/api/cards/{cid}',auth=a)['card'];call('POST',f'/api/cards/{cid}/move',{'targetColumnId':columns[2]['id'],'expectedVersion':current['version'],'expectedBoardVersion':version()},accounts[2],200)
    stats=call('GET',f'/api/boards/{bid}/statistics?today={today.isoformat()}&utcOffsetMinutes=180',auth=a)
    assert stats['completed']==12 and stats['previousCompleted']==9 and stats['inProgress']==8 and stats['overdue']==3,stats
    assert sum(d['count'] for d in stats['days'])==12 and abs(stats['meanDays']-2.4)<.001
    events=call('GET',f'/api/boards/{bid}/history?cardId={cid}',auth=a);assert all(e['cardId']==cid for e in events['items'])
    call('GET',f'/api/boards/{bid}/history',expected=401)
    # Set native-only fixture portraits from project-owned assets.
    for account,slug in zip(accounts,['anastasiia','olena','dmytro','iryna']):
        image=(root/f'assets/demo/avatar-{slug}.png').read_bytes();boundary='Kanban'+uuid.uuid4().hex
        profile=call('GET','/api/auth/me',auth=account)
        raw=f'--{boundary}\r\nContent-Disposition: form-data; name="ExpectedVersion"\r\n\r\n{profile["version"]}\r\n--{boundary}\r\nContent-Disposition: form-data; name="File"; filename="avatar.png"\r\nContent-Type: image/png\r\n\r\n'.encode()+image+f'\r\n--{boundary}--\r\n'.encode()
        req=urllib.request.Request('http://127.0.0.1:5000/api/profile/avatar',headers={'Authorization':'Bearer '+account['accessToken'],'Content-Type':'multipart/form-data; boundary='+boundary},data=raw,method='PUT')
        with urllib.request.urlopen(req,timeout=20) as r:assert r.status==200
    saved.update({'sender':a['user'],'password':password,'boardId':bid,'cardId':cid,'columns':[c['id'] for c in columns]})
    report={'verifiedAtUtc':now.isoformat(),'completed':12,'previousCompleted':9,'inProgress':8,'overdue':3,'meanDays':stats['meanDays'],'seriesTotal':sum(d['count'] for d in stats['days']),'httpRequests':scope['count']+4,'syntheticHistoricalFixture':True,'demoAccountsChanged':False}
    (root/'TestResults/insights-http.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report))
    if scope['args'].prepare_ui:state.write_text(json.dumps(saved));state.chmod(0o600);keep=True
finally:
    if saved['users'] and not keep:scope['cleanup'](saved)
