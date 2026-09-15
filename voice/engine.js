const token=location.hash.slice(1);history.replaceState(null,'','/engine.html');
const headers={'x-sola-token':token};
let devices=[],selectedDevice='',capturedDevice='',serverInstance='';
async function api(path,data){const r=await fetch(path,{method:data===undefined?'GET':'POST',headers:{...headers,'Content-Type':'application/json'},body:data===undefined?undefined:JSON.stringify(data)});if(!r.ok)throw Error('语音连接错误 '+r.status);return r.json();}
let context,analyser,stream,micNode,source,playing=false,queue=[],generation=0,cursor=0,listening=false,micName='',aec=false,error='',initializing=false,sendChain=Promise.resolve(),outstanding=0,playId=0,lastConnection=Date.now();
function stopAudio(){playId++;queue=[];if(source){source.onended=null;try{source.stop();}catch{}source=null;}playing=false;}
// Decode PCM WAV directly. This avoids the legacy desktop runtime's crashing FFmpeg decoder.
function pcmWav(bytes){
 const v=new DataView(bytes);let format=0,channels=0,rate=0,bits=0,offset=0,size=0;
 const tag=p=>String.fromCharCode(...new Uint8Array(bytes,p,4));
 if(bytes.byteLength<44||tag(0)!=='RIFF'||tag(8)!=='WAVE')throw Error('PCM WAV required');
 for(let p=12;p+8<=bytes.byteLength;){const n=v.getUint32(p+4,true);if(p+8+n>bytes.byteLength)throw Error('Truncated WAV');if(tag(p)==='fmt '){format=v.getUint16(p+8,true);channels=v.getUint16(p+10,true);rate=v.getUint32(p+12,true);bits=v.getUint16(p+22,true);}if(tag(p)==='data'){offset=p+8;size=n;}p+=8+n+(n%2);}
 if(format!==1||bits!==16||!channels||!rate||!offset)throw Error('16-bit PCM WAV required');
 const frames=Math.floor(size/2/channels),buffer=context.createBuffer(channels,frames,rate);
 for(let c=0;c<channels;c++){const out=buffer.getChannelData(c);for(let i=0;i<frames;i++)out[i]=v.getInt16(offset+(i*channels+c)*2,true)/32768;}
 return buffer;
}
async function capture(enabled){
 if(!enabled){listening=false;if(stream)stream.getTracks().forEach(t=>t.stop());stream=null;if(micNode){micNode.disconnect();micNode=null;}return;}
 if(stream||initializing)return;initializing=true;
 try{
  context=context||new AudioContext({sampleRate:48000});await context.resume();
  stream=await navigator.mediaDevices.getUserMedia({audio:{echoCancellation:true,noiseSuppression:true,autoGainControl:true,channelCount:1,...(selectedDevice?{deviceId:{exact:selectedDevice}}:{})},video:false});capturedDevice=selectedDevice;
  devices=(await navigator.mediaDevices.enumerateDevices()).filter(d=>d.kind==='audioinput'&&d.deviceId!=='communications').map(d=>({id:d.deviceId,label:d.label||'麦克风'}));
  const track=stream.getAudioTracks()[0];micName=track.label;const settings=track.getSettings();aec=settings.echoCancellation===true;
  await context.audioWorklet.addModule('/capture-worklet.js');micNode=new AudioWorkletNode(context,'sola-capture');
  const input=context.createMediaStreamSource(stream);input.connect(micNode);const mute=context.createGain();mute.gain.value=0;micNode.connect(mute).connect(context.destination);
  listening=true;error='';console.log('Microphone ready; echoCancellation='+aec+' sampleRate='+settings.sampleRate);
  micNode.port.onmessage=e=>{if(!listening||outstanding>=6)return;outstanding++;sendChain=sendChain.then(()=>fetch('/audio',{method:'POST',headers,body:e.data})).catch(()=>{}).finally(()=>outstanding--);};
 }catch(e){error='麦克风不可用，请检查 Windows 麦克风权限和输入设备。';await api('/control',{action:'listen',enabled:false});console.warn(error);}
 finally{initializing=false;}
}
async function playNext(){
 if(playing||!queue.length)return;
 const item=queue.shift();if(item.generation!==generation)return playNext();playing=true;const id=++playId;
 try{
  context=context||new AudioContext({sampleRate:48000});await context.resume();
  const r=await fetch('/audio/'+item.file,{headers});if(!r.ok)throw Error('audio unavailable');const buffer=pcmWav(await r.arrayBuffer());
  if(item.generation!==generation||id!==playId)return;
  analyser=analyser||context.createAnalyser();analyser.fftSize=512;analyser.smoothingTimeConstant=.5;
  source=context.createBufferSource();source.buffer=buffer;source.connect(analyser);analyser.disconnect();analyser.connect(context.destination);
  source.onended=()=>{source=null;playing=false;playNext();};source.start();error='';
 }catch(e){if(id!==playId)return;playing=false;error='朗读音频无法播放。';console.warn(error);playNext();}
}
async function tick(){
 try{
  const state=await api('/state?after='+cursor);lastConnection=Date.now();
  if(serverInstance!==state.instance){serverInstance=state.instance;cursor=0;stopAudio();}
  if(state.generation!==generation){generation=state.generation;stopAudio();}
  for(const event of state.events){cursor=Math.max(cursor,event.seq);if(event.type==='audio'&&event.generation===generation)queue.push(event);}
  selectedDevice=state.audio_device||'';if(listening&&capturedDevice!==selectedDevice)await capture(false);
  if(state.listening!==listening&&!initializing)await capture(state.listening);
  playNext();let mouth=0;
  if(playing&&analyser){const values=new Float32Array(analyser.fftSize);analyser.getFloatTimeDomainData(values);mouth=Math.min(1,Math.sqrt(values.reduce((s,x)=>s+x*x,0)/values.length)*9);}
  await api('/playback',{generation,speaking:playing,mouth,microphone:micName,aec,error,devices});
 }catch(e){if(Date.now()-lastConnection>5000){stopAudio();await capture(false);}}
 setTimeout(tick,100);
}
tick();
