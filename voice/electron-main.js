const {app,BrowserWindow,session,globalShortcut}=require('electron');
const fs=require('fs'),path=require('path');
const root=path.resolve(__dirname,'..'),runtime=path.join(root,'userdata','voice');
let win;
app.disableHardwareAcceleration();
app.setPath('userData',path.join(runtime,'electron'));
app.commandLine.appendSwitch('autoplay-policy','no-user-gesture-required');
if(!app.requestSingleInstanceLock()){app.quit();}else{
app.whenReady().then(()=>{
 const token=fs.readFileSync(path.join(runtime,'token.txt'),'utf8').trim();
 const allowed=(permission,details)=>permission==='media'&&(!details.mediaTypes||details.mediaTypes.every(t=>t==='audio'));
 session.defaultSession.setPermissionRequestHandler((wc,permission,callback,details)=>callback(wc.getURL().startsWith('http://127.0.0.1:18768/')&&allowed(permission,details)));
 session.defaultSession.setPermissionCheckHandler((wc,permission,origin,details)=>/^http:\/\/127\.0\.0\.1:18768\/?$/.test(origin)&&allowed(permission,details));
 win=new BrowserWindow({show:false,width:460,height:220,webPreferences:{nodeIntegration:false,contextIsolation:true,sandbox:true,backgroundThrottling:false}});
 win.loadURL('http://127.0.0.1:18768/engine.html#'+token);
 win.webContents.on('console-message',(...args)=>{const [event,level,message]=args;const text=typeof message==='string'?message:level&&level.message||event.message||'';if(text&&!text.includes(token))fs.appendFileSync(path.join(runtime,'engine.log'),new Date().toISOString()+' '+text+'\n');});
 win.webContents.on('render-process-gone',(_,details)=>{fs.appendFileSync(path.join(runtime,'engine.log'),'Audio renderer stopped: '+JSON.stringify(details)+'\n');fetch('http://127.0.0.1:18768/control',{method:'POST',headers:{'Content-Type':'application/json','x-sola-token':token},body:JSON.stringify({action:'listen',enabled:false})}).catch(()=>{});setTimeout(()=>{if(win&&!win.isDestroyed())win.loadURL('http://127.0.0.1:18768/engine.html#'+token);},1500);});
 win.on('closed',()=>{win=null;app.quit();});
 globalShortcut.register('CommandOrControl+Alt+V',()=>fetch('http://127.0.0.1:18768/control',{method:'POST',headers:{'Content-Type':'application/json','x-sola-token':token},body:JSON.stringify({action:'toggle'})}).catch(()=>{}));
 app.on('before-quit',()=>globalShortcut.unregisterAll());
});}
