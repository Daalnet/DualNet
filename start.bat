@echo off
rem Start DualNet and turn on the Windows proxy so browsers and IDM use it.
rem The proxy is turned off again when you close this window.
rem
rem Which connection goes first, and its line speed, are chosen in the
rem dashboard and saved per computer in settings-<PC name>.txt.
rem To cap usage of a connection per month, add for example:  --limit Wi-Fi=10GB
cd /d "%~dp0"
if exist DualNet.old*.exe del DualNet.old*.exe 2>nul
start "" http://127.0.0.1:1080/
DualNet.exe --system-proxy %*
pause
