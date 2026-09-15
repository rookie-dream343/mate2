param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_.-]+$')][string]$Version)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$dest=Join-Path $root "work\releases\$Version"
if(Test-Path -LiteralPath $dest){throw "Version already exists: $Version"}
$files=@('Start-Sola.ps1','README.md','使用说明.md','work/SolaSupport.cs','work/SolaDisplay.cs','work/build_support.ps1','app/MateEngineX_Data/Managed/SolaSupport.dll','app/MateEngineX_Data/Managed/Assembly-CSharp.dll','app/MateEngineX_Data/Managed/Unity.Postprocessing.Runtime.dll','userdata/settings.json','userdata/display.json','userdata/voice/settings.json')
foreach($folder in @('voice','scripts','tests')){$files+=Get-ChildItem -LiteralPath (Join-Path $root $folder) -File | Where-Object Extension -in @('.py','.js','.cs','.ps1','.html','.json','.txt') | ForEach-Object {"$folder/"+$_.Name}}
$entries=@()
foreach($relative in ($files | Select-Object -Unique)){
 $source=Join-Path $root $relative;if(!(Test-Path -LiteralPath $source)){continue}
 $target=Join-Path $dest $relative;New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null;Copy-Item -LiteralPath $source -Destination $target
 $entries+=@{path=$relative;sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash}
}
$manifest=@{version=$Version;created=(Get-Date -Format o);git=(& git -C $root rev-parse HEAD);files=$entries}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dest 'manifest.json') -Encoding UTF8
Write-Output "Saved $($entries.Count) files: $dest"
