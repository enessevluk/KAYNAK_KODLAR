[CmdletBinding()]
param([switch]$RemoveProgramFiles)

$ErrorActionPreference = 'Stop'
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($id)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'KALDIR_RAVEN_SERVER.bat dosyasini yonetici olarak calistirin.'
}

$installRoot = [Environment]::GetEnvironmentVariable('RAVEN_SERVER_ROOT','Machine')
if ([string]::IsNullOrWhiteSpace($installRoot)) { $installRoot = Join-Path $env:ProgramData 'RavenMap\Server' }

& schtasks.exe /Delete /F /TN 'RavenMapHealthMonitor' 2>$null | Out-Null
foreach ($name in @('RavenMapCaddy','RavenMapServer')) {
    if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
        Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
        & "$env:SystemRoot\System32\sc.exe" delete $name | Out-Null
    }
}
foreach ($name in @('RavenMap HTTPS 80','RavenMap HTTPS 443')) {
    Remove-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
}
[Environment]::SetEnvironmentVariable('RAVEN_SERVER_ROOT',$null,[EnvironmentVariableTarget]::Machine)
[Environment]::SetEnvironmentVariable('RAVEN_CONFIG_FILE',$null,[EnvironmentVariableTarget]::Machine)
[Environment]::SetEnvironmentVariable('RAVEN_ADMIN_PASSWORD',$null,[EnvironmentVariableTarget]::Machine)
[Environment]::SetEnvironmentVariable('RAVEN_ADMIN_TOTP_SECRET',$null,[EnvironmentVariableTarget]::Machine)

Write-Host 'RavenMap servisleri, health gorevi ve firewall kurallari kaldirildi.' -ForegroundColor Green
Write-Host "Data, Config, Secrets ve Backups guvenlik icin SILINMEDI: $installRoot" -ForegroundColor Yellow
if ($RemoveProgramFiles) {
    throw 'Guvenlik nedeniyle otomatik veri silme desteklenmez. Yedek aldiktan sonra klasoru elle kaldirin.'
}
