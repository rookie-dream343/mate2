param([switch]$SkipModels)
$ErrorActionPreference='Stop'
$env:PYTHONIOENCODING='utf-8'
$env:PYTHONUTF8='1'
$root=Split-Path -Parent $PSScriptRoot
$runtime=Join-Path $root 'userdata\voice'; New-Item -ItemType Directory -Path $runtime -Force | Out-Null
$python='D:\conda\envs\my-neuro\python.exe'
$legacy='D:\deskmate\deskmate\Windomate-codex-bridge'
function Ready($url){try{Invoke-WebRequest -Uri $url -TimeoutSec 2 -UseBasicParsing | Out-Null;return $true}catch{return $_.Exception.Response -ne $null}}
function PortReady($port){$client=New-Object Net.Sockets.TcpClient;try{$task=$client.ConnectAsync('127.0.0.1',$port);return $task.Wait(600) -and $client.Connected}catch{return $false}finally{$client.Dispose()}}
function Launch($exe,$arguments,$cwd,$name){
 $p=Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $cwd -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runtime "$name.stdout.log") -RedirectStandardError (Join-Path $runtime "$name.stderr.log") -PassThru
 $p.Id | Set-Content -LiteralPath (Join-Path $runtime "$name.pid");return $p.Id
}
if(!(Ready 'http://127.0.0.1:18768/health')){
 Launch $python ('"'+(Join-Path $root 'voice\server.py')+'"') $root 'server' | Out-Null
 for($i=0;$i -lt 25;$i++){if(Ready 'http://127.0.0.1:18768/health'){break};Start-Sleep -Milliseconds 200}
}
if(!$SkipModels){
 $procs=Get-CimInstance Win32_Process
 if(!(PortReady 1000) -and !($procs | Where-Object {$_.CommandLine -like '*asr_api.py*' -and $_.Name -like '*python*'})){
  Launch $python 'asr_api.py' (Join-Path $legacy 'full-hub') 'asr' | Out-Null
 }
 if(!(PortReady 5000) -and !($procs | Where-Object {$_.CommandLine -like '*api.py*5000*' -and $_.Name -like '*python*'})){
  $tts=Join-Path $legacy 'full-hub\tts-hub\GPT-SoVITS-Bundle'
  Launch (Join-Path $tts 'runtime\python.exe') 'api.py -p 5000 -d cuda -s role_voice_api/neuro/merge.pth -dr role_voice_api/neuro/01.wav -dt "Hold on please, I am busy. Okay, I think I heard him say he wants me to stream Hollow Knight on Tuesday and Thursday." -dl en' $tts 'tts' | Out-Null
 }
}
$electron=Join-Path $legacy 'live-2d\node_modules\electron\dist\electron.exe'
if(Test-Path -LiteralPath (Join-Path $root 'runtime\electron-44.3.0\electron.exe')){$electron=Join-Path $root 'runtime\electron-44.3.0\electron.exe'}
$existing=Get-CimInstance Win32_Process | Where-Object {$_.Name -eq 'electron.exe' -and $_.CommandLine -match 'MateEngine-Sola[\\/]voice"' -and $_.CommandLine -notmatch '--type='}
if(!$existing){Launch $electron ('"'+(Join-Path $root 'voice')+'"') $root 'audio-engine' | Out-Null}
Write-Output 'Sola voice started. Microphone remains off until explicitly enabled.'
