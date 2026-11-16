@echo off
rem Lets you type `islet run npm test` from cmd or PowerShell once this folder is on PATH.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0islet.ps1" %*
exit /b %ERRORLEVEL%
