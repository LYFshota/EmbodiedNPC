@echo off
setlocal
chcp 65001 >nul
set "PYTHONPATH="
set "PYTHONHOME="
if not exist "%~dp0Backend\unified.py" (
  echo UNIFIED_RESOURCE_MISSING: Backend\unified.py
  pause
  exit /b 1
)
if not exist "%~dp0Backend\python\python.exe" (
  echo UNIFIED_RESOURCE_MISSING: Backend\python\python.exe
  pause
  exit /b 1
)
"%~dp0Backend\python\python.exe" -X utf8 -B "%~dp0Backend\unified.py" stop %*
set "LAUNCH_RESULT=%errorlevel%"
if not "%LAUNCH_RESULT%"=="0" pause
exit /b %LAUNCH_RESULT%
