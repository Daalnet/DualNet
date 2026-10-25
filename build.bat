@echo off
rem Builds DualNet.exe with the C# compiler that ships with Windows.
rem The dashboard page and the Vazirmatn font are packed inside the exe.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize /target:exe /out:DualNet.exe /resource:src\dashboard.html,dashboard.html /resource:src\fonts\Vazirmatn.woff2,Vazirmatn.woff2 src\DualNet.cs
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
echo Built DualNet.exe
