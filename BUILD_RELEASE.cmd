@echo off
setlocal
cd /d "%~dp0"

echo === GeniaFolder Release Build ===
dotnet --version
if errorlevel 1 (
  echo [ERROR] .NET SDK not found. Install .NET 10 / Visual Studio 2026 .NET desktop workload.
  pause
  exit /b 1
)

dotnet build src\GeniaFolder\GeniaFolder.csproj -c Release
if errorlevel 1 (
  echo [FAILED] Build failed.
  pause
  exit /b 1
)

echo.
echo [PASS] Build completed.
echo Output: src\GeniaFolder\bin\Release\net10.0-windows\GeniaFolder.exe
start "" "src\GeniaFolder\bin\Release\net10.0-windows"
pause
