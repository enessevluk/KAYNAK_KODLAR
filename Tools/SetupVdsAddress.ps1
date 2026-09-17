param(
    [string]$ServerConfig = "",
    [switch]$SkipClientConfig
)
$ErrorActionPreference = 'Stop'
$toolsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = Split-Path -Parent $toolsRoot
$workspaceRoot = Split-Path -Parent $sourceRoot

if ([string]::IsNullOrWhiteSpace($ServerConfig)) {
    $deployed = Join-Path $workspaceRoot 'SERVER\Config\raven_server.json'
    $source = Join-Path $sourceRoot 'Raven.Server\raven_server.json'
    if (Test-Path $deployed) { $ServerConfig = $deployed } else { $ServerConfig = $source }
}
$ServerConfig = [IO.Path]::GetFullPath($ServerConfig)
if (-not (Test-Path $ServerConfig)) { throw "Server config bulunamadi: $ServerConfig" }

function Ask([string]$label, [string]$current) {
    $suffix = if ([string]::IsNullOrWhiteSpace($current)) { '' } else { " [$current]" }
    $v = Read-Host "$label$suffix"
    if ([string]::IsNullOrWhiteSpace($v)) { return $current }
    return $v.Trim()
}

$c = Get-Content -Raw -Encoding UTF8 $ServerConfig | ConvertFrom-Json
Write-Host ''
Write-Host '=== RAVEN ILK KURULUM / VDS ADRESI ===' -ForegroundColor Cyan
Write-Host "Config: $ServerConfig"
Write-Host ''
Write-Host 'Bu ekran yalniz ag adresini ayarlar.' -ForegroundColor Green
Write-Host 'Shopier, urunler ve Discord/destek ayarlari Admin Panel > Sistem Ayarlari bolumunden yonetilir.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Test: http://VDS_IP:5088   Production: https://raven.senindomainin.com'
$public = Ask 'VDS public Raven API adresi' ([string]$c.Raven.PublicBaseUrl)
if ($public -match '^\d{1,3}(\.\d{1,3}){3}$') { $public = "http://$public`:5088" }
if ($public -notmatch '^https?://') { throw 'Adres http:// veya https:// ile baslamali. Yalniz IPv4 yazarsan test icin otomatik http://IP:5088 yapilir.' }
try { $u = [Uri]$public } catch { throw 'Gecersiz URL.' }

$ipObj = $null
$isIp = [Net.IPAddress]::TryParse($u.Host, [ref]$ipObj)
if ($u.Scheme -eq 'https' -and $isIp -and $u.Port -eq 5088) {
    $public = "http://$($u.Host):5088"
    $u = [Uri]$public
    Write-Host "UYARI: IP:5088 test kurulumunda HTTPS kullanilamaz; adres otomatik HTTP yapildi: $public" -ForegroundColor Yellow
}

$c.Raven.PublicBaseUrl = $public.TrimEnd('/')
$c.Raven.ListenUrl = 'http://0.0.0.0:5088'
$c.Raven.Environment = if ($u.Scheme -eq 'https') { 'Production' } else { 'Development' }

$backup = "$ServerConfig.backup_$(Get-Date -Format yyyyMMdd_HHmmss)"
Copy-Item $ServerConfig $backup -Force
$c | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 $ServerConfig
Write-Host "`nVDS adresi kaydedildi. Backup: $backup" -ForegroundColor Green

if (-not $SkipClientConfig) {
    $targets = @(
        (Join-Path $sourceRoot 'RavenMapPanel.Wpf\Config\raven_server.json'),
        (Join-Path $workspaceRoot 'CLIENT\App\Config\raven_server.json')
    ) | Select-Object -Unique
    foreach ($clientConfig in $targets) {
        $parent = Split-Path -Parent $clientConfig
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
        $existingClient = $null
        if (Test-Path -LiteralPath $clientConfig) {
            try { $existingClient = Get-Content -LiteralPath $clientConfig -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $existingClient = $null }
        }
        $licenseCenter = ([string]$existingClient.LicenseCenterUrl).Trim()
        if ([string]::IsNullOrWhiteSpace($licenseCenter)) { $licenseCenter = 'https://license.ravenrusttr.com.tr' }
        $client = [ordered]@{
            BaseUrl = $c.Raven.PublicBaseUrl
            LicenseCenterUrl = $licenseCenter
            LicenseProduct = 'raven_map'
            StoreUrl = ([string]$existingClient.StoreUrl).Trim()
            SupportUrl = ([string]$existingClient.SupportUrl).Trim()
            AllowInsecureHttpForTesting = ($u.Scheme -eq 'http')
        }
        $client | ConvertTo-Json | Set-Content -Encoding UTF8 $clientConfig
        Write-Host "Client API config: $clientConfig" -ForegroundColor Green
    }
}

Write-Host ''
if ($u.Scheme -eq 'http') {
    Write-Host 'UYARI: Uzak VDS icin HTTP yalniz TEST modudur. Musteriye dagitirken HTTPS kullan.' -ForegroundColor Yellow
}
Write-Host 'Normal Shopier/destek degisiklikleri icin bu scripti tekrar calistirma; Admin Paneli kullan.' -ForegroundColor Green
