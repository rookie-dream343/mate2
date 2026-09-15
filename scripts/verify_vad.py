"""Send a known synthetic utterance through the real streaming VAD + ASR path."""
import pathlib,httpx,time,wave,numpy as np,json
from scipy.signal import resample_poly
root=pathlib.Path(__file__).resolve().parents[1];rt=root/'userdata/voice'
if not (rt/'verified-speech.wav').exists():
 r=httpx.post('http://127.0.0.1:5000',json={'text':'你好，我是小歪，很高兴见到你。','text_language':'zh'},timeout=90);r.raise_for_status();(rt/'verified-speech.wav').write_bytes(r.content)
with wave.open(str(rt/'verified-speech.wav')) as f:rate=f.getframerate();pcm=np.frombuffer(f.readframes(f.getnframes()),dtype='<i2')
if rate!=16000:pcm=np.clip(resample_poly(pcm.astype(np.float32),16000,rate),-32768,32767).astype('<i2')
headers={'x-sola-token':(rt/'token.txt').read_text().strip()}
with httpx.Client(base_url='http://127.0.0.1:18768',headers=headers,timeout=10,trust_env=False) as c:
 # Inject while the real microphone capture is disabled in the renderer, to avoid mixing two streams.
 # Stop the audio engine before running this test; Start-Voice restarts it afterwards.
 c.post('/control',json={'action':'listen','enabled':True}).raise_for_status();start=c.get('/state').json()['generation']
 for frame in np.array_split(np.concatenate([np.zeros(8000,np.int16),pcm,np.zeros(16000,np.int16)]),max(1,(len(pcm)+24000)//1600)):
  c.post('/audio',content=frame.astype('<i2').tobytes()).raise_for_status();time.sleep(.035)
 deadline=time.time()+30;result={}
 while time.time()<deadline:
  s=c.get('/state').json()
  if s['input'] and '你好' in s['input'] and s['generation']>=start+2:
   result={'recognized':s['input'],'generation_changed':True,'success':True};break
  time.sleep(.2)
 c.post('/control',json={'action':'listen','enabled':False})
 if not result:result={'success':False,'phase':s['phase'],'error':s['error']}
 (rt/'vad-verification.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(result,ensure_ascii=False))
 raise SystemExit(0 if result['success'] else 1)
