$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$update = Join-Path $root 'Update'
$newApp = Join-Path $update 'App'
$app = Join-Path $root 'App'
$backup = Join-Path $root 'App_Backup'
$newPrefabs = Join-Path $update 'Content\Prefabs'
$prefabs = Join-Path $root 'Content\Prefabs'
$prefabBackup = Join-Path $root 'Content\Prefabs_Backup'
$prefabsUpdated = $false
$prefabsHadOriginal = $false
$usedZip = $null
$usedSignature = $null
$usedHash = $null
$startedServer = $null
$healthValidated = $false
$serviceMode = $null -ne (Get-Service -Name 'RavenMapServer' -ErrorAction SilentlyContinue)

function Fail([string]$m) { Write-Host ('HATA: ' + $m) -ForegroundColor Red; exit 1 }

function Get-LocalHealthUrl {
    $configPath = Join-Path $root 'Config\raven_server.json'
    if (-not (Test-Path -LiteralPath $configPath)) {
        throw 'Config\raven_server.json bulunamadi; yerel saglik adresi belirlenemedi.'
    }

    try {
        $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $listenUrl = [string]$config.Raven.ListenUrl
        if ([string]::IsNullOrWhiteSpace($listenUrl)) { $listenUrl = 'http://0.0.0.0:5088' }
        $listenUri = [Uri]$listenUrl
        $builder = [UriBuilder]::new($listenUri)
        $builder.Host = '127.0.0.1'
        $builder.Path = 'health'
        $builder.Query = ''
        return $builder.Uri.AbsoluteUri.TrimEnd('/')
    }
    catch {
        throw ('Yerel saglik adresi olusturulamadi: ' + $_.Exception.Message)
    }
}

function Start-RavenServerHidden {
    $serverExe = Join-Path $app 'Raven.Server.exe'
    $configPath = Join-Path $root 'Config\raven_server.json'
    if (-not (Test-Path -LiteralPath $serverExe)) { throw 'Yeni App\Raven.Server.exe bulunamadi.' }

    $adminPassword = [Environment]::GetEnvironmentVariable('RAVEN_ADMIN_PASSWORD','Machine')
    if ([string]::IsNullOrWhiteSpace($adminPassword)) {
        $adminPassword = [Environment]::GetEnvironmentVariable('RAVEN_ADMIN_PASSWORD','User')
    }
    if ([string]::IsNullOrWhiteSpace($adminPassword)) {
        throw 'RAVEN_ADMIN_PASSWORD bulunamadi. Once RAVEN_SERVER.bat menu 2 ile admin guvenlik ayarlarini yapin.'
    }

    $adminTotp = [Environment]::GetEnvironmentVariable('RAVEN_ADMIN_TOTP_SECRET','Machine')
    if ([string]::IsNullOrWhiteSpace($adminTotp)) {
        $adminTotp = [Environment]::GetEnvironmentVariable('RAVEN_ADMIN_TOTP_SECRET','User')
    }

    # Start-Process mevcut process environment'ini devralir. Server, normal
    # RAVEN_SERVER.bat baslatmasiyla ayni root/config/secret degerlerini gorur.
    $env:RAVEN_SERVER_ROOT = $root
    $env:RAVEN_CONFIG_FILE = $configPath
    $env:RAVEN_ADMIN_PASSWORD = $adminPassword
    $env:RAVEN_ADMIN_TOTP_SECRET = $adminTotp

    return Start-Process -FilePath $serverExe -WorkingDirectory $app -WindowStyle Hidden -PassThru
}

function Start-RavenRuntime {
    if ($serviceMode) {
        Start-Service -Name 'RavenMapServer'
        return $null
    }
    return Start-RavenServerHidden
}

function Wait-RavenHealth([Diagnostics.Process]$Process, [string]$ExpectedVersion, [int]$TimeoutSeconds = 60) {
    $healthUrl = Get-LocalHealthUrl
    Write-Host "Yeni server saglik kontrolu: $healthUrl" -ForegroundColor Cyan
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = 'Server henuz cevap vermedi.'

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($null -ne $Process) {
            $Process.Refresh()
            if ($Process.HasExited) {
                throw "Yeni Raven.Server beklenmedik sekilde kapandi. Exit code: $($Process.ExitCode)"
            }
        }

        try {
            $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 3
            $reportedVersion = [string]$health.version
            if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $reportedVersion -ne $ExpectedVersion) {
                throw "Saglik endpoint'i beklenen surumu bildirmedi. Beklenen=$ExpectedVersion, Gelen=$reportedVersion"
            }
            if ($health.ok -eq $false) { throw 'Saglik endpointi ok=false bildirdi.' }
            Write-Host "Saglik kontrolu: OK - v$reportedVersion - Build $($health.buildId)" -ForegroundColor Green
            return
        }
        catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Seconds 1
        }
    }

    throw "Yeni server $TimeoutSeconds saniye icinde saglikli duruma gecmedi. Son hata: $lastError"
}

# Production update yalniz imzali ZIP ile uygulanir. Pre-extracted Update\App
# klasoru tek basina kabul edilmez; aksi halde RSA dogrulamasi bypass edilebilir.
$candidate = Get-ChildItem -LiteralPath $root -File -Filter 'RavenServer_UPDATE_*.zip' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $candidate) {
    Fail 'Imzali RavenServer_UPDATE_*.zip bulunamadi. ZIP + .sig + .sha256 dosyalarini SERVER klasorune kopyalayin.'
}

$signaturePath = $candidate.FullName + '.sig'
$hashPath = $candidate.FullName + '.sha256'
$publicKeyPath = Join-Path $root 'Secrets\update_public.pem'
$verifier = Join-Path $app 'Raven.Server.exe'
if (-not (Test-Path -LiteralPath $signaturePath)) { Fail "Update RSA imzasi bulunamadi: $([IO.Path]::GetFileName($signaturePath))" }
if (-not (Test-Path -LiteralPath $hashPath)) { Fail "Update SHA-256 dosyasi bulunamadi: $([IO.Path]::GetFileName($hashPath))" }
if (-not (Test-Path -LiteralPath $publicKeyPath)) { Fail 'Secrets\update_public.pem bulunamadi.' }
if (-not (Test-Path -LiteralPath $verifier)) { Fail 'Mevcut App\Raven.Server.exe update dogrulayicisi bulunamadi.' }

Write-Host "Update paketi bulundu: $($candidate.Name)" -ForegroundColor Cyan
Write-Host 'RSA-SHA256 imzasi dogrulaniyor...' -ForegroundColor Cyan
& $verifier '--verify-server-update' $candidate.FullName $signaturePath $publicKeyPath
if ($LASTEXITCODE -ne 0) { Fail 'Server update RSA/SHA-256 dogrulamasi basarisiz. Paket uygulanmadi.' }
Write-Host 'Update imzasi: OK' -ForegroundColor Green

if (Test-Path $update) { Remove-Item $update -Recurse -Force }
Expand-Archive -LiteralPath $candidate.FullName -DestinationPath $root -Force
$usedZip = $candidate.FullName
$usedSignature = $signaturePath
$usedHash = $hashPath

if (-not (Test-Path (Join-Path $newApp 'Raven.Server.exe'))) {
    Fail 'Imzali paketin icinde Update\App\Raven.Server.exe bulunamadi.'
}
$manifestPath = Join-Path $update 'server-update-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { Fail 'Imzali server update manifesti bulunamadi.' }
try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$manifest.product -ne 'Raven.Server' -or [string]::IsNullOrWhiteSpace([string]$manifest.version)) {
        Fail 'Server update manifesti gecersiz.'
    }
    Write-Host ("Manifest: Raven.Server v{0} · Build {1}" -f $manifest.version,$manifest.buildId) -ForegroundColor DarkGreen
} catch {
    Fail ('Server update manifesti okunamadi: ' + $_.Exception.Message)
}

Write-Host 'Raven.Server kapatiliyor...' -ForegroundColor Yellow
if ($serviceMode) {
    Stop-Service -Name 'RavenMapServer' -Force -ErrorAction Stop
    $service = Get-Service -Name 'RavenMapServer'
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
} else {
    $runningServers = @(Get-Process Raven.Server -ErrorAction SilentlyContinue)
    if ($runningServers.Count -gt 0) {
        $runningServers | Stop-Process -Force -ErrorAction SilentlyContinue
        $runningServers | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    }
}
Start-Sleep -Seconds 1

try {
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    if (Test-Path $app) { Move-Item $app $backup -Force }
    Move-Item $newApp $app -Force

    # Yeni server build prefab katalogu iceriyorsa, sadece Content\Prefabs alanini
    # kaynak build ile birebir degistir. Config/Data/Secrets ve diger Content korunur.
    if (Test-Path $newPrefabs) {
        if (Test-Path $prefabBackup) { Remove-Item $prefabBackup -Recurse -Force }
        if (Test-Path $prefabs) {
            $prefabsHadOriginal = $true
            Move-Item $prefabs $prefabBackup -Force
        }
        $contentRoot = Split-Path -Parent $prefabs
        if (-not (Test-Path $contentRoot)) { New-Item -ItemType Directory -Path $contentRoot -Force | Out-Null }
        Move-Item $newPrefabs $prefabs -Force
        $prefabsUpdated = $true
    }

    $newLauncher = Join-Path $update 'RAVEN_SERVER.bat'
    if (Test-Path $newLauncher) { Copy-Item $newLauncher (Join-Path $root 'RAVEN_SERVER.bat') -Force }
    foreach ($rootFile in @('KUR_RAVEN_SERVER.bat','KALDIR_RAVEN_SERVER.bat','CLOUDFLARE_DOMAIN_REHBERI.txt')) {
        $newRootFile = Join-Path $update $rootFile
        if (Test-Path -LiteralPath $newRootFile) { Copy-Item -LiteralPath $newRootFile -Destination (Join-Path $root $rootFile) -Force }
    }

    $newTools = Join-Path $update 'Tools'
    if (Test-Path $newTools) {
        $tools = Join-Path $root 'Tools'
        if (-not (Test-Path $tools)) { New-Item -ItemType Directory -Path $tools -Force | Out-Null }
        Get-ChildItem -LiteralPath $newTools -File | ForEach-Object {
            # Running updater itself is not overwritten until the process exits; stage it when needed.
            if ($_.Name -ieq 'ApplyServerUpdate.ps1') {
                Copy-Item $_.FullName (Join-Path $tools 'ApplyServerUpdate.next.ps1') -Force
            } else {
                Copy-Item $_.FullName (Join-Path $tools $_.Name) -Force
            }
        }
    }

    Write-Host ($(if ($serviceMode) { 'Yeni RavenMapServer servisi baslatiliyor...' } else { 'Yeni Raven.Server arka planda baslatiliyor...' })) -ForegroundColor Cyan
    $startedServer = Start-RavenRuntime
    Wait-RavenHealth -Process $startedServer -ExpectedVersion ([string]$manifest.version) -TimeoutSeconds 60
    $healthValidated = $true

    # Yedekler ve update paketi yalniz yeni surum gercekten ayaga kalkip
    # dogru surumu /health uzerinden bildirdikten sonra temizlenir.
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    if (Test-Path $prefabBackup) { Remove-Item $prefabBackup -Recurse -Force }
    if (Test-Path $update) { Remove-Item $update -Recurse -Force }
    if ($null -ne $usedZip -and (Test-Path $usedZip)) { Remove-Item $usedZip -Force -ErrorAction SilentlyContinue }
    if ($null -ne $usedSignature -and (Test-Path $usedSignature)) { Remove-Item $usedSignature -Force -ErrorAction SilentlyContinue }
    if ($null -ne $usedHash -and (Test-Path $usedHash)) { Remove-Item $usedHash -Force -ErrorAction SilentlyContinue }

    $next = Join-Path $root 'Tools\ApplyServerUpdate.next.ps1'
    $current = Join-Path $root 'Tools\ApplyServerUpdate.ps1'
    if (Test-Path $next) {
        # A tiny delayed helper swaps the updater after this process has finished reading it.
        $cmd = "Start-Sleep -Milliseconds 500; Move-Item -Force '" + $next.Replace("'","''") + "' '" + $current.Replace("'","''") + "'"
        Start-Process powershell -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-Command',$cmd) | Out-Null
    }

    Write-Host ''
    Write-Host 'SERVER UPDATE TAMAMLANDI.' -ForegroundColor Green
    if ($prefabsUpdated) {
        Write-Host 'Config, Data ve Secrets korundu. Content\Prefabs yeni kaynak katalogla guncellendi.' -ForegroundColor DarkGreen
    } else {
        Write-Host 'Config, Data, Secrets ve Content korundu. Bu eski tip update paketinde prefab katalogu yoktu.' -ForegroundColor DarkGreen
    }
    Write-Host ($(if ($serviceMode) { 'Yeni server Windows servisi olarak baslatildi ve saglik kontrolunden gecti.' } else { 'Yeni server arka planda baslatildi ve saglik kontrolunden gecti.' })) -ForegroundColor Cyan
    exit 0
}
catch {
    Write-Host ('Update hatasi: ' + $_.Exception.Message) -ForegroundColor Red
    if ($healthValidated) {
        # Yeni surum dogru surumle ayaga kalktiysa bundan sonraki hata yalniz
        # yedek/update artiklarini temizleme asamasindadir. Calisan saglikli
        # serveri ve yeni App'i geri alma; artiklar sonraki bakimda temizlenebilir.
        Write-Host 'Yeni server saglikli calisiyor; yalniz eski update/yedek artiklarinin bir kismi temizlenemedi.' -ForegroundColor Yellow
        Write-Host 'Guncelleme uygulandi. Calisan yeni server korunuyor.' -ForegroundColor Green
        exit 0
    }
    try {
        if ($serviceMode) {
            Stop-Service -Name 'RavenMapServer' -Force -ErrorAction SilentlyContinue
        } elseif ($null -ne $startedServer) {
            $startedServer.Refresh()
            if (-not $startedServer.HasExited) {
                Stop-Process -Id $startedServer.Id -Force -ErrorAction SilentlyContinue
                Wait-Process -Id $startedServer.Id -Timeout 15 -ErrorAction SilentlyContinue
            }
        }
        if (Test-Path $app) { Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue }
        if (Test-Path $backup) { Move-Item $backup $app -Force }

        if (Test-Path $prefabBackup) {
            if (Test-Path $prefabs) { Remove-Item $prefabs -Recurse -Force -ErrorAction SilentlyContinue }
            Move-Item $prefabBackup $prefabs -Force
        } elseif ($prefabsUpdated -and -not $prefabsHadOriginal) {
            if (Test-Path $prefabs) { Remove-Item $prefabs -Recurse -Force -ErrorAction SilentlyContinue }
        }

        Write-Host 'Eski App ve varsa eski prefab katalogu geri yuklendi.' -ForegroundColor Yellow
        Write-Host 'Eski Raven.Server yeniden baslatiliyor...' -ForegroundColor Yellow
        $rollbackServer = Start-RavenRuntime
        Wait-RavenHealth -Process $rollbackServer -ExpectedVersion '' -TimeoutSeconds 45
        Write-Host 'Geri donus saglik kontrolu: OK' -ForegroundColor Green
    }
    catch {
        Write-Host ('Geri donus dosyalari yuklendi ancak eski server otomatik baslatilamadi: ' + $_.Exception.Message) -ForegroundColor Red
        Write-Host 'RAVEN_SERVER.bat > 1 ile eski serveri baslatmayi deneyin.' -ForegroundColor Yellow
    }
    exit 1
}
