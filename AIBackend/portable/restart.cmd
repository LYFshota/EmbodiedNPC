@echo off
setlocal
set "PYTHONPATH="
set "PYTHONHOME="
if not defined EMBODIEDNPC_DATA_ROOT for %%I in ("%~dp0..\UserData") do set "EMBODIEDNPC_DATA_ROOT=%%~fI"
"%~dp0python\python.exe" -B "%~dp0launcher.py" restart %*
exit /b %errorlevel%
