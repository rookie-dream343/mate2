"""Local authenticated voice orchestrator; no cloud credentials or recordings in Git."""
import asyncio, collections, io, json, os, pathlib, re, secrets, shutil, time, wave
from contextlib import asynccontextmanager
import httpx, numpy as np, websockets
from fastapi import FastAPI, Request, HTTPException
from fastapi.responses import FileResponse, JSONResponse
import uvicorn

ROOT=pathlib.Path(__file__).resolve().parent.parent
RUNTIME=ROOT/'userdata'/'voice'; RUNTIME.mkdir(parents=True,exist_ok=True)
TOKENFILE=RUNTIME/'token.txt'
if not TOKENFILE.exists(): TOKENFILE.write_text(secrets.token_hex(32))
TOKEN=TOKENFILE.read_text().strip()
CONFIG={'asr':'http://127.0.0.1:1000/v1/upload_audio','vad':'ws://127.0.0.1:1000/v1/ws/vad','tts':'http://127.0.0.1:5000','workspace':str(ROOT),'auto_announce':True,'language':'zh'}
CFGFILE=RUNTIME/'settings.json'
if CFGFILE.exists(): CONFIG.update(json.loads(CFGFILE.read_text(encoding='utf-8-sig')))

def clean_speech(text):
    text=re.sub(r'```[\s\S]*?```','代码已显示在对话里。',text)
    text=re.sub(r'https?://\S+','链接',text)
    text=re.sub(r'[*#`]','',text)
    return text.strip()

def route(text):
    text=text.strip()
    if re.search(r'(停止|取消|终止)\s*(codex|任务)',text,re.I):return 'cancel',text
    m=re.match(r'^(?:小歪[，,\s]*)?(?:让|叫|请|用)\s*codex[，,:：\s]*(.+)',text,re.I)
    if m:return 'task',m[1].strip()
    if re.search(r'(codex|任务|工作).*(状态|进度|做什么|干嘛|怎么样|完成|好了吗)|(?:现在|目前).*(?:忙什么|做什么工作)|任务做完',text,re.I):return 'status',text
    return 'chat',text

class CodexMonitor:
    def __init__(self,root=None):
        self.root=pathlib.Path(root or pathlib.Path.home()/'.codex'/'sessions');self.files={};self.tasks={};self.scan=0
    def update(self):
        now=time.time()
        if now-self.scan>15:
            self.scan=now
            # A task can resume months after creation. Use Codex's read-only index as well as new folders.
            import sqlite3
            index=self.root.parent/'state_5.sqlite'
            if index.exists():
                try:
                    with sqlite3.connect(index.as_uri()+'?mode=ro',uri=True,timeout=.5) as db:
                        for file,cwd in db.execute('select rollout_path,cwd from threads where archived=0 order by updated_at desc limit 32'):
                            p=pathlib.Path(file)
                            if p.exists() and p not in self.files:
                                self.files[p]={'offset':max(0,p.stat().st_size-262144),'tail':b''}
                                self.tasks[str(p)]={'id':p.stem,'cwd':cwd,'phase':'unknown','detail':'','updated':p.stat().st_mtime,'stale':False}
                except (sqlite3.Error,OSError,ValueError):pass
            import datetime
            for day in range(2):
                d=datetime.datetime.now()-datetime.timedelta(days=day)
                folder=self.root/d.strftime('%Y')/d.strftime('%m')/d.strftime('%d')
                if folder.exists():
                    for p in folder.glob('*.jsonl'):
                        if p not in self.files:self.files[p]={'offset':max(0,p.stat().st_size-262144),'tail':b''}
        for p,track in list(self.files.items()):
            try:
                size=p.stat().st_size
                if size<track['offset']:track.update(offset=0,tail=b'')
                with p.open('rb') as f:f.seek(track['offset']);chunk=f.read(1048576);track['offset']=f.tell()
                if not chunk:continue
                lines=(track['tail']+chunk).split(b'\n');track['tail']=lines.pop()
                for line in lines:
                    try:self.consume(str(p),json.loads(line))
                    except (ValueError,TypeError,KeyError):pass
            except OSError:continue
        rows=sorted(self.tasks.values(),key=lambda t:t['updated'],reverse=True)
        for t in rows:
            t['stale']=now-t['updated']>900
        return rows[:8]
    def consume(self,key,event):
        payload=event.get('payload') or {};typ=payload.get('type','');top=event.get('type')
        from datetime import datetime
        try:stamp=datetime.fromisoformat(event.get('timestamp','').replace('Z','+00:00')).timestamp()
        except ValueError:stamp=time.time()
        t=self.tasks.setdefault(key,{'id':pathlib.Path(key).stem,'cwd':'','phase':'unknown','detail':'','updated':stamp,'stale':False})
        if top=='session_meta':t.update(id=payload.get('id',t['id']),cwd=payload.get('cwd',''))
        if top=='turn_context' and payload.get('cwd'):t['cwd']=payload['cwd']
        if top=='event_msg':
            phase={'task_started':'working','task_complete':'completed','turn_aborted':'cancelled','task_failed':'failed','turn_failed':'failed'}.get(typ)
            if phase:t.update(phase=phase,updated=stamp)
            if typ=='agent_reasoning':t.update(updated=stamp)
            if typ=='agent_message':
                value=payload.get('message') or payload.get('text') or ''
                if value:t.update(detail=re.sub(r'\s+',' ',value)[:150],updated=stamp)
        if top=='response_item' and payload.get('type')=='function_call':
            if 'request_user_input' in payload.get('name',''):t.update(phase='waiting',updated=stamp)
            else:t.update(phase='working',updated=stamp)

class Voice:
    def __init__(self):
        self.seq=0;self.generation=secrets.randbelow(1000000000)+1;self.instance=secrets.token_hex(8);self.events=collections.deque(maxlen=160);self.audio_queue=asyncio.Queue()
        self.state={'phase':'idle','listening':False,'speaking':False,'mouth':0.,'input':'','reply':'','error':'','tasks':[], 'codex':{'phase':'idle','detail':''},'engine':False,'microphone':'','aec':False,'level':0.}
        self.pending='';self.cumulative='';self.reply_id=self.generation;self.monitor=CodexMonitor();self.codex_process=None;self.codex_cancelled=False
        self.vad=None;self.vad_buffer=np.zeros(0,np.float32);self.pre=collections.deque(maxlen=5);self.record=[];self.in_voice=False;self.silent=0;self.voiced=0;self.last_audio=0
        self.http=None;self.audio_lock=asyncio.Lock();self.tasks=[];self.last_playback=0;self.last_unity=0;self.tts_request=None
        self.observed=None;self.notices=collections.deque(maxlen=8);self.last_notice=0;self.closed=False
    def event(self,typ,**kw):
        self.seq+=1;self.events.append(dict(seq=self.seq,type=typ,generation=self.generation,**kw))
    def stop(self):
        if self.tts_request and not self.tts_request.done():self.tts_request.cancel()
        while not self.audio_queue.empty():
            self.audio_queue.get_nowait();self.audio_queue.task_done()
        self.generation+=1;self.reply_id=self.generation;self.pending='';self.cumulative=''
        self.state.update(speaking=False,mouth=0.,phase='listening' if self.state['listening'] else 'idle')
        self.event('stop')
    async def text(self,text):
        text=text.strip()[:4000]
        if not text:return
        self.stop();self.state.update(input=text,reply='',error='',phase='thinking');kind,value=route(text)
        if kind=='status':
            rows=[t for t in self.monitor.update() if not t.get('stale')]
            active=[t for t in rows if t['phase'] in ('working','waiting')]
            if active:
                labels={'working':'正在处理','waiting':'等待你输入'}
                answer='当前有%d个活跃任务。'%len(active)+' '.join((pathlib.Path(t['cwd']).name or '一个任务')+'，'+labels[t['phase']]+'。'+t.get('detail','')[:90] for t in active[:3])
            elif rows:answer='目前没有检测到正在工作的任务。最近任务的状态是'+{'completed':'已完成','cancelled':'已停止','failed':'失败'}.get(rows[0]['phase'],'暂无明确状态')+'。'
            else:answer='目前没有读取到新鲜的 Codex 工作状态，不能确定它是否正在工作。'
            await self.reply(self.reply_id,answer,True)
        elif kind=='cancel':
            if self.codex_process and self.codex_process.returncode is None:
                self.codex_cancelled=True;self.codex_process.terminate();await self.reply(self.reply_id,'已请求停止由我启动的 Codex 任务。',True)
            else:await self.reply(self.reply_id,'我这里没有正在执行的 Codex 任务。桌面里的其他任务需要在 Codex 中停止。',True)
        elif kind=='task':
            if self.codex_process and self.codex_process.returncode is None:await self.reply(self.reply_id,'我启动的任务还在运行，请等它完成，或说停止 Codex 任务。',True)
            else:
                await self.reply(self.reply_id,'收到，我会在'+pathlib.Path(CONFIG['workspace']).name+'目录中开始这个任务。',True)
                self.tasks.append(asyncio.create_task(self.run_codex(value)))
        else:self.event('chat',id=self.reply_id,text=text)
    async def reply(self,rid,text,done):
        if rid!=self.reply_id:return
        text=str(text or '')[:6000];self.state['reply']=text
        delta=text[len(self.cumulative):] if text.startswith(self.cumulative) else text
        self.cumulative=text;self.pending+=delta
        while True:
            m=re.search(r'[。！？!?；;\n]',self.pending)
            if not m:break
            end=m.end();segment=self.pending[:end];self.pending=self.pending[end:]
            if segment.strip():await self.audio_queue.put((self.generation,clean_speech(segment)))
        if done and self.pending.strip():await self.audio_queue.put((self.generation,clean_speech(self.pending)));self.pending=''
        if done and not text.strip():self.state.update(phase='error',error='对话模型没有返回内容，请稍后重试。')
    async def tts_worker(self):
        while True:
            gen,text=await self.audio_queue.get()
            try:
                if gen!=self.generation or not text:continue
                self.state['phase']='synthesizing'
                self.tts_request=asyncio.create_task(self.http.post(CONFIG['tts'],json={'text':text,'text_language':CONFIG['language']},timeout=90))
                try:res=await self.tts_request
                except asyncio.CancelledError:
                    if gen!=self.generation:continue
                    raise
                res.raise_for_status()
                if gen!=self.generation:continue
                if not res.content.startswith((b'RIFF',b'OggS',b'ID3')):raise RuntimeError('语音服务未返回有效音频')
                name=secrets.token_hex(10)+'.wav';(RUNTIME/name).write_bytes(res.content)
                self.event('audio',file=name,text=text)
            except Exception as e:
                if gen==self.generation:self.state.update(phase='error',error='语音合成失败，请检查本地 TTS 服务。');self.event('error',message=self.state['error'])
            finally:self.audio_queue.task_done()
    async def transcribe(self,pcm,gen):
        try:
            data=io.BytesIO()
            with wave.open(data,'wb') as f:f.setnchannels(1);f.setsampwidth(2);f.setframerate(16000);f.writeframes(pcm)
            res=await self.http.post(CONFIG['asr'],files={'file':('utterance.wav',data.getvalue(),'audio/wav')},timeout=45);res.raise_for_status();result=res.json()
            if gen!=self.generation:return
            if result.get('status')!='success':raise RuntimeError('识别服务未成功')
            if result.get('text','').strip():await self.text(result['text'])
            else:self.state['phase']='listening' if self.state['listening'] else 'idle'
        except Exception:
            if gen==self.generation:self.state.update(phase='error',error='语音识别失败，请检查 ASR 服务。')
    async def audio(self,raw):
        if not self.state['listening']:return
        async with self.audio_lock:
            samples=np.frombuffer(raw,dtype='<i2').astype(np.float32)/32768
            if not len(samples):return
            rms=float(np.sqrt(np.mean(samples*samples)));self.state['level']=min(1.,rms*15)
            self.vad_buffer=np.concatenate([self.vad_buffer,samples]);prob=0.
            try:
                if self.vad is None:self.vad=await websockets.connect(CONFIG['vad'],open_timeout=3)
                while len(self.vad_buffer)>=512:
                    frame=self.vad_buffer[:512];self.vad_buffer=self.vad_buffer[512:]
                    await self.vad.send(frame.astype('<f4').tobytes());msg=json.loads(await asyncio.wait_for(self.vad.recv(),2))
                    prob=max(prob,float(msg.get('speech_prob',msg.get('probability',0))))
            except Exception:
                self.vad=None;self.vad_buffer=np.zeros(0,np.float32);self.state['error']='语音检测服务连接中，请等待 ASR 就绪。';return
            duration=len(samples)/16000
            speech=prob>.65 and rms>.003
            self.pre.append(raw)
            if speech:
                self.voiced+=duration;self.silent=0
                if not self.in_voice and self.voiced>=.18:
                    self.stop();self.state.update(phase='hearing',error='');self.in_voice=True;self.record=list(self.pre)
                elif self.in_voice:self.record.append(raw)
            else:
                if not self.in_voice:self.voiced=0
                else:self.record.append(raw);self.silent+=duration
            if self.in_voice and (self.silent>.65 or sum(map(len,self.record))>16000*2*25):
                pcm=b''.join(self.record);self.in_voice=False;self.record=[];self.voiced=0;self.silent=0;self.state['phase']='recognizing'
                self.tasks.append(asyncio.create_task(self.transcribe(pcm,self.generation)))
    async def run_codex(self,prompt):
        import subprocess
        try:
            self.codex_cancelled=False
            cmd=shutil.which('codex.exe') or shutil.which('codex')
            if not cmd:raise RuntimeError('Codex CLI 未找到')
            if cmd.endswith(('.cmd','.ps1')):
                script=pathlib.Path(cmd).parent/'node_modules/@openai/codex/bin/codex.js'
                if not script.exists():raise RuntimeError('Codex CLI 路径无法解析')
                launch=[shutil.which('node') or 'node',str(script)]
            else:launch=[cmd]
            self.state['codex']={'phase':'working','detail':prompt[:120]}
            p=await asyncio.create_subprocess_exec(*launch,'exec','--json','--sandbox','workspace-write','--skip-git-repo-check','-C',CONFIG['workspace'],'-',stdin=asyncio.subprocess.PIPE,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW)
            self.codex_process=p;p.stdin.write(prompt.encode());await p.stdin.drain();p.stdin.close()
            async def drain():
                while await p.stderr.read(4096):pass
            err=asyncio.create_task(drain());final='';turn_complete=False
            while True:
                try:line=await asyncio.wait_for(p.stdout.readline(),1)
                except asyncio.TimeoutError:
                    if p.returncode is not None:break
                    continue
                if not line:break
                try:
                    event=json.loads(line);item=event.get('item') or {}
                    if event.get('type')=='turn.completed':turn_complete=True;break
                    if event.get('type')=='turn.failed':break
                    if item.get('type')=='agent_message':final=item.get('text','');self.state['codex']['detail']=final[:180]
                    elif item.get('type'):self.state['codex']['detail']={'command_execution':'正在执行命令','file_change':'正在修改文件','web_search':'正在检索资料'}.get(item['type'],'正在处理任务')
                except ValueError:pass
            try:rc=await asyncio.wait_for(p.wait(),3)
            except asyncio.TimeoutError:rc=0 if turn_complete else (p.returncode or 1)
            err.cancel();self.state['codex']['phase']='cancelled' if self.codex_cancelled else ('completed' if rc==0 else 'failed')
            if self.codex_cancelled:return
            if not self.state['speaking'] and self.state['phase'] not in ('thinking','hearing','recognizing'):
                self.stop();await self.reply(self.reply_id,('Codex 任务完成了。'+final[:220]) if rc==0 else 'Codex 任务没有成功完成，请查看任务状态。',True)
        except Exception as e:self.state['codex']={'phase':'failed','detail':str(e)}
    async def monitor_loop(self):
        while True:
            self.state['tasks']=await asyncio.to_thread(self.monitor.update)
            current={t['id']:t['phase'] for t in self.state['tasks']}
            if self.observed is not None and CONFIG.get('auto_announce'):
                for t in self.state['tasks']:
                    if self.observed.get(t['id']) in ('working','waiting') and t['phase'] in ('completed','failed') and not t['stale']:
                        self.notices.append((time.time(),(pathlib.Path(t['cwd']).name or 'Codex')+'的任务'+('完成了。' if t['phase']=='completed' else '遇到了问题，请查看 Codex。')))
            self.observed=current
            if self.notices and self.last_unity>time.time()-5 and time.time()-self.last_notice>15 and self.state['phase'] in ('idle','listening') and not self.state['speaking']:
                stamp,notice=self.notices.popleft()
                if time.time()-stamp<180:
                    self.last_notice=time.time();self.stop();self.state['input']='任务通知';await self.reply(self.reply_id,notice,True)
            if self.last_unity and time.time()-self.last_unity>5 and not self.closed:
                self.closed=True;self.state['listening']=False;self.in_voice=False;self.record=[];self.stop()
            if time.time()-self.last_playback>5:self.state.update(engine=False,speaking=False,mouth=0.)
            self.tasks=[t for t in self.tasks if not t.done()]
            for p in RUNTIME.glob('*.wav'):
                try:
                    if time.time()-p.stat().st_mtime>600:p.unlink()
                except OSError:pass
            await asyncio.sleep(1)

v=Voice()
@asynccontextmanager
async def lifespan(app):
    v.http=httpx.AsyncClient();workers=[asyncio.create_task(v.tts_worker()),asyncio.create_task(v.monitor_loop())]
    yield
    for t in workers+v.tasks:t.cancel()
    await v.http.aclose()
app=FastAPI(lifespan=lifespan,docs_url=None,redoc_url=None)

@app.middleware('http')
async def authenticate(req,call):
    public=req.url.path in ['/health','/engine.html','/engine.js','/capture-worklet.js']
    if req.headers.get('origin') not in (None,'http://127.0.0.1:18768'):return JSONResponse({'error':'origin'},403)
    if not public and not secrets.compare_digest(req.headers.get('x-sola-token',''),TOKEN):return JSONResponse({'error':'unauthorized'},401)
    return await call(req)
@app.get('/health')
async def health():return {'service':'mate2-voice','version':1}
@app.get('/state')
async def state(after:int=0,client:str=''):
    if client=='unity':v.last_unity=time.time();v.closed=False
    return dict(v.state,generation=v.generation,instance=v.instance,seq=v.seq,workspace=CONFIG['workspace'],audio_device=CONFIG.get('audio_device',''),events=[e for e in v.events if e['seq']>after])
@app.post('/control')
async def control(req:Request):
    data=await req.json();action=data.get('action')
    if action in ('listen','toggle'):
        v.state['listening']=not v.state['listening'] if action=='toggle' else bool(data.get('enabled'));v.in_voice=False;v.record=[];v.voiced=0;v.silent=0;v.pre.clear();v.stop()
    elif action=='interrupt':v.stop()
    elif action=='text':await v.text(str(data.get('text','')))
    elif action=='workspace':
        path=pathlib.Path(data.get('path','')).resolve()
        if not path.is_dir():raise HTTPException(400,'工作目录不存在')
        CONFIG['workspace']=str(path);CFGFILE.write_text(json.dumps(CONFIG,ensure_ascii=False,indent=2),encoding='utf-8')
    elif action=='cancel_codex':await v.text('停止Codex任务')
    elif action=='audio_device':
        CONFIG['audio_device']=str(data.get('device',''));CFGFILE.write_text(json.dumps(CONFIG,ensure_ascii=False,indent=2),encoding='utf-8')
    elif action=='auto_announce':
        CONFIG['auto_announce']=bool(data.get('enabled'));CFGFILE.write_text(json.dumps(CONFIG,ensure_ascii=False,indent=2),encoding='utf-8')
    return {'ok':True}
@app.post('/reply')
async def reply(req:Request):
    d=await req.json();await v.reply(int(d['id']),d.get('text',''),bool(d.get('done')));return {'ok':True}
@app.post('/audio')
async def audio(req:Request):
    raw=await req.body()
    if len(raw)>64000 or len(raw)%2:raise HTTPException(400,'audio frame size')
    await v.audio(raw);return {'ok':True}
@app.post('/playback')
async def playback(req:Request):
    d=await req.json();v.last_playback=time.time();v.state.update(engine=True,microphone=str(d.get('microphone','')),aec=bool(d.get('aec')),devices=d.get('devices',[]))
    if int(d.get('generation',-1))==v.generation:
        speaking=bool(d.get('speaking'));v.state.update(speaking=speaking,mouth=min(1.,max(0.,float(d.get('mouth',0)))))
        if speaking:v.state['phase']='speaking'
        elif v.state['phase']=='speaking':v.state['phase']='listening' if v.state['listening'] else 'idle'
    if d.get('error'):v.state['error']=str(d['error'])[:200]
    return {'ok':True}
@app.get('/audio/{name}')
async def audio_file(name:str):
    if not re.fullmatch('[0-9a-f]{20}\.wav',name) or not (RUNTIME/name).exists():raise HTTPException(404)
    return FileResponse(RUNTIME/name,media_type='audio/wav')
@app.get('/{name}')
async def static_file(name:str):
    if name not in ['engine.html','engine.js','capture-worklet.js']:raise HTTPException(404)
    return FileResponse(ROOT/'voice'/name)
if __name__=='__main__':uvicorn.run(app,host='127.0.0.1',port=18768,access_log=False,log_level='warning')
