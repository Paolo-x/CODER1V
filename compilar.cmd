@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: para compilar se necesita el SDK de .NET 10 en este PC.
    echo Descarga oficial: https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)

dotnet publish "GTACodigoOCR.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o "%~dp0dist"
if errorlevel 1 (
    echo.
    echo ERROR: fallo la compilacion. Revisa los errores anteriores.
    pause
    exit /b 1
)

if not exist "%~dp0dist\GTACodigoOCR.exe" (
    echo ERROR: la publicacion no genero el ejecutable esperado.
    pause
    exit /b 1
)

echo.
echo EXITO: %~dp0dist\GTACodigoOCR.exe
pause
