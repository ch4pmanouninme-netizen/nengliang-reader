@echo off
rem The one double-clickable file: it puts three shortcuts on the desktop, after which the
rem shortcuts are the entry point. Keep this file ASCII-only - cmd.exe reads .bat in the
rem console code page, so non-ASCII inside would turn into noise.
rem
rem A .bat wrapper around start/stop/update would be unsafe: update.ps1 rewrites the repository
rem in place (git reset --hard), and cmd re-reads a running .bat line by line from disk, so
rem replacing it mid-run makes it execute half-lines. Nothing this file runs rewrites anything,
rem which is why this one is fine.
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-shortcuts.ps1" %*
set RC=%ERRORLEVEL%
echo.
pause
exit /b %RC%
