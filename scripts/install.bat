@echo off
REM Eling installer wrapper for cmd.exe
REM Usage:
REM   install.bat                 ^| install latest binary (stable first, pre-release fallback)
REM   install.bat -DashboardOnly  ^| repair dashboard UI only
REM This wrapper only calls install.ps1 with PowerShell; all logic stays in install.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
exit /b %ERRORLEVEL%
