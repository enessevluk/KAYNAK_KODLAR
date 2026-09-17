@echo off
chcp 65001 >nul
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
title RAVEN SERVER YONETIM

set "RAVEN_SERVER_ROOT=%~dp0"
set "RAVEN_CONFIG_FILE=%~dp0Config\raven_server.json"
set "CADDY_CONFIG=%~dp0Caddyfile"
set "CADDY_OUT=%~dp0Logs\caddy-output.log"
set "CADDY_ERR=%~dp0Logs\caddy-error.log"

if not exist "%~dp0Logs" mkdir "%~dp0Logs" >nul 2>&1

call :FIND_CADDY

:MENU
call :READ_PUBLIC_API
call :READ_STATUS
cls
echo.
echo  ========================================================================
echo                         RAVEN SERVER YONETIM
echo  ========================================================================
echo.
echo    Raven.Server   : !SERVER_STATUS!
echo    Caddy          : !CADDY_STATUS!
echo    Local API 5088 : !LOCAL_STATUS!
echo    Public API     : !PUBLIC_API!
echo.
echo  ------------------------------------------------------------------------
echo.
echo    [1] Raven Stack Baslat       Caddy + Raven.Server canli konsol
echo    [2] Admin Guvenlik Ayarlari
echo    [3] Local Health Kontrolu
echo    [4] Admin Panelini Ac
echo    [5] TOTP Secret Goster
echo    [6] Server Update Uygula
echo    [7] Shopier Config + API Testi
echo    [8] Server Config Dosyasini Ac
echo    [9] Caddy Kontrol / Reload
echo    [P] Public HTTPS Health Testi
echo.
echo    [0] Cikis
echo.
echo  ========================================================================
echo.
set /p "SECIM=  Secim: "

if /I "%SECIM%"=="1" goto START
if /I "%SECIM%"=="2" goto SECRETS
if /I "%SECIM%"=="3" goto HEALTH
if /I "%SECIM%"=="4" goto ADMIN
if /I "%SECIM%"=="5" goto TOTP
if /I "%SECIM%"=="6" goto UPDATE
if /I "%SECIM%"=="7" goto SHOPIERTEST
if /I "%SECIM%"=="8" goto CONFIG
if /I "%SECIM%"=="9" goto CADDY
if /I "%SECIM%"=="P" goto PUBLICHEALTH
if /I "%SECIM%"=="0" exit /b 0
goto MENU

:FIND_CADDY
set "CADDY_EXE="
if exist "%~dp0caddy.exe" set "CADDY_EXE=%~dp0caddy.exe"
if defined CADDY_EXE exit /b 0
for /f "delims=" %%I in ('where caddy.exe 2^>nul') do if not defined CADDY_EXE set "CADDY_EXE=%%I"
exit /b 0

:READ_PUBLIC_API
set "PUBLIC_API=Bilinmiyor"
if exist "%RAVEN_CONFIG_FILE%" (
  for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$c=Get-Content -Raw -LiteralPath '%RAVEN_CONFIG_FILE%'|ConvertFrom-Json; [string]$c.Raven.PublicBaseUrl"`) do set "PUBLIC_API=%%I"
)
exit /b 0

:READ_STATUS
set "SERVER_STATUS=KAPALI"
set "CADDY_STATUS=KAPALI"
set "LOCAL_STATUS=KAPALI"

tasklist /FI "IMAGENAME eq Raven.Server.exe" 2>nul | find /I "Raven.Server.exe" >nul
if not errorlevel 1 set "SERVER_STATUS=CALISIYOR"

tasklist /FI "IMAGENAME eq caddy.exe" 2>nul | find /I "caddy.exe" >nul
if not errorlevel 1 set "CADDY_STATUS=CALISIYOR"

powershell -NoProfile -ExecutionPolicy Bypass -Command "try{$c=New-Object Net.Sockets.TcpClient;$a=$c.BeginConnect('127.0.0.1',5088,$null,$null);if($a.AsyncWaitHandle.WaitOne(250)){try{$c.EndConnect($a);exit 0}catch{exit 1}}else{exit 1}}catch{exit 1}finally{if($c){$c.Close()}}" >nul 2>&1
if not errorlevel 1 set "LOCAL_STATUS=ACIK"

exit /b 0

:PREPARE_CADDY
if not exist "%CADDY_CONFIG%" (
  echo.
  echo  HATA: Caddyfile bulunamadi:
  echo  %CADDY_CONFIG%
  exit /b 20
)

call :FIND_CADDY
if not defined CADDY_EXE (
  echo.
  echo  HATA: caddy.exe bulunamadi.
  echo  caddy.exe dosyasini SERVER klasorune koy veya PATH'e ekle.
  exit /b 21
)

echo.
echo  Caddy config kontrol ediliyor...
"%CADDY_EXE%" validate --config "%CADDY_CONFIG%" --adapter caddyfile >"%~dp0Logs\caddy-validate.log" 2>&1
if errorlevel 1 (
  echo  HATA: Caddyfile gecersiz.
  echo  Log: "%~dp0Logs\caddy-validate.log"
  exit /b 22
)

tasklist /FI "IMAGENAME eq caddy.exe" 2>nul | find /I "caddy.exe" >nul
if not errorlevel 1 (
  echo  Caddy calisiyor. Config reload ediliyor...
  "%CADDY_EXE%" reload --config "%CADDY_CONFIG%" --adapter caddyfile >"%~dp0Logs\caddy-reload.log" 2>&1
  if errorlevel 1 (
    echo  HATA: Caddy reload basarisiz.
    echo  Log: "%~dp0Logs\caddy-reload.log"
    exit /b 23
  )
  echo  Caddy: HAZIR
  exit /b 0
)

echo  Caddy arka planda baslatiliyor...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$a=@('run','--config','%CADDY_CONFIG%','--adapter','caddyfile'); Start-Process -FilePath '%CADDY_EXE%' -ArgumentList $a -WorkingDirectory '%~dp0' -WindowStyle Hidden -RedirectStandardOutput '%CADDY_OUT%' -RedirectStandardError '%CADDY_ERR%'"
timeout /t 2 /nobreak >nul

tasklist /FI "IMAGENAME eq caddy.exe" 2>nul | find /I "caddy.exe" >nul
if errorlevel 1 (
  echo  HATA: Caddy baslatilamadi.
  echo  Log: "%CADDY_ERR%"
  exit /b 24
)

echo  Caddy: HAZIR
exit /b 0

:START
cls
echo.
echo  ========================================================================
echo                         RAVEN STACK BASLATILIYOR
echo  ========================================================================
echo.

sc.exe query RavenMapServer >nul 2>&1
if not errorlevel 1 (
  echo  Windows Service kurulumu bulundu. Servisler baslatiliyor...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Service RavenMapServer -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; Start-Service RavenMapCaddy -ErrorAction SilentlyContinue"
  timeout /t 3 /nobreak >nul
  powershell -NoProfile -ExecutionPolicy Bypass -Command "try{$r=Invoke-RestMethod 'http://127.0.0.1:5088/health' -TimeoutSec 8; Write-Host ('OK - RavenMapServer Windows Service v'+$r.version) -ForegroundColor Green}catch{Write-Host ('HATA: '+$_.Exception.Message) -ForegroundColor Red}"
  echo.
  echo  Servis modu aktif. Bu pencereyi kapatabilirsiniz; Raven arka planda calisir.
  echo.
  pause
  goto MENU
)

if not exist "%~dp0App\Raven.Server.exe" (
  echo  HATA: App\Raven.Server.exe bulunamadi.
  echo.
  pause
  goto MENU
)
if not exist "%RAVEN_CONFIG_FILE%" (
  echo  HATA: Config\raven_server.json bulunamadi.
  echo.
  pause
  goto MENU
)
if not exist "%~dp0Secrets\license_private.pem" (
  echo  HATA: Secrets\license_private.pem bulunamadi.
  echo.
  pause
  goto MENU
)

call :PREPARE_CADDY
if errorlevel 1 (
  echo.
  echo  Raven.Server baslatilmadi.
  echo.
  pause
  goto MENU
)

echo.
echo  Public API : !PUBLIC_API!
echo  Local API  : http://127.0.0.1:5088
echo.
echo  ------------------------------------------------------------------------
echo  Raven.Server canli cikti asagida gorunecek.
echo  Sunucuyu kapatmak icin Ctrl+C kullanabilirsin.
echo  ------------------------------------------------------------------------
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $p=[Environment]::GetEnvironmentVariable('RAVEN_ADMIN_PASSWORD','Machine'); if([string]::IsNullOrWhiteSpace($p)){$p=[Environment]::GetEnvironmentVariable('RAVEN_ADMIN_PASSWORD','User')}; if([string]::IsNullOrWhiteSpace($p)){Write-Host 'HATA: RAVEN_ADMIN_PASSWORD bulunamadi. Once menu 2 ile Admin guvenlik ayarlarini yapin.' -ForegroundColor Red; exit 91}; $t=[Environment]::GetEnvironmentVariable('RAVEN_ADMIN_TOTP_SECRET','Machine'); if([string]::IsNullOrWhiteSpace($t)){$t=[Environment]::GetEnvironmentVariable('RAVEN_ADMIN_TOTP_SECRET','User')}; $env:RAVEN_ADMIN_PASSWORD=$p; $env:RAVEN_ADMIN_TOTP_SECRET=$t; Set-Location -LiteralPath (Join-Path $env:RAVEN_SERVER_ROOT 'App'); & '.\Raven.Server.exe'; exit $LASTEXITCODE"

set "RC=%ERRORLEVEL%"
cd /d "%~dp0"

echo.
echo  ------------------------------------------------------------------------
echo  Raven.Server kapandi. Exit code: %RC%
echo  ------------------------------------------------------------------------
echo.
pause
goto MENU

:SECRETS
cls
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\SetupServerSecrets.ps1"
echo.
pause
goto MENU

:HEALTH
cls
echo.
echo  LOCAL HEALTH TESTI
echo  ------------------------------------------------------------------------
powershell -NoProfile -ExecutionPolicy Bypass -Command "try{$r=Invoke-RestMethod 'http://127.0.0.1:5088/health' -TimeoutSec 5; Write-Host ('OK - v'+$r.version+' - Build '+$r.buildId) -ForegroundColor Green; $r|ConvertTo-Json -Depth 4}catch{Write-Host ('HATA: '+$_.Exception.Message) -ForegroundColor Red}"
echo.
pause
goto MENU

:PUBLICHEALTH
cls
call :READ_PUBLIC_API
echo.
echo  PUBLIC HTTPS HEALTH TESTI
echo  ------------------------------------------------------------------------
powershell -NoProfile -ExecutionPolicy Bypass -Command "$u='!PUBLIC_API!'.TrimEnd('/')+'/health'; try{$r=Invoke-RestMethod $u -TimeoutSec 10; Write-Host ('OK - '+$u) -ForegroundColor Green; $r|ConvertTo-Json -Depth 4}catch{Write-Host ('HATA - '+$u+' - '+$_.Exception.Message) -ForegroundColor Red}"
echo.
pause
goto MENU

:ADMIN
call :READ_PUBLIC_API
if /I "!PUBLIC_API!"=="Bilinmiyor" (
  start "" "http://127.0.0.1:5088/admin"
) else (
  start "" "!PUBLIC_API!/admin"
)
goto MENU

:CONFIG
notepad "%RAVEN_CONFIG_FILE%"
goto MENU

:TOTP
cls
powershell -NoProfile -Command "$scope='Machine'; $v=[Environment]::GetEnvironmentVariable('RAVEN_ADMIN_TOTP_SECRET','Machine'); if([string]::IsNullOrWhiteSpace($v)){$scope='User';$v=[Environment]::GetEnvironmentVariable('RAVEN_ADMIN_TOTP_SECRET','User')}; if([string]::IsNullOrWhiteSpace($v)){Write-Host 'TOTP secret ayarlanmamis.' -ForegroundColor Yellow}else{Write-Host ('TOTP secret ['+$scope+']:') -ForegroundColor DarkGray; Write-Host $v -ForegroundColor Cyan}"
echo.
pause
goto MENU

:SHOPIERTEST
cls
if not exist "%~dp0Tools\ShopierConfigTest.ps1" (
  echo.
  echo  HATA: Tools\ShopierConfigTest.ps1 bulunamadi.
  echo.
  pause
  goto MENU
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\ShopierConfigTest.ps1"
echo.
pause
goto MENU

:CADDY
cls
echo.
echo  CADDY KONTROL / RELOAD
echo  ------------------------------------------------------------------------
call :PREPARE_CADDY
if errorlevel 1 (
  echo.
  echo  Caddy islemi BASARISIZ.
) else (
  echo.
  echo  Caddy HAZIR.
  echo  Config: %CADDY_CONFIG%
)
echo.
pause
goto MENU

:UPDATE
cls
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\ApplyServerUpdate.ps1"
echo.
pause
goto MENU
