Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$depsDir = Join-Path $root 'deps'
$tmpZip = Join-Path $env:TEMP 'System.Data.SQLite.nupkg.zip'
$tmpExtract = Join-Path $env:TEMP 'System.Data.SQLite.nupkg'

$managedDll = Join-Path $depsDir 'System.Data.SQLite.dll'
$interopX64 = Join-Path $depsDir 'x64\SQLite.Interop.dll'
$interopX86 = Join-Path $depsDir 'x86\SQLite.Interop.dll'

if ((Test-Path $managedDll)) {
    Write-Host '[Download-Dependencies] All SQLite files already present. Skipping download.'
    exit 0
}

New-Item -ItemType Directory -Force -Path $depsDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $depsDir 'x64') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $depsDir 'x86') | Out-Null

# Use the official prebuilt binary bundle from system.data.sqlite.org
# This is a plain zip that contains DLLs directly without NuGet restore complexity.
$url = 'https://system.data.sqlite.org/downloads/1.0.118.0/sqlite-netFx46-binary-bundle-x64-2015-1.0.118.0.zip'
Write-Host "[Download-Dependencies] Downloading prebuilt bundle from: $url"

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $url -OutFile $tmpZip -UseBasicParsing

Write-Host '[Download-Dependencies] Extracting...'
if (Test-Path $tmpExtract) {
    Remove-Item $tmpExtract -Recurse -Force
}
Expand-Archive -Path $tmpZip -DestinationPath $tmpExtract -Force

$srcManaged = Get-ChildItem $tmpExtract -Recurse -Filter 'System.Data.SQLite.dll' | Select-Object -First 1 -ExpandProperty FullName
if (-not $srcManaged) { throw 'System.Data.SQLite.dll not found in bundle.' }

# This is the "bundle" build - native SQLite is embedded in the managed DLL.
# No separate SQLite.Interop.dll is needed.
Copy-Item $srcManaged -Destination $managedDll -Force
# Copy config so the runtime knows to use the bundle (no interop probe)
$srcConfig = $srcManaged + '.config'
if (Test-Path $srcConfig) {
    Copy-Item $srcConfig -Destination ($managedDll + '.config') -Force
}

Write-Host "[Download-Dependencies] Done."
Write-Host "  Managed DLL : $managedDll"
