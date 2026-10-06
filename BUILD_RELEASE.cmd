@echo off
setlocal
cd /d "%~dp0"

echo === GeniaFolder Release Build ===

set "GENIAFOLDER_BUILD_ROOT=%CD%"

rem GeniaFolder may be launched from either the main Release output or from a
rem referenced test output. Stop every GeniaFolder.exe whose executable lives
rem inside THIS checkout, but never touch installations in other directories.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$root=[IO.Path]::GetFullPath($env:GENIAFOLDER_BUILD_ROOT).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar; " ^
  "$items=Get-CimInstance Win32_Process -Filter \"Name='GeniaFolder.exe'\" | Where-Object { " ^
    "if(-not $_.ExecutablePath){ return $false }; " ^
    "try { $exe=[IO.Path]::GetFullPath($_.ExecutablePath) } catch { return $false }; " ^
    "$exe.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) " ^
  "}; " ^
  "foreach($p in $items){ Write-Host ('Stopping running GeniaFolder PID ' + $p.ProcessId + ' from this checkout before build...'); Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue; Wait-Process -Id $p.ProcessId -Timeout 5 -ErrorAction SilentlyContinue }"
if errorlevel 1 (
  echo [ERROR] Could not inspect/stop running GeniaFolder processes from this checkout.
  pause
  exit /b 1
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
