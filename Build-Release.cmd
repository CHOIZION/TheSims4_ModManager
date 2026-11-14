@echo off
setlocal
set "ROOT=%~dp0"
set "DOTNET_DIR=%USERPROFILE%\.dotnet"
set "DOTNET=%DOTNET_DIR%\dotnet.exe"
if not exist "%DOTNET%" goto :use_path_dotnet
set "DOTNET_ROOT=%DOTNET_DIR%"
set "PATH=%DOTNET_DIR%;%PATH%"
goto :build
:use_path_dotnet
set "DOTNET=dotnet"
:build
set "OUT_DIR=%ROOT%release\win-x64"
echo [Sims4ModUpdater] Publishing Release standalone executable to "%OUT_DIR%"...
"%DOTNET%" publish "%ROOT%src\Sims4ModUpdater.App\Sims4ModUpdater.csproj" -c Release -r win-x64 --self-contained true --disable-build-servers -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "%OUT_DIR%"
if errorlevel 1 (
    echo.
    echo [ERROR] Release publish failed. Exit code: %ERRORLEVEL%
    if /I not "%~1"=="--nopause" pause
    exit /b 1
)
echo.
echo [SUCCESS] Release EXE ready at: "%OUT_DIR%\Sims4ModUpdater.exe"
if /I not "%~1"=="--nopause" pause
exit /b 0
