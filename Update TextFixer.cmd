@echo off
powershell.exe -NoProfile -File "%~dp0update-and-start.ps1"
if errorlevel 1 pause
