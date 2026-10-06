@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo === GeniaFolder Clean Test Environment ===
echo.

for /f %%I in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "STAMP=%%I"

set "TEST_ROOT=D:\GeniaFolder test"
set "TEST_BACKUP=D:\GeniaFolder test_backup_%STAMP%"
set "APP_STATE=%LOCALAPPDATA%\GeniaFolder"
set "APP_BACKUP=%LOCALAPPDATA%\GeniaFolder_backup_%STAMP%"
set "GENIAFOLDER_BUILD_EXE=%CD%\src\GeniaFolder\bin\Release\net10.0-windows\GeniaFolder.exe"

echo This script does NOT permanently delete the old test state.
echo Existing test folders/vaults and app state are moved to timestamped backups.
echo.

if exist "%GENIAFOLDER_BUILD_EXE%" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$target=$env:GENIAFOLDER_BUILD_EXE; " ^
    "$items=Get-CimInstance Win32_Process -Filter \"Name='GeniaFolder.exe'\" | Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq [IO.Path]::GetFullPath($target) }; " ^
    "foreach($p in $items){ Write-Host ('Stopping running GeniaFolder PID ' + $p.ProcessId + '...'); Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue; Wait-Process -Id $p.ProcessId -Timeout 5 -ErrorAction SilentlyContinue }"
)

if exist "%TEST_ROOT%" (
  echo Backing up test root:
  echo   %TEST_ROOT%
  echo   -^> %TEST_BACKUP%
  move "%TEST_ROOT%" "%TEST_BACKUP%" >nul
  if errorlevel 1 (
    echo [ERROR] Could not back up test root.
    pause
    exit /b 1
  )
)

if exist "%APP_STATE%" (
  echo Backing up GeniaFolder app state:
  echo   %APP_STATE%
  echo   -^> %APP_BACKUP%
  move "%APP_STATE%" "%APP_BACKUP%" >nul
  if errorlevel 1 (
    echo [ERROR] Could not back up app state.
    pause
    exit /b 1
  )
)

mkdir "%TEST_ROOT%\Папка 1" 2>nul
mkdir "%TEST_ROOT%\Папка 2" 2>nul
mkdir "%TEST_ROOT%\Папка 3" 2>nul

echo.
echo [PASS] Clean test environment created:
echo   %TEST_ROOT%\Папка 1
echo   %TEST_ROOT%\Папка 2
echo   %TEST_ROOT%\Папка 3
echo.
echo Old state is preserved in timestamped backup folders.
echo Start GeniaFolder and add the three folders as fresh entries.
pause
