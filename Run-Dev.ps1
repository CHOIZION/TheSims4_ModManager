$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnetDir = Join-Path $env:USERPROFILE '.dotnet'
$dotnet = Join-Path $dotnetDir 'dotnet.exe'

if (Test-Path $dotnet) {
    $env:DOTNET_ROOT = $dotnetDir
    $env:PATH = "$dotnetDir;$env:PATH"
} else {
    $dotnet = 'dotnet'
}

Write-Host '[Sims4ModUpdater] 개발 모드로 WPF 데스크톱 UI를 실행합니다 (최종 Release/단일 exe 빌드 전)...' -ForegroundColor Cyan
& $dotnet run --project (Join-Path $root 'src\Sims4ModUpdater.App\Sims4ModUpdater.csproj') --no-restore --disable-build-servers
