@echo off
setlocal
call "%~dp0Start.cmd" %*
exit /b %errorlevel%
