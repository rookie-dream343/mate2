class Capture extends AudioWorkletProcessor{
 constructor(){super();this.samples=[];this.phase=0;this.sum=0;this.count=0;}
 process(inputs){const ch=inputs[0];if(!ch||!ch[0])return true;const x=ch[0];
  for(let i=0;i<x.length;i++){this.sum+=x[i];this.count++;this.phase+=16000;if(this.phase>=sampleRate){this.phase-=sampleRate;const value=Math.max(-1,Math.min(1,this.sum/this.count));this.samples.push(Math.round(value*32767));this.sum=0;this.count=0;}}
  if(this.samples.length>=1600){const pcm=new Int16Array(this.samples.splice(0,1600));this.port.postMessage(pcm.buffer,[pcm.buffer]);}return true;
 }
}
registerProcessor('sola-capture',Capture);
