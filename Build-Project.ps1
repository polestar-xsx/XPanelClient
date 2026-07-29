param(
    [string]$Configuration = "Debug",
    [switch]$Clean,
    [switch]$Test,
    [switch]$Pack
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SolutionFile = Join-Path $ScriptDir "XPanelServer.sln"

Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host "XPanel Build Script" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Checking .NET SDK..." -ForegroundColor Yellow
try {
    $dotnetVersion = & dotnet --version
    Write-Host ".NET SDK version: $dotnetVersion" -ForegroundColor Green
} catch {
    Write-Host "ERROR: .NET SDK not found. Please install .NET 6.0 or higher." -ForegroundColor Red
    exit 1
}

if ($Clean) {
    Write-Host ""
    Write-Host "Cleaning build files..." -ForegroundColor Yellow
    & dotnet clean "$SolutionFile" -c $Configuration 2>&1 | Out-Null
    Write-Host "Clean completed" -ForegroundColor Green
}

Write-Host ""
Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow
& dotnet restore "$SolutionFile"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Restore failed" -ForegroundColor Red
    exit 1
}
Write-Host "Restore completed" -ForegroundColor Green

Write-Host ""
Write-Host "Building project (Configuration: $Configuration)..." -ForegroundColor Yellow
& dotnet build "$SolutionFile" -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed" -ForegroundColor Red
    exit 1
}
Write-Host "Build completed" -ForegroundColor Green

if ($Test) {
    Write-Host ""
    Write-Host "Running unit tests..." -ForegroundColor Yellow
    & dotnet test "$SolutionFile" -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed" -ForegroundColor Red
        exit 1
    }
    Write-Host "Tests completed" -ForegroundColor Green
}

if ($Pack) {
    Write-Host ""
    Write-Host "Publishing application..." -ForegroundColor Yellow
    $AppProject = Join-Path $ScriptDir "src\XPanel.Application\XPanel.Application.csproj"
    $PublishDir = Join-Path $ScriptDir "publish"
    & dotnet publish "$AppProject" -c $Configuration -o "$PublishDir"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Publishing failed" -ForegroundColor Red
        exit 1
    }
    Write-Host "Publishing completed at: $PublishDir" -ForegroundColor Green
}

Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host "Build completed successfully!" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
