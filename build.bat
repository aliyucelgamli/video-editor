@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if not "%errorlevel%"=="0" goto :nosdk

set "SDKFOUND="
for /f "delims=" %%i in ('dotnet --list-sdks 2^>nul') do set SDKFOUND=1
if not defined SDKFOUND goto :nosdk

rem A running instance would lock build\VideoEditor.exe.
tasklist /FI "IMAGENAME eq VideoEditor.exe" /NH 2>nul | find /I "VideoEditor.exe" >nul
if "%errorlevel%"=="0" goto :running

echo Publishing a self-contained release build into build\ ...
echo (This takes a while the first time.)
echo.
dotnet publish development\src\App\App.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -o build ^
    --nologo -v minimal
if not "%errorlevel%"=="0" (
    echo.
    echo [ERROR] Publish failed. Copy the message above and send it to Claude.
    echo.
    pause
    exit /b 1
)

echo.
echo Done: build\VideoEditor.exe
echo The user\ and cache\ folders stay where they are - the EXE reads them from
echo the project root, so keep build\ next to them or copy those folders along.
echo.
pause
exit /b 0

:running
echo [ERROR] Video Editor is running and locks build\VideoEditor.exe.
echo Close it and run this script again.
pause
exit /b 1

:nosdk
echo [ERROR] The .NET 10 SDK is not installed.
echo Install it from:
echo https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.exe
pause
exit /b 1
