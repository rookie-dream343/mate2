param([string]$Version='stable-before-voice-20260915',[switch]$NoStart)
$ErrorActionPreference='Stop'
if($Version -notmatch '^[a-zA-Z0-9_.-]+$'){throw 'Invalid version name'}
$root=Split-Path -Parent $PSScriptRoot
$legacy=$Version -eq 'stable-before-voice-20260915'
$source=if($legacy){Join-Path $root 'work\before-voice-20260915'}else{Join-Path $root "work\releases\$Version"}
if(!(Test-Path -LiteralPath $source)){throw "Local snapshot not found: $source. See README for Git source recovery."}
$files=@()
if($legacy){
 foreach($name in @('SolaSupport.dll','Assembly-CSharp.dll','Unity.Postprocessing.Runtime.dll')){$files+=@{from=$name;to="app/MateEngineX_Data/Managed/$name"}}
 foreach($name in @('SolaSupport.cs','SolaDisplay.cs','build_support.ps1')){$files+=@{from=$name;to="work/$name"}}
 foreach($name in @('settings.json','display.json')){$files+=@{from=$name;to="userdata/$name"}}
 $files+=@{from='Start-Sola.ps1';to='Start-Sola.ps1'}
}else{
 $manifest=Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw|ConvertFrom-Json
 foreach($entry in $manifest.files){
  $resolved=[IO.Path]::GetFullPath((Join-Path $root $entry.path))
  if(!$resolved.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Snapshot contains an invalid destination'}
  if((Get-FileHash -LiteralPath (Join-Path $source $entry.path) -Algorithm SHA256).Hash -ne $entry.sha256){throw "Snapshot checksum mismatch: $($entry.path)"}
  $files+=@{from=$entry.path;to=$entry.path}
 }
}
foreach($f in $files){if(!(Test-Path -LiteralPath (Join-Path $source $f.from))){throw "Missing snapshot file: $($f.from)"}}
& (Join-Path $root 'scripts\Save-Version.ps1') -Version ('before-restore-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
& (Join-Path $root 'scripts\Stop-Voice.ps1')
Get-Process MateEngineX -ErrorAction SilentlyContinue | Where-Object {$_.Path -match 'MateEngine-Sola[\\/]app[\\/]MateEngineX.exe$'} | ForEach-Object {if(!$_.CloseMainWindow()){Stop-Process -Id $_.Id}else{if(!$_.WaitForExit(8000)){Stop-Process -Id $_.Id}}}
foreach($f in $files){$target=Join-Path $root $f.to;New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null;Copy-Item -LiteralPath (Join-Path $source $f.from) -Destination $target -Force}
Write-Output "Restored $Version. Previous files were saved automatically."
if(!$NoStart){& (Join-Path $root 'Start-Sola.ps1')}
