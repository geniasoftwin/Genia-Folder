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

dotnet build GeniaFolder.slnx -c Release
if errorlevel 1 (
  echo [FAILED] Build failed.
  pause
  exit /b 1
)

echo.
echo === GeniaFolder Security Smoke Tests ===
dotnet run --project tests\GeniaFolder.SecuritySmokeTests\GeniaFolder.SecuritySmokeTests.csproj -c Release --no-build
if errorlevel 1 (
  echo.
  echo [FAILED] Security smoke tests failed. Release output must not be trusted.
  pause
  exit /b 1
)

echo.
echo [PASS] Build and security smoke tests completed.
echo Output: src\GeniaFolder\bin\Release\net10.0-windows\GeniaFolder.exe
start "" "src\GeniaFolder\bin\Release\net10.0-windows"
pause
