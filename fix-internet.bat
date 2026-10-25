@echo off
rem If DualNet was closed by force and websites stop loading, run this.
cd /d "%~dp0"
DualNet.exe --clear-proxy
pause
