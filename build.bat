@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if not "%errorlevel%"=="0" goto :nosdk

set "SDKFOUND="
for /f "delims=" %%i in ('dotnet --list-sdks 2^>nul') do set SDKFOUND=1
if not defined SDKFOUND goto :nosdk

echo Release build hazirlaniyor...
dotnet publish development\src\App\App.csproj -c Release -r win-x64 --self-contained true -o build\
if not "%errorlevel%"=="0" (
    echo.
    echo [HATA] Build basarisiz. Yukaridaki hata mesajini kopyalayip Claude'a gonderin.
    echo.
    pause
    exit /b 1
)
echo.
echo Build tamamlandi: build\VideoEditor.exe
pause
exit /b 0

:nosdk
echo [HATA] .NET 10 SDK kurulu degil.
echo (Runtime kurulu olabilir ama derleme icin SDK gerekir.)
echo.
echo Kurulum: asagidaki adresi tarayicida acin, inen dosyayi kurun:
echo https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.exe
echo.
echo Kurulum bitince bu pencereyi kapatip build.bat'i tekrar calistirin.
pause
exit /b 1
