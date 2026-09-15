"""Exercise real Unity -> local LLM -> TTS -> audio playback -> mouth -> interrupt."""
import httpx,time,pathlib,json
root=pathlib.Path(__file__).resolve().parents[1];rt=root/'userdata/voice'
headers={'x-sola-token':(rt/'token.txt').read_text().strip()}
report={};c=httpx.Client(base_url='http://127.0.0.1:18768',headers=headers,timeout=10,trust_env=False)
def control(**body):return c.post('/control',json=body).raise_for_status()
def state():return c.get('/state').raise_for_status().json()
control(action='listen',enabled=False)
control(action='text',text='你好，请用一句中文介绍自己。')
start=time.time();spoken=False;maxmouth=0
while time.time()-start<120:
 s=state();maxmouth=max(maxmouth,s['mouth'])
 if s['reply'] and 'first_reply_seconds' not in report:report['first_reply_seconds']=round(time.time()-start,2);print('LLM reply arrived',flush=True)
 if s['speaking'] and maxmouth>.03:
  spoken=True;report['first_audio_seconds']=round(time.time()-start,2);report['reply']=s['reply'];old=s['generation']
  command={'id':'voice-mouth-'+str(time.time_ns()),'op':'voice'};(root/'work/command.json').write_text(json.dumps(command))
  until=time.time()+.6
  while time.time()<until:
   try:
    native=json.loads((root/'work/result.json').read_text(encoding='utf-8-sig'))
    if native['id']==command['id']:report['native']=native['value'];break
   except (OSError,ValueError):pass
   time.sleep(.04)
  control(action='interrupt');break
 if s['error']:report['error']=s['error']
 time.sleep(.15)
report.update(playback=spoken,mouth_max=round(maxmouth,3))
time.sleep(.5);s=state();report['interrupt_stopped']=not s['speaking'] and s['mouth']==0 and (not spoken or s['generation']>old)
report['task_monitor_count']=len(s['tasks']);report['engine']=s['engine']
control(action='listen',enabled=True);time.sleep(2);s=state();report.update(microphone=s['microphone'],aec=s['aec'],devices=len(s.get('devices',[])));control(action='listen',enabled=False)
report['success']=spoken and report['interrupt_stopped'] and report['aec']
(rt/'integration-verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False),flush=True)
raise SystemExit(0 if report['success'] else 1)
