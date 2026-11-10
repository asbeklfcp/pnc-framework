@echo off
title PNC Framework - Recover v0.1.5
setlocal
echo PNC Framework v0.1.5 one-time signed rollback recovery
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Recover-v015.ps1" %*
echo.
echo Press any key to close this window.
pause >nul