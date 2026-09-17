@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

fltmc >nul 2>&1
if errorlevel 1 (
  echo RavenMap servislerini kaldirmak yonetici yetkisi istiyor. UAC penceresi acilacak.
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Uninstall-RavenServer.ps1"
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%
