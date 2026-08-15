@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if not "%errorlevel%"=="0" goto :nosdk

set "SDKFOUND="
for /f "delims=" %%i in ('dotnet --list-sdks 2^>nul') do set SDKFOUND=1
if not defined SDKFOUND goto :nosdk

dotnet run --project development\tests\Tests\Tests.csproj
echo.
pause
exit /b 0

:nosdk
echo [ERROR] The .NET 10 SDK is not installed.
echo Install it from:
echo https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.exe
pause
exit /b 1
