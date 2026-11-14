@echo off
setlocal
set "ROOT=%~dp0"
set "DOTNET_DIR=%USERPROFILE%\.dotnet"
set "DOTNET=%DOTNET_DIR%\dotnet.exe"
if not exist "%DOTNET%" goto :use_path_dotnet
set "DOTNET_ROOT=%DOTNET_DIR%"
set "PATH=%DOTNET_DIR%;%PATH%"
goto :launch
:use_path_dotnet
set "DOTNET=dotnet"
:launch
echo [Sims4ModUpdater] Running Core Automated Test Suite...
"%DOTNET%" run --project "%ROOT%tests\Sims4ModUpdater.Tests\Sims4ModUpdater.Tests.csproj" --no-restore --disable-build-servers
set "RC=%ERRORLEVEL%"
if /I not "%~1"=="--nopause" pause
exit /b %RC%
