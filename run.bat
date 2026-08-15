@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if not "%errorlevel%"=="0" goto :nosdk

set "SDKFOUND="
for /f "delims=" %%i in ('dotnet --list-sdks 2^>nul') do set SDKFOUND=1
if not defined SDKFOUND goto :nosdk

rem A running instance locks the output DLLs and makes the build fail with
rem MSB3026, so close it first.
call :isrunning
if not "%errorlevel%"=="0" goto :build

echo Video Editor is already running - its files are locked, so a rebuild would fail.
echo Save your work in that window if you need to.
choice /C YN /N /T 8 /D Y /M "Close it and rebuild? [Y/n] "
if errorlevel 2 goto :cancelled

echo Closing the running instance...
taskkill /IM VideoEditor.exe /F >nul 2>nul

set /a WAITS=0
:waitloop
call :isrunning
if not "%errorlevel%"=="0" goto :build
set /a WAITS+=1
if %WAITS% GEQ 10 goto :stillrunning
timeout /t 1 /nobreak >nul
goto :waitloop

:build
echo [1/2] Building (seconds when nothing changed)...
dotnet build development\src\App\App.csproj --nologo -v minimal
if not "%errorlevel%"=="0" (
    echo.
    echo [ERROR] Build failed. Copy the message above and send it to Claude.
    echo.
    pause
    exit /b 1
)

echo [2/2] Starting Video Editor...
echo.
dotnet run --project development\src\App\App.csproj --no-build
if not "%errorlevel%"=="0" (
    echo.
    echo [ERROR] The app could not start. Copy the message above and send it to Claude.
    echo.
    pause
    exit /b 1
)
exit /b 0

rem Returns 0 when VideoEditor.exe is running, 1 when it is not.
:isrunning
tasklist /FI "IMAGENAME eq VideoEditor.exe" /NH 2>nul | find /I "VideoEditor.exe" >nul
exit /b %errorlevel%

:cancelled
echo.
echo Cancelled - close Video Editor yourself, then run this script again.
echo.
pause
exit /b 1

:stillrunning
echo.
echo [ERROR] Video Editor is still running and keeps the build output locked.
echo Close it manually (or end the VideoEditor.exe task) and run this script again.
echo.
pause
exit /b 1

:nosdk
echo [ERROR] The .NET 10 SDK is not installed.
echo (A runtime may be present, but building needs the SDK.)
echo.
echo Install it from:
echo https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.exe
echo.
echo When it is installed, close this window and run run.bat again.
pause
exit /b 1
