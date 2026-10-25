@echo off
rem Removes the Rome II 8-player campaign mod.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall
echo.
pause
