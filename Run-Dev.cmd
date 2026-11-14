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
echo [Sims4ModUpdater] Starting WPF GUI (Dev Mode)...
"%DOTNET%" run --project "%ROOT%src\Sims4ModUpdater.App\Sims4ModUpdater.csproj" --no-restore --disable-build-servers %*
if errorlevel 1 (
    echo.
    echo [ERROR] Launch failed. Exit code: %ERRORLEVEL%
    pause
)
endlocal
