@echo off
setlocal
cd /d "%~dp0"

echo === GeniaFolder Portable x64 Publish ===
dotnet publish src\GeniaFolder\GeniaFolder.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts\portable-win-x64
if errorlevel 1 (
  echo [FAILED] Publish failed.
  pause
  exit /b 1
)

echo.
echo [PASS] Portable build ready:
echo %CD%\artifacts\portable-win-x64\GeniaFolder.exe
start "" "artifacts\portable-win-x64"
pause
