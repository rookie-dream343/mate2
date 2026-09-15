$ErrorActionPreference='Stop'
$root='D:\MateEngine-Sola'
$exe=Join-Path $root 'app\MateEngineX.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Mate Engine was not found in D:\MateEngine-Sola'}
$running=Get-Process MateEngineX -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe -or $_.Path -eq 'D:\桌宠\MateEngine-Sola\app\MateEngineX.exe' }
& (Join-Path $root 'scripts\Start-Voice.ps1')
if($running){exit 0}
$cache=Join-Path $root 'userdata\Temp'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$env:TEMP=$cache
$env:TMP=$cache
$log=Join-Path $root 'userdata\MateEngine.log'
Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'app') -ArgumentList @('--datadir','D:\MateEngine-Sola\userdata','-screen-fullscreen','0','-screen-width','900','-screen-height','640','-logFile',('"'+$log+'"'))
