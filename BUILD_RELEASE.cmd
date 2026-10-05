@echo off
setlocal
cd /d "%~dp0"

echo === GeniaFolder Release Build ===

set "GENIAFOLDER_BUILD_EXE=%CD%\src\GeniaFolder\bin\Release\net10.0-windows\GeniaFolder.exe"

rem GeniaFolder now stays alive in the tray after X. Stop only the Release
rem executable from this checkout so MSBuild can safely replace it.
if exist "%GENIAFOLDER_BUILD_EXE%" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$target=$env:GENIAFOLDER_BUILD_EXE; " ^
    "$items=Get-CimInstance Win32_Process -Filter \"Name='GeniaFolder.exe'\" | Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq [IO.Path]::GetFullPath($target) }; " ^
    "foreach($p in $items){ Write-Host ('Stopping running GeniaFolder PID ' + $p.ProcessId + ' before build...'); Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue; Wait-Process -Id $p.ProcessId -Timeout 5 -ErrorAction SilentlyContinue }"
)

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
