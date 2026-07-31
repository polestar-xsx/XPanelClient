Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'ToastWatcherNetFx\Program.cs'
$outDir = Join-Path $root 'bin'
$outExe = Join-Path $outDir 'ToastWatcherNetFx.exe'

if (-not (Test-Path $src)) {
    throw "Source file not found: $src"
}

$candidates = @(
    'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\Roslyn\csc.exe',
    'C:\Program Files (x86)\MSBuild\14.0\Bin\Roslyn\csc.exe',
    'C:\Program Files (x86)\MSBuild\12.0\Bin\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
)

$csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    throw 'No C# compiler found. Install Visual Studio Build Tools or .NET SDK.'
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$uiaBase = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6'
$uiaClient = Join-Path $uiaBase 'UIAutomationClient.dll'
$uiaTypes  = Join-Path $uiaBase 'UIAutomationTypes.dll'

& $csc /nologo /target:exe /out:$outExe `
    /r:System.Core.dll `
    /r:System.Xml.Linq.dll `
    /r:$uiaClient `
    /r:$uiaTypes `
    $src

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Build succeeded: $outExe"
