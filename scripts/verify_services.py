import httpx,time,pathlib,io,wave,json
root=pathlib.Path(__file__).resolve().parents[1];runtime=root/'userdata'/'voice';report={}
with httpx.Client(timeout=120) as c:
 t=time.time();r=c.post('http://127.0.0.1:5000',json={'text':'你好，我是小歪，很高兴见到你。','text_language':'zh'});r.raise_for_status();report['tts_seconds']=round(time.time()-t,2)
 with wave.open(io.BytesIO(r.content)) as f:
  rate=f.getframerate();channels=f.getnchannels();width=f.getsampwidth();frames=f.readframes(f.getnframes());report.update(sample_rate=rate,channels=channels,audio_seconds=len(frames)/rate/channels/width)
 # Reuse the actual synthesized voice as a deterministic ASR round trip; no microphone recording is stored.
 import numpy as np
 from scipy.signal import resample_poly
 import math
 x=np.frombuffer(frames,dtype='<i2').astype(np.float32)/32768
 if channels>1:x=x.reshape(-1,channels).mean(axis=1)
 g=math.gcd(rate,16000);x=resample_poly(x,16000//g,rate//g);buf=io.BytesIO()
 with wave.open(buf,'wb') as f:f.setnchannels(1);f.setsampwidth(2);f.setframerate(16000);f.writeframes((np.clip(x,-1,1)*32767).astype('<i2').tobytes())
 t=time.time();a=c.post('http://127.0.0.1:1000/v1/upload_audio',files={'file':('roundtrip.wav',buf.getvalue(),'audio/wav')});a.raise_for_status();report['asr_seconds']=round(time.time()-t,2);report['asr']=a.json();report['success']=report['asr'].get('status')=='success' and '你好' in report['asr'].get('text','')
 (runtime/'verified-speech.wav').write_bytes(r.content)
(runtime/'services-verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(report,ensure_ascii=False))
