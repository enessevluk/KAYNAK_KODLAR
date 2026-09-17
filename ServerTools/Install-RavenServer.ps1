[CmdletBinding()]
param(
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$InstallRoot = (Join-Path $env:ProgramData 'RavenMap\Server'),
    [string]$Domain = '',
    [switch]$Repair,
    [switch]$DownloadCaddy,
    [switch]$SkipDnsCheck,
    [switch]$SkipPublicHealth
)

$ErrorActionPreference = 'Stop'
$serverService = 'RavenMapServer'
$caddyService = 'RavenMapCaddy'
$healthTask = 'RavenMapHealthMonitor'

function Write-Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Fail([string]$Text) { throw $Text }
function Is-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Normalize-Domain([string]$Value) {
    $text = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { Fail 'Domain bos olamaz.' }
    if ($text -notmatch '^https?://') { $text = 'https://' + $text }
    try { $uri = [Uri]$text } catch { Fail "Domain gecersiz: $Value" }
    if ($uri.Scheme -ne 'https') { Fail 'Production kurulumu HTTPS domaini kullanmalidir.' }
    if ($uri.HostNameType -ne [UriHostNameType]::Dns -or $uri.Host -notmatch '\.') { Fail 'Gercek bir domain/subdomain girin. Ornek: api.example.com' }
    if (-not [string]::IsNullOrWhiteSpace($uri.Query) -or -not [string]::IsNullOrWhiteSpace($uri.Fragment)) { Fail 'Domain adresinde query veya fragment olamaz.' }
    if ($uri.AbsolutePath -ne '/') { Fail 'Yalniz domain girin; alt yol kullanmayin.' }
    return $uri.Host.ToLowerInvariant()
}
function Run-Sc([string[]]$Arguments, [switch]$AllowMissing) {
    $output = & "$env:SystemRoot\System32\sc.exe" @Arguments 2>&1
    if ($LASTEXITCODE -ne 0 -and -not $AllowMissing) { Fail ("sc.exe basarisiz: " + ($output -join ' ')) }
    return $output
}
function Wait-Health([string]$Url, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        try {
            $response = Invoke-RestMethod -Uri $Url -TimeoutSec 5
            if ($response.ok -eq $true -or $null -ne $response.version) { return $true }
        } catch { }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return $false
}
function Resolve-Caddy([string]$Destination) {
    $bundled = Join-Path $SourceRoot 'caddy.exe'
    if (Test-Path -LiteralPath $bundled) {
        Copy-Item -LiteralPath $bundled -Destination $Destination -Force
        return
    }
    $command = Get-Command caddy.exe -ErrorAction SilentlyContinue
    if ($command) {
        Copy-Item -LiteralPath $command.Source -Destination $Destination -Force
        return
    }

    $allowDownload = $DownloadCaddy
    if (-not $allowDownload) {
        $answer = Read-Host 'Caddy bulunamadi. Resmi caddyserver.com adresinden indirilsin mi? (E/H) [E]'
        $allowDownload = [string]::IsNullOrWhiteSpace($answer) -or $answer -match '^(e|evet|y|yes)$'
    }
    if (-not $allowDownload) { Fail 'Caddy olmadan HTTPS kurulumu tamamlanamaz. CLOUDFLARE_DOMAIN_REHBERI.txt dosyasina bakin.' }

    Write-Step 'Caddy resmi indirme servisinden aliniyor'
    $temporary = Join-Path $env:TEMP ('raven-caddy-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    try {
        $download = Join-Path $temporary 'caddy-download.bin'
        Invoke-WebRequest -UseBasicParsing -Uri 'https://caddyserver.com/api/download?os=windows&arch=amd64' -OutFile $download
        $header = [IO.File]::ReadAllBytes($download) | Select-Object -First 2
        if ($header.Count -eq 2 -and $header[0] -eq 0x50 -and $header[1] -eq 0x4B) {
            $zip = Join-Path $temporary 'caddy.zip'
            Move-Item -LiteralPath $download -Destination $zip
            Expand-Archive -LiteralPath $zip -DestinationPath $temporary -Force
            $exe = Get-ChildItem -LiteralPath $temporary -Filter caddy.exe -File -Recurse | Select-Object -First 1
            if (-not $exe) { Fail 'Indirilen resmi Caddy arsivinde caddy.exe bulunamadi.' }
            Copy-Item -LiteralPath $exe.FullName -Destination $Destination -Force
        } else {
            Copy-Item -LiteralPath $download -Destination $Destination -Force
        }
    } finally {
        Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($env:OS -ne 'Windows_NT') { Fail 'Bu kurucu yalniz Windows VDS icindir.' }
if (-not (Is-Admin)) { Fail 'KUR_RAVEN_SERVER.bat dosyasini yonetici olarak calistirin.' }

$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
foreach ($required in @('App\Raven.Server.exe','Config\raven_server.json','Secrets\license_private.pem','Secrets\update_public.pem','Caddyfile','Tools\SetupServerSecrets.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot $required))) { Fail "Kurulum paketinde zorunlu dosya eksik: $required" }
}

$configSource = Join-Path $SourceRoot 'Config\raven_server.json'
$config = Get-Content -LiteralPath $configSource -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($Domain)) {
    $current = ([Uri]([string]$config.Raven.PublicBaseUrl)).Host
    $entered = Read-Host "Raven API domaini (Enter = $current)"
    $Domain = if ([string]::IsNullOrWhiteSpace($entered)) { $current } else { $entered }
}
$Domain = Normalize-Domain $Domain
$publicUrl = "https://$Domain"

Write-Step 'Domain ve DNS kontrol ediliyor'
if (-not $SkipDnsCheck) {
    try {
        $resolved = Resolve-DnsName -Name $Domain -Type A -ErrorAction Stop | Where-Object IPAddress
        if (-not $resolved) { Fail "A kaydi bulunamadi: $Domain" }
        Write-Host ("DNS A: " + (($resolved.IPAddress | Sort-Object -Unique) -join ', ')) -ForegroundColor Green
    } catch {
        Fail "Cloudflare DNS A kaydi henuz gorunmuyor: $Domain. CLOUDFLARE_DOMAIN_REHBERI.txt dosyasini uygulayin. Ayrinti: $($_.Exception.Message)"
    }
}

Write-Step 'RavenMap dosyalari kalici alana kuruluyor'
$sameRoot = $SourceRoot.TrimEnd('\') -ieq $InstallRoot.TrimEnd('\')
if (-not $sameRoot) {
    if ((Test-Path -LiteralPath $InstallRoot) -and (Get-ChildItem -LiteralPath $InstallRoot -Force -ErrorAction SilentlyContinue) -and -not $Repair) {
        Fail "Kurulum klasoru bos degil: $InstallRoot. Mevcut sistem icin signed SERVER_UPDATE kullanin veya bilincli olarak -Repair secin."
    }
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
    if ($Repair) {
        foreach ($name in @('App','Content','Tools','RAVEN_SERVER.bat','KUR_RAVEN_SERVER.bat','KALDIR_RAVEN_SERVER.bat','CLOUDFLARE_DOMAIN_REHBERI.txt')) {
            $source = Join-Path $SourceRoot $name
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $InstallRoot -Recurse -Force }
        }
    } else {
        Copy-Item -Path (Join-Path $SourceRoot '*') -Destination $InstallRoot -Recurse -Force
    }
}

$configPath = Join-Path $InstallRoot 'Config\raven_server.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config.Raven.PublicBaseUrl = $publicUrl
$config.Raven.ListenUrl = 'http://127.0.0.1:5088'
$config.Raven.Environment = 'Production'
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $InstallRoot 'Caddyfile'), "$Domain {`n    reverse_proxy 127.0.0.1:5088`n}`n", [Text.UTF8Encoding]::new($false))

foreach ($directory in @('Data','Logs','Updates','Backups')) { New-Item -ItemType Directory -Path (Join-Path $InstallRoot $directory) -Force | Out-Null }

Write-Step 'Caddy hazirlaniyor'
$caddyPath = Join-Path $InstallRoot 'caddy.exe'
if (-not (Test-Path -LiteralPath $caddyPath)) { Resolve-Caddy $caddyPath }
& $caddyPath version
if ($LASTEXITCODE -ne 0) { Fail 'caddy.exe calistirilamadi.' }
& $caddyPath validate --config (Join-Path $InstallRoot 'Caddyfile') --adapter caddyfile
if ($LASTEXITCODE -ne 0) { Fail 'Caddyfile dogrulamasi basarisiz.' }

Write-Step 'Admin parolasi ve Authenticator ayarlaniyor'
try {
    & (Join-Path $InstallRoot 'Tools\SetupServerSecrets.ps1') -Scope Machine
} catch {
    Fail "Admin guvenlik ayarlari tamamlanamadi: $($_.Exception.Message)"
}
[Environment]::SetEnvironmentVariable('RAVEN_SERVER_ROOT', $InstallRoot, [EnvironmentVariableTarget]::Machine)
[Environment]::SetEnvironmentVariable('RAVEN_CONFIG_FILE', $configPath, [EnvironmentVariableTarget]::Machine)

Write-Step 'Secret klasoru Windows ACL ile korunuyor'
$secretsPath = Join-Path $InstallRoot 'Secrets'
& icacls.exe $secretsPath /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { Fail 'Secrets klasoru izinleri uygulanamadi.' }

Write-Step 'Windows servisleri kuruluyor'
foreach ($name in @($caddyService,$serverService)) {
    $existing = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($existing) { Stop-Service -Name $name -Force -ErrorAction SilentlyContinue }
}
$serverExe = Join-Path $InstallRoot 'App\Raven.Server.exe'
$serverBin = '"' + $serverExe + '"'
$caddyBin = '"' + $caddyPath + '" run --config "' + (Join-Path $InstallRoot 'Caddyfile') + '" --adapter caddyfile'
if (Get-Service -Name $serverService -ErrorAction SilentlyContinue) {
    Run-Sc @('config',$serverService,'binPath=', $serverBin,'start=', 'delayed-auto') | Out-Null
} else {
    Run-Sc @('create',$serverService,'binPath=', $serverBin,'start=', 'delayed-auto','DisplayName=', 'Raven Map Server') | Out-Null
}
Run-Sc @('description',$serverService,'RavenMap lisans, Shopier, update ve prefab API servisi.') | Out-Null
Run-Sc @('failure',$serverService,'reset=', '86400','actions=', 'restart/5000/restart/10000/restart/30000') | Out-Null

if (Get-Service -Name $caddyService -ErrorAction SilentlyContinue) {
    Run-Sc @('config',$caddyService,'binPath=', $caddyBin,'start=', 'delayed-auto','depend=', $serverService) | Out-Null
} else {
    Run-Sc @('create',$caddyService,'binPath=', $caddyBin,'start=', 'delayed-auto','depend=', $serverService,'DisplayName=', 'Raven Map HTTPS') | Out-Null
}
Run-Sc @('description',$caddyService,'RavenMap HTTPS reverse proxy ve otomatik TLS servisi.') | Out-Null
Run-Sc @('failure',$caddyService,'reset=', '86400','actions=', 'restart/5000/restart/10000/restart/30000') | Out-Null

Write-Step 'Windows Firewall kurallari uygulanıyor'
foreach ($port in @(80,443)) {
    $name = "RavenMap HTTPS $port"
    if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -Program $caddyPath | Out-Null
    }
}

Write-Step 'Otomatik health monitor kuruluyor'
$monitorPath = Join-Path $InstallRoot 'Tools\Raven-HealthMonitor.ps1'
$taskCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$monitorPath`""
& schtasks.exe /Create /F /TN $healthTask /SC MINUTE /MO 5 /RU SYSTEM /RL HIGHEST /TR $taskCommand | Out-Null
if ($LASTEXITCODE -ne 0) { Fail 'Health monitor zamanlanmis gorevi olusturulamadi.' }

Write-Step 'RavenMap servisleri baslatiliyor'
Start-Service -Name $serverService
if (-not (Wait-Health 'http://127.0.0.1:5088/health' 45)) { Fail 'RavenMapServer servisi basladi fakat local /health yanit vermedi. Windows Event Viewer ve Logs klasorunu kontrol edin.' }
Start-Service -Name $caddyService

$publicOk = $SkipPublicHealth -or (Wait-Health "$publicUrl/health" 90)
$state = [ordered]@{
    schemaVersion = 1
    installedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    installRoot = $InstallRoot
    publicUrl = $publicUrl
    serverService = $serverService
    caddyService = $caddyService
    healthTask = $healthTask
    publicHealthVerified = [bool]$publicOk
    caddySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $caddyPath).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $InstallRoot 'Data\install-state.json'), ($state | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

Write-Host "`nRAVENMAP KURULUMU TAMAMLANDI" -ForegroundColor Green
Write-Host "Server klasoru : $InstallRoot"
Write-Host "Public API     : $publicUrl"
Write-Host "Admin paneli   : $publicUrl/admin"
Write-Host "Otomatik acilis: AKTIF"
Write-Host "Crash recovery : AKTIF"
Write-Host "Health monitor : 5 dakikada bir"
if (-not $publicOk) {
    Write-Host "UYARI: Public HTTPS health henuz dogrulanamadi. Cloudflare DNS, proxy ve SSL/TLS ayarlarini rehberden kontrol edin." -ForegroundColor Yellow
} else {
    Write-Host 'Public HTTPS health: PASS' -ForegroundColor Green
    Start-Process "$publicUrl/admin"
}
