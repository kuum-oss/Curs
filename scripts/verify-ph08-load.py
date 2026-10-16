#!/usr/bin/env python3
"""20 real users against the production Docker image and an isolated MSSQL database.
The existing application volume and its databases are never reset.
"""
from concurrent.futures import ThreadPoolExecutor
from datetime import date,timedelta
from pathlib import Path
import json,os,secrets,socket,subprocess,threading,time,urllib.error,urllib.parse,urllib.request,uuid
root=Path(__file__).resolve().parent.parent
out=root/'TestResults';out.mkdir(exist_ok=True)
run='ph08'+uuid.uuid4().hex[:12];database='KanbanLoad_'+run;container='kanban-load-'+run
settings={}
for line in (root/'.env').read_text().splitlines():
 if '=' in line and not line.lstrip().startswith('#'):
  k,v=line.split('=',1);settings[k]=v.strip().strip('"').strip("'")
sock=socket.socket();sock.bind(('127.0.0.1',0));port=sock.getsockname()[1];sock.close();base='http://127.0.0.1:'+str(port)
envfile=out/'ph08-load.env'
envfile.write_text('\n'.join(['ASPNETCORE_HTTP_PORTS=8080','Database__ApplyMigrationsOnStartup=true','ConnectionStrings__Default=Server=sqlserver,1433;Database='+database+';User Id=sa;Password="'+settings['MSSQL_SA_PASSWORD']+'";TrustServerCertificate=True;','Jwt__Key='+secrets.token_hex(32),'Jwt__Issuer=KanbanBoard.Api','Jwt__Audience=KanbanBoard.Desktop','Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Warning'])+'\n');envfile.chmod(0o600)
report={'phase':'PH-08','users':20,'database':database,'container':container,'requests':[],'checks':[],'scenarios':{},'started_at_utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())}
lock=threading.Lock();stage='setup'
def command(args,check=True,**kwargs):return subprocess.run(args,cwd=root,check=check,capture_output=True,text=True,**kwargs)
def http(method,path,data=None,token=None,expected=200,record=True):
 headers={'Content-Type':'application/json'}
 if token:headers['Authorization']='Bearer '+token
 req=urllib.request.Request(base+path,data=None if data is None else json.dumps(data).encode(),headers=headers,method=method);start=time.perf_counter()
 try:r=urllib.request.urlopen(req,timeout=30)
 except urllib.error.HTTPError as e:r=e
 with r:raw=r.read();status=r.status
 ms=round((time.perf_counter()-start)*1000,3)
 if record:
  with lock:report['requests'].append({'scenario':stage,'method':method,'path':path,'status':status,'ms':ms})
 assert status in ((expected,) if isinstance(expected,int) else expected),(method,path,status,raw[:400])
 return json.loads(raw) if raw else None

def check(name,condition=True):
 assert condition,name
 report['checks'].append(name);print('PASS',name,flush=True)
def board(id,token):return http('GET','/api/boards/'+id,token=token)
def wave(fn):
 barrier=threading.Barrier(20)
 def work(i):barrier.wait(timeout=30);return fn(i)
 with ThreadPoolExecutor(max_workers=20) as pool:return list(pool.map(work,range(20)))
def pct(values,p):return sorted(values)[max(0,__import__('math').ceil(len(values)*p)-1)]
report['revision']=command(['git','rev-parse','HEAD']).stdout.strip()
report['api_image_id']=command(['docker','image','inspect','kanbanboard-api:local','--format','{{.Id}}']).stdout.strip()
report['docker_resources']=json.loads(command(['docker','info','--format','{{json .}}']).stdout)
report['docker_resources']={k:report['docker_resources'].get(k) for k in ['Architecture','NCPU','MemTotal','OperatingSystem','ServerVersion']}
created=False
try:
 command(['docker','run','-d','--name',container,'--network','kanbanboard_kanban-network','--env-file',str(envfile),'-p',f'127.0.0.1:{port}:8080','--ulimit','core=0','kanbanboard-api:local']);created=True
 for attempt in range(90):
  try:http('GET','/health/ready',record=False);break
  except Exception:
   if attempt==89:raise
   time.sleep(1)
 users=[];private=[];columns=[];cards=[]
 password='Load.'+secrets.token_hex(12)+'!'
 for i in range(20):
  email=f'{run}.{i}@example.test';auth=http('POST','/api/auth/register',{'email':email,'name':f'Навантаження {i+1}','password':password},expected=201)
  # Login is measured separately with one arrival at a time; the workload then uses 20 concurrent sessions.
  login=http('POST','/api/auth/login',{'email':email,'password':password});users.append(login)
  token=login['accessToken'];b=http('POST','/api/boards',{'name':f'Навантаження {i+1}'},token,201);private.append(b['id'])
  cols=[]
  for name in ['План','Виконано']:
   current=board(b['id'],token);cols.append(http('POST',f'/api/boards/{b["id"]}/columns',{'name':name,'expectedBoardVersion':current['version']},token,201))
  columns.append(cols)
 owner=users[0]['accessToken'];shared=http('POST','/api/boards',{'name':'Спільна дошка 20 учасників'},owner,201)['id']
 for user in users[1:]:http('POST',f'/api/boards/{shared}/members',{'email':user['user']['email'],'expectedBoardVersion':board(shared,owner)['version']},owner,201)
 col=http('POST',f'/api/boards/{shared}/columns',{'name':'Спільна робота','expectedBoardVersion':board(shared,owner)['version']},owner,201)
 shared_card=http('POST',f'/api/boards/{shared}/cards',{'columnId':col['id'],'title':'Конкурентна картка','expectedBoardVersion':board(shared,owner)['version']},owner,201)['card']
 check('20 independent authenticated users and shared memberships created')
 # Warm the production image and SQL query plans before timed concurrent waves.
 for i in range(20):http('GET',f'/api/boards/{shared}/cards?limit=100',token=users[i]['accessToken'],record=False)
 stage='read-20'
 for iteration in range(5):
  def read(i):
   token=users[i]['accessToken'];paths=[f'/api/boards/{shared}/cards?limit=100',f'/api/boards/{shared}/members','/api/boards','/api/me/cards','/api/me/team']
   return http('GET',paths[(i+iteration)%5],token=token)
  wave(read)
 check('100 simultaneous reads across five desktop data routes succeeded')
 stage='crud-20'
 def crud(i):
  token=users[i]['accessToken'];bid=private[i];v=board(bid,token)['version']
  card=http('POST',f'/api/boards/{bid}/cards',{'columnId':columns[i][0]['id'],'title':f'Задача {i}','description':'Перевірка всіх полів','dueDate':str(date.today()+timedelta(days=1)),'priority':i%4,'assigneeId':users[i]['user']['id'],'expectedBoardVersion':v},token,201)['card']
  card=http('PUT',f'/api/cards/{card["id"]}',{'title':f'Оновлена {i}','description':'Збережений опис','priority':2,'assigneeId':users[i]['user']['id'],'expectedVersion':card['version'],'expectedBoardVersion':board(bid,token)['version']},token)['card']
  moved=http('POST',f'/api/cards/{card["id"]}/move',{'targetColumnId':columns[i][1]['id'],'expectedVersion':card['version'],'expectedBoardVersion':board(bid,token)['version']},token)
  current=http('GET',f'/api/cards/{card["id"]}',token=token)['card'];assert current['columnId']==columns[i][1]['id']
  comment=http('POST',f'/api/cards/{card["id"]}/comments',{'text':'Коментар користувача '+str(i),'expectedVersion':current['version'],'expectedBoardVersion':board(bid,token)['version']},token,201)
  rows=http('GET',f'/api/boards/{bid}/cards?priority=High&search='+urllib.parse.quote('Оновлена'),token=token)
  assert rows['totalCount']==1 and rows['items'][0]['commentCount']==1
  current=rows['items'][0]
  http('PUT',f'/api/cards/{card["id"]}/archive',{'archived':True,'expectedVersion':current['version'],'expectedBoardVersion':board(bid,token)['version']},token)
  assert http('GET',f'/api/boards/{bid}/cards',token=token)['totalCount']==0
  archived=http('GET',f'/api/boards/{bid}/cards?archived=true',token=token)['items'];assert len(archived)==1
  http('PUT',f'/api/cards/{card["id"]}/archive',{'archived':False,'expectedVersion':archived[0]['version'],'expectedBoardVersion':board(bid,token)['version']},token)
  return card['id']
 cards=wave(crud);check('20 concurrent CRUD move comment filter archive and restore workflows preserved integrity')
 stage='same-card-conflict-20'
 expected=board(shared,owner)['version'];version=shared_card['version']
 def conflict(i):
  return http('POST',f'/api/cards/{shared_card["id"]}/move',{'targetColumnId':col['id'],'expectedBoardVersion':expected,'expectedVersion':version},users[i]['accessToken'],(200,409))
 wave(conflict)
 requests=[r for r in report['requests'] if r['scenario']==stage and r['method']=='POST']
 check('One same-card write won and 19 conflicts were reported',sum(r['status']==200 for r in requests)==1 and sum(r['status']==409 for r in requests)==19)
 check('Shared card remains present exactly once',http('GET',f'/api/boards/{shared}/cards',token=owner)['totalCount']==1)
 stage='persistence'
 before=[http('GET',f'/api/cards/{cid}',token=users[i]['accessToken']) for i,cid in enumerate(cards)]
 command(['docker','restart',container])
 for attempt in range(60):
  try:http('GET','/health/ready',record=False);break
  except Exception:
   if attempt==59:raise
   time.sleep(1)
 after=[http('GET',f'/api/cards/{cid}',token=users[i]['accessToken']) for i,cid in enumerate(cards)]
 check('Restarted production API preserves all 20 workflows',before==after)
 measured=[r for r in report['requests'] if r['scenario'] in ('read-20','crud-20','same-card-conflict-20')]
 for name in sorted({r['scenario'] for r in measured}):
  values=[r['ms'] for r in measured if r['scenario']==name];report['scenarios'][name]={'count':len(values),'p95_ms':pct(values,.95),'max_ms':max(values)}
 report['standard']={'count':len(measured),'p95_ms':pct([r['ms'] for r in measured],.95),'max_ms':max(r['ms'] for r in measured),'limit_ms':2000}
 report['setup_auth']={method:{'count':len(rows),'p95_ms':pct([r['ms'] for r in rows],.95),'max_ms':max(r['ms'] for r in rows)} for method in ['register','login'] if (rows:=[r for r in report['requests'] if r['path']=='/api/auth/'+method])}
 check('Every measured standard request is within 2 seconds',report['standard']['max_ms']<=2000)
 report['passed']=True
except Exception as e:
 report['passed']=False;report['failure']=str(e);raise
finally:
 if created:
  logs=command(['docker','logs',container],check=False);(out/'ph08-load-api.log').write_text(logs.stdout+logs.stderr)
  command(['docker','rm','-f',container],check=False)
  env=os.environ.copy();env['SQLCMDPASSWORD']=settings['MSSQL_SA_PASSWORD']
  cleanup=command(['docker','compose','exec','-T','-e','SQLCMDPASSWORD','sqlserver','/opt/mssql-tools18/bin/sqlcmd','-S','localhost','-U','sa','-C','-b','-Q',f"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END"],check=False,env=env)
  report['isolated_database_removed']=cleanup.returncode==0
 report['application_volume_preserved']=True
 (out/'ph08-load.json').write_text(json.dumps(report,ensure_ascii=False,indent=2))
 print('Report:',out/'ph08-load.json',flush=True)
