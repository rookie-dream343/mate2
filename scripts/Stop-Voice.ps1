param([switch]$Models)
$root=Split-Path -Parent $PSScriptRoot
$runtime=Join-Path $root 'userdata\voice'
try{$h=@{'x-sola-token'=(Get-Content -LiteralPath (Join-Path $runtime 'token.txt') -Raw).Trim()};Invoke-RestMethod 'http://127.0.0.1:18768/control' -Method Post -Headers $h -ContentType 'application/json' -Body '{"action":"listen","enabled":false}' -TimeoutSec 3 | Out-Null}catch{}
$names=@('audio-engine','server');if($Models){$names+=@('asr','tts')}
foreach($name in $names){
 $file=Join-Path $runtime "$name.pid"
 if(!(Test-Path -LiteralPath $file)){continue}
 $number=0;if(![int]::TryParse((Get-Content -LiteralPath $file -Raw).Trim(),[ref]$number)){continue}
 $process=Get-CimInstance Win32_Process -Filter "ProcessId=$number" -ErrorAction SilentlyContinue
 $pattern=switch($name){'audio-engine' {'MateEngine-Sola[\\/]voice"'} 'server' {'MateEngine-Sola[\\/]voice[\\/]server.py'} 'asr' {'asr_api.py'} 'tts' {'api.py.*5000'}}
 if($process -and $process.CommandLine -match $pattern){Stop-Process -Id $number -ErrorAction SilentlyContinue}
}
