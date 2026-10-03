@echo off
rem Castle Story Plus installer / updater for Windows: runs install.ps1 (see that file for the options).
rem Works on its own too: without install.ps1 next to it, it runs the latest one from GitHub.
if exist "%~dp0install.ps1" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; & ([scriptblock]::Create((New-Object Net.WebClient).DownloadString('https://raw.githubusercontent.com/Tricky12321/CastleStoryPlus/main/installer/install.ps1'))) %*"
)
if "%~1"=="" pause
