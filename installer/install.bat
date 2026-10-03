@echo off
rem Castle Story Plus installer / updater for Windows: runs install.ps1 (see that file for the options).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
if "%~1"=="" pause
