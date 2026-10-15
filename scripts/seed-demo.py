#!/usr/bin/env python3
"""Create opt-in fictional local demo data through the same API as the desktop client."""
from pathlib import Path
from datetime import date,timedelta
import json,os,secrets,time,urllib.request,urllib.error,uuid
root=Path(__file__).resolve().parent.parent
base=os.environ.get('KANBAN_API_URL','http://localhost:5000').rstrip('/')
# Credentials and server IDs are local ignored artifacts, never repository configuration.
state_path=root/'TestResults/ph08-demo.json';state_path.parent.mkdir(exist_ok=True)
state=json.loads(state_path.read_text()) if state_path.exists() else {'password':'Demo.'+secrets.token_hex(10)+'!','users':[],'boards':[]}
def save():state_path.write_text(json.dumps(state,ensure_ascii=False,indent=2));state_path.chmod(0o600)
def call(method,path,data=None,token=None):
 headers={'Content-Type':'application/json'}
 if token:headers['Authorization']='Bearer '+token
 request=urllib.request.Request(base+path,data=None if data is None else json.dumps(data).encode(),headers=headers,method=method)
 with urllib.request.urlopen(request,timeout=30) as r:
  raw=r.read();return json.loads(raw) if raw else None
def upload(path,file,field,version,token):
 boundary='KanbanDemo'+uuid.uuid4().hex
 body=(f'--{boundary}\r\nContent-Disposition: form-data; name="{field}"\r\n\r\n{version}\r\n--{boundary}\r\nContent-Disposition: form-data; name="File"; filename="{file.name}"\r\nContent-Type: image/png\r\n\r\n').encode()+file.read_bytes()+f'\r\n--{boundary}--\r\n'.encode()
 req=urllib.request.Request(base+path,data=body,method='PUT',headers={'Authorization':'Bearer '+token,'Content-Type':'multipart/form-data; boundary='+boundary})
 with urllib.request.urlopen(req,timeout=30) as r:return json.load(r)
save()
for attempt in range(30):
 try:
  call('GET','/health/ready');break
 except Exception:
  if attempt==29:raise
  time.sleep(1)
names=[('Анастасія Коваленко','anastasiia'),('Олена Мельник','olena'),('Дмитро Бондар','dmytro'),('Ірина Петренко','iryna')]
auth=[]
for name,slug in names:
 email='demo.'+slug+'@kanban.local'
 try:a=call('POST','/api/auth/login',{'email':email,'password':state['password']})
 except urllib.error.HTTPError as e:
  if e.code!=401:raise
  a=call('POST','/api/auth/register',{'email':email,'password':state['password'],'name':name})
 auth.append(a)
 photo=root/'assets/demo'/('avatar-'+slug+'.png')
 if photo.exists() and not a['user']['hasAvatar']:
  a['user']=upload('/api/profile/avatar',photo,'ExpectedVersion',a['user']['version'],a['accessToken'])
state['users']=[{'email':a['user']['email'],'id':a['user']['id']} for a in auth];save()
token=auth[0]['accessToken']
def get(board):return call('GET','/api/boards/'+board,token=token)
def version(board):return get(board)['version']
themes=[('Курсовий проект','Розробка десктопного застосунку KanbanBoard на Avalonia','workspace'),('Особисті справи','Щоденні справи, цілі та особисті проекти','mountains'),('Навчання','Лекції, практичні завдання та підготовка до іспитів','library'),('Командні задачі','Спільна робота над завданнями та обговорення','team'),('План на тиждень','Поточні задачі та пріоритети на цей тиждень','planner'),('Ідеї','Ідеї для майбутніх проектів та натхнення','city')]
existing=call('GET','/api/boards',token=token)
for index,(name,description,photo) in enumerate(themes):
 found=next((b for b in existing if b['name']==name),None)
 if found is not None:
  if found['id'] not in state['boards']:state['boards'].append(found['id']);save()
  continue
 board=call('POST','/api/boards',{'name':name,'description':description},token);bid=board['id'];state['boards'].append(bid);save()
 upload('/api/boards/'+bid+'/cover',root/'assets/demo'/(photo+'.png'),'ExpectedBoardVersion',version(bid),token)
 for a in auth[1:]:call('POST',f'/api/boards/{bid}/members',{'email':a['user']['email'],'expectedBoardVersion':version(bid)},token)
 columns=[call('POST',f'/api/boards/{bid}/columns',{'name':n,'expectedBoardVersion':version(bid)},token) for n in ['Заплановано','У роботі','Перевірка','Готово']]
 tags=[call('POST',f'/api/boards/{bid}/tags',{'name':n,'color':c,'expectedBoardVersion':version(bid)},token) for n,c in [('UI','#4A8BCD'),('API','#4B82B6'),('Дизайн','#5A9A80'),('Документація','#9F855C'),('Безпека','#987CB6')]]
 titles=['Опис вимог','Модель бази даних','Налаштування середовища','Реєстрація користувачів','Drag-and-drop карток','Фільтри та пошук','JWT-авторизація','Завантаження фото','Перевірка доступу','Docker Compose','Базовий UI']
 descriptions=['Скласти детальний опис функціональних та нефункціональних вимог до системи.','Спроектувати структуру БД та зв’язки між сутностями.','Підготувати інструменти розробки та конфігурацію проекту.','Реалізувати форму реєстрації з валідацією даних.','Реалізувати перетягування карток між колонками з плавним зворотним зв’язком.','Знайти потрібні задачі за пріоритетом, тегом, відповідальним і датою.','Налаштувати автентифікацію та авторизацію користувачів з використанням JWT.','Додати обкладинку, прев’ю на дошці та перегляд повного зображення.','Перевірити права доступу до маршрутів і дій.','Налаштувати середовище розгортання за допомогою Docker Compose.','Створити основні екрани додатку за ескізами.']
 for i,title in enumerate(titles if index==0 else ['Підготувати план','Опрацювати матеріали','Обговорити результати']):
  col=columns[[0,0,0,1,1,1,2,2,3,3,3][i]]
  card=call('POST',f'/api/boards/{bid}/cards',{'columnId':col['id'],'title':title,'description':descriptions[i],'priority':[1,2,0,2,1,1,3,1,0,0,1][i],'dueDate':str(date.today()+timedelta(days=[-3,0,2,1,3,4,-1,2,-4,1,0][i])),'assigneeId':auth[i%4]['user']['id'],'tagIds':[tags[i%5]['id'],tags[(i+1)%5]['id']],'expectedBoardVersion':version(bid)},token)
  cid=card['card']['id']
  # Card media requires both board and card versions.
  if i in (0,4):
   file=root/'assets/demo'/('mountains.png' if i==0 else 'workspace.png');boundary='KanbanDemo'+uuid.uuid4().hex
   fields={'ExpectedBoardVersion':version(bid),'ExpectedVersion':card['card']['version']}
   body=b''.join((f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"\r\n\r\n{v}\r\n').encode() for k,v in fields.items())+(f'--{boundary}\r\nContent-Disposition: form-data; name="File"; filename="{file.name}"\r\nContent-Type: image/png\r\n\r\n').encode()+file.read_bytes()+f'\r\n--{boundary}--\r\n'.encode()
   req=urllib.request.Request(base+f'/api/cards/{cid}/image',data=body,method='PUT',headers={'Authorization':'Bearer '+token,'Content-Type':'multipart/form-data; boundary='+boundary})
   with urllib.request.urlopen(req,timeout=30) as r:r.read()
  for j in range(2 if index==0 else 1):
   current=call('GET',f'/api/cards/{cid}',token=token)
   call('POST',f'/api/cards/{cid}/comments',{'text':['Прев’ю потрібно показувати на самій картці.','Додам перевірку та збережу результати.'][j],'expectedBoardVersion':version(bid),'expectedVersion':current['card']['version']},auth[(i+j)%4]['accessToken'])
 print('Created demo board:',name,flush=True)
print('Demo ready; local credentials saved to ignored TestResults/ph08-demo.json',flush=True)
