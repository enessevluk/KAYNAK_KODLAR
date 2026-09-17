@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

fltmc >nul 2>&1
if errorlevel 1 (
  echo RavenMap kurulumu yonetici yetkisi istiyor. UAC penceresi acilacak.
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Install-RavenServer.ps1" -SourceRoot "%~dp0"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" echo Kurulum tamamlanamadi. Hata kodu: %RC%
pause
exit /b %RC%
