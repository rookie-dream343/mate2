$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$version='44.3.0'
$dest=Join-Path $root "runtime\electron-$version"
if(Test-Path -LiteralPath (Join-Path $dest 'electron.exe')){Write-Output "Audio runtime already installed: $version";exit 0}
$downloads=Join-Path $root 'downloads';New-Item -ItemType Directory -Path $downloads -Force | Out-Null
$name="electron-v$version-win32-x64.zip"
$base="https://github.com/electron/electron/releases/download/v$version"
$zip=Join-Path $downloads $name
$sums=Join-Path $downloads "electron-$version-SHASUMS256.txt"
& curl.exe -fL --retry 3 --silent --show-error "$base/$name" -o $zip
if($LASTEXITCODE -ne 0){throw 'Electron download failed'}
& curl.exe -fL --retry 3 --silent --show-error "$base/SHASUMS256.txt" -o $sums
if($LASTEXITCODE -ne 0){throw 'Electron checksums download failed'}
$line=Get-Content -LiteralPath $sums | Where-Object { $_ -match ('\s+\*?'+[regex]::Escape($name)+'$') }
if(!$line){throw 'Electron checksum entry missing'}
$expected=($line -split '\s+')[0]
if((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expected){throw 'Electron checksum mismatch'}
Expand-Archive -LiteralPath $zip -DestinationPath $dest -Force
Write-Output "Verified and installed Electron $version in $dest"
