@echo off
rem Builds and starts Developer Island from source. Usage: run [-Demo] [-Release]
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\run.ps1" %*
