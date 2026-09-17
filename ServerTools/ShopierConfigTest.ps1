$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$configPath = Join-Path $root 'Config\raven_server.json'

if (-not (Test-Path $configPath)) {
    Write-Host "HATA: Config bulunamadi: $configPath" -ForegroundColor Red
    exit 1
}

try {
    $cfg = Get-Content -Raw -Encoding UTF8 $configPath | ConvertFrom-Json
} catch {
    Write-Host ('HATA: raven_server.json okunamadi: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}

$pat = [string]$cfg.Shopier.ApiKey
if ($null -eq $pat) { $pat = '' }
$pat = $pat.Trim()

Write-Host '=== RAVEN SHOPIER CONFIG TESTI ===' -ForegroundColor Cyan
Write-Host "Config: $configPath"
Write-Host "Shopier Enabled: $($cfg.Shopier.Enabled)"
Write-Host "ApiKey uzunlugu: $($pat.Length) karakter"

if ([string]::IsNullOrWhiteSpace($pat)) {
    Write-Host 'HATA: Shopier.ApiKey config icinde bos.' -ForegroundColor Red
    exit 2
}
if ($pat.Length -lt 16) {
    Write-Host "HATA: Shopier.ApiKey cok kisa ($($pat.Length) karakter)." -ForegroundColor Red
    exit 3
}
if ($pat.ToCharArray() | Where-Object { [char]::IsControl($_) } | Select-Object -First 1) {
    Write-Host 'HATA: Shopier.ApiKey kontrol karakteri iceriyor.' -ForegroundColor Red
    exit 4
}

$headers = @{ Authorization = "Bearer $pat"; Accept = 'application/json' }

function Test-Url([string]$label, [string]$url) {
    Write-Host ''
    Write-Host "[$label] $url" -ForegroundColor Yellow
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri $url -Headers $headers -Method Get -TimeoutSec 20
        Write-Host "HTTP $([int]$r.StatusCode) $($r.StatusDescription)" -ForegroundColor Green
        return $true
    } catch {
        $status = $null
        $body = ''
        try { $status = [int]$_.Exception.Response.StatusCode } catch {}
        try {
            $stream = $_.Exception.Response.GetResponseStream()
            if ($null -ne $stream) {
                $reader = New-Object IO.StreamReader($stream)
                $body = $reader.ReadToEnd()
            }
        } catch {}
        if ($null -ne $status) { Write-Host "HTTP $status" -ForegroundColor Red }
        else { Write-Host ('HTTP HATA: ' + $_.Exception.Message) -ForegroundColor Red }
        if (-not [string]::IsNullOrWhiteSpace($body)) {
            $safe = $body
            if ($safe.Length -gt 1000) { $safe = $safe.Substring(0,1000) + '...' }
            Write-Host $safe -ForegroundColor DarkGray
        }
        return $false
    }
}

$ownerOk = Test-Url 'PAT' 'https://api.shopier.com/v1/shop/owner'

$end = [DateTime]::UtcNow.AddMinutes(1).ToString('yyyy-MM-ddTHH:mm:ssZ')
$start = [DateTime]::UtcNow.AddDays(-7).ToString('yyyy-MM-ddTHH:mm:ssZ')
$orders = 'https://api.shopier.com/v1/orders?limit=10&page=1&sort=dateDesc&dateStart=' + [Uri]::EscapeDataString($start) + '&dateEnd=' + [Uri]::EscapeDataString($end)
$ordersOk = Test-Url 'ORDERS' $orders

Write-Host ''
if ($ownerOk -and $ordersOk) {
    Write-Host 'SONUC: Shopier config ve API baglantisi OK.' -ForegroundColor Green
    exit 0
}
if (-not $ownerOk) {
    Write-Host 'SONUC: Configteki PAT Shopier tarafinda dogrulanamadi.' -ForegroundColor Red
    exit 5
}
Write-Host 'SONUC: PAT dogru, ancak orders istegi hata veriyor.' -ForegroundColor Yellow
exit 6
