param(
    [ValidateSet('','ClientInstall','ClientUpdate','ServerUpdate','ServerInstall','SetVersion','Settings','OpenOutput','Verify')]
    [string]$Command = ''
)

$ErrorActionPreference = 'Stop'
$SourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$SystemRoot = [IO.Path]::GetFullPath((Join-Path $SourceRoot '..'))
$ProjectRoot = if ((Split-Path -Leaf $SystemRoot) -eq '.raven') {
    [IO.Path]::GetFullPath((Join-Path $SystemRoot '..'))
} else {
    $SystemRoot
}
$OutputRoot = Join-Path $ProjectRoot 'OUTPUT'
$BuildDir = Join-Path $SourceRoot 'Build'
$BuildConfigPath = Join-Path $BuildDir 'Raven.Build.json'
$PrefabSourceRoot = Join-Path $SourceRoot 'RavenMapPanel.Wpf\Assets\custom_prefabs'
$PrefabMonuments = @(
    'outpost',
    'bandit_camp',
    'stables_a',
    'stables_b',
    'fishing_village_a',
    'fishing_village_b',
    'fishing_village_c'
)

function Title([string]$text) {
    Clear-Host
    Write-Host '================================================================' -ForegroundColor DarkGray
    Write-Host ('  ' + $text) -ForegroundColor Cyan
    Write-Host '================================================================' -ForegroundColor DarkGray
}
function Pause-Raven { Write-Host ''; Read-Host 'Devam etmek icin Enter' | Out-Null }
function Ensure-Dir([string]$p) { if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null } }

function Get-Sha256Hex([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash($stream)
        return (($bytes | ForEach-Object { $_.ToString('x2') }) -join '')
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Get-VersionOutputRoot {
    return (Join-Path $OutputRoot ('v' + (Get-Version)))
}

function Get-ReleaseOutputPath([ValidateSet('CLIENT_INSTALL','CLIENT_UPDATE','SERVER_UPDATE','SERVER_INSTALL_OWNER_ONLY')][string]$Kind) {
    return (Join-Path (Get-VersionOutputRoot) $Kind)
}

function Assert-PathInsideOutput([string]$Path) {
    $root = [IO.Path]::GetFullPath($OutputRoot).TrimEnd([char[]]@([char]92,[char]47)) + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath($Path)
    if (-not $target.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Guvenlik: OUTPUT disindaki bir yol degistirilemez: $target"
    }
}

function Publish-OutputDirectory([string]$Candidate, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Candidate)) { throw "Hazirlanan cikti klasoru bulunamadi: $Candidate" }
    Assert-PathInsideOutput $Destination
    Ensure-Dir (Split-Path -Parent $Destination)

    $backup = $Destination + '.previous-' + [guid]::NewGuid().ToString('N')
    $hadPrevious = Test-Path -LiteralPath $Destination
    try {
        if ($hadPrevious) { Move-Item -LiteralPath $Destination -Destination $backup }
        Move-Item -LiteralPath $Candidate -Destination $Destination
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
    }
    catch {
        if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $Destination)) {
            Move-Item -LiteralPath $backup -Destination $Destination
        }
        throw
    }
}

function Write-ArtifactHash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Hash alinacak dosya bulunamadi: $Path" }
    $hash = Get-Sha256Hex $Path
    [IO.File]::WriteAllText($Path + '.sha256', $hash, [Text.UTF8Encoding]::new($false))
    return $hash
}

function Write-ArtifactInfo([string]$Directory, [string]$Kind, [string]$BuildId, [string]$ApiUrl) {
    [ordered]@{
        product = 'Raven Map Panel'
        kind = $Kind
        version = Get-Version
        buildId = $BuildId
        apiUrl = $ApiUrl
        httpsRequired = $true
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Directory 'artifact-info.json')
}

function Update-ReleaseSummary([string]$Kind, [string]$BuildId) {
    $versionRoot = Get-VersionOutputRoot
    Ensure-Dir $versionRoot
    $summaryPath = Join-Path $versionRoot 'release-summary.json'
    $artifacts = @()
    foreach($dirName in @('CLIENT_INSTALL','CLIENT_UPDATE','SERVER_UPDATE','SERVER_INSTALL_OWNER_ONLY')) {
        $dir = Join-Path $versionRoot $dirName
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $artifactBuildId = ''
        $infoPath = Join-Path $dir 'artifact-info.json'
        if (Test-Path -LiteralPath $infoPath) {
            try { $artifactBuildId = [string](Get-Content -Raw -Encoding UTF8 $infoPath | ConvertFrom-Json).buildId } catch {}
        }
        foreach($file in Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Name -notlike '*.sha256' -and $_.Name -ne 'artifact-info.json' }) {
            $artifacts += [ordered]@{
                kind = $dirName
                buildId = $artifactBuildId
                file = $file.Name
                size = $file.Length
                sha256 = Get-Sha256Hex $file.FullName
            }
        }
    }
    [ordered]@{
        product = 'Raven Map Panel'
        version = Get-Version
        lastOperation = $Kind
        buildId = $BuildId
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        artifacts = $artifacts
    } | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 $summaryPath
}

function Show-OutputResult([string]$Kind) {
    $dir = Get-ReleaseOutputPath $Kind
    Write-Host ''
    Write-Host 'ISLEM BASARILI' -ForegroundColor Green
    Write-Host "Cikti klasoru: $dir" -ForegroundColor Cyan
    if (Test-Path -LiteralPath $dir) {
        Get-ChildItem -LiteralPath $dir -File | ForEach-Object {
            Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor DarkGreen
        }
        if ($env:RAVEN_NO_OPEN -ne '1') { Start-Process explorer.exe $dir }
    }
}

function Assert-ProductionHttps([string]$ApiUrl) {
    if ([string]::IsNullOrWhiteSpace($ApiUrl)) { throw 'HTTPS API adresi ayarlanmamis.' }
    try { $uri = [Uri]$ApiUrl } catch { throw "API adresi gecersiz: $ApiUrl" }
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https') {
        throw "Guvenlik: yayin paketleri yalniz HTTPS kullanabilir. Mevcut adres: $ApiUrl"
    }
    if ([string]::IsNullOrWhiteSpace($uri.Host) -or $uri.IsLoopback) {
        throw "Guvenlik: yayin icin gercek HTTPS domaini gerekli. Mevcut adres: $ApiUrl"
    }
    return $uri.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
}

function Initialize-PrefabSource {
    Ensure-Dir $PrefabSourceRoot

    foreach($tier in @('STANDARD','PREMIUM')) {
        foreach($mon in $PrefabMonuments) {
            Ensure-Dir (Join-Path $PrefabSourceRoot "$tier\$mon")
        }
    }

    # Eski v1.7 yapisini ilk calistirmada yeni klasor sistemine tasir.
    # Bu dosyalar eskiden custom_prefabs kokunde duruyordu ve otomatik STANDARD sayiliyordu.
    $legacy = @(
        @('rustmaps_outpost_template.map',           'STANDARD\outpost\outpost_standard.prefab.map'),
        @('rustmaps_bandit_camp_template.map',       'STANDARD\bandit_camp\bandit_standard.prefab.map'),
        @('rustmaps_stables_a_template.map',         'STANDARD\stables_a\stables_a_standard.prefab.map'),
        @('rustmaps_stables_b_template.map',         'STANDARD\stables_b\stables_b_standard.prefab.map'),
        @('rustmaps_fishing_village_a_template.map', 'STANDARD\fishing_village_a\fishing_village_a_standard.prefab.map'),
        @('rustmaps_fishing_village_b_template.map', 'STANDARD\fishing_village_b\fishing_village_b_standard.prefab.map'),
        @('rustmaps_fishing_village_c_template.map', 'STANDARD\fishing_village_c\fishing_village_c_standard.prefab.map')
    )

    foreach($pair in $legacy) {
        $old = Join-Path $PrefabSourceRoot $pair[0]
        if (-not (Test-Path -LiteralPath $old)) { continue }

        $new = Join-Path $PrefabSourceRoot $pair[1]
        Ensure-Dir (Split-Path -Parent $new)

        if (Test-Path -LiteralPath $new) {
            Write-Host "Prefab gecisi: hedef zaten var, eski dosya dokunulmadi -> $($pair[0])" -ForegroundColor Yellow
            continue
        }

        Move-Item -LiteralPath $old -Destination $new -Force
        Write-Host "Prefab gecisi: $($pair[0]) -> $($pair[1])" -ForegroundColor DarkGreen
    }
}

function Sync-PrefabContent {
    param([Parameter(Mandatory=$true)][string]$DestinationRoot)
    Initialize-PrefabSource

    $contentRoot = [IO.Path]::GetFullPath($DestinationRoot)
    Ensure-Dir $contentRoot

    $standardCount = 0
    $premiumCount = 0

    foreach($tier in @('STANDARD','PREMIUM')) {
        foreach($mon in $PrefabMonuments) {
            $srcDir = Join-Path $PrefabSourceRoot "$tier\$mon"
            $destDir = Join-Path $contentRoot "$tier\$mon"
            Ensure-Dir $srcDir
            Ensure-Dir $destDir

            # Kaynak klasor tek gercek kaynaktir. Paket her seferinde temiz ve gecici
            # bir alanda kurulur; proje icinde ikinci bir SERVER kopyasi tutulmaz.
            Get-ChildItem -LiteralPath $destDir -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name.EndsWith('.map',[StringComparison]::OrdinalIgnoreCase) } |
                Remove-Item -Force

            $files = @(Get-ChildItem -LiteralPath $srcDir -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name.EndsWith('.map',[StringComparison]::OrdinalIgnoreCase) })

            foreach($file in $files) {
                Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destDir $file.Name) -Force
            }

            if ($tier -eq 'STANDARD') { $standardCount += $files.Count } else { $premiumCount += $files.Count }
        }
    }

    Write-Host "Prefab senkronu: STANDARD=$standardCount, PREMIUM=$premiumCount" -ForegroundColor Green
    Write-Host "Kaynak: $PrefabSourceRoot" -ForegroundColor DarkGray
}

function Open-PrefabSource {
    Initialize-PrefabSource
    Start-Process explorer.exe $PrefabSourceRoot
}
function Get-Version {
    [xml]$x = Get-Content -Raw -Encoding UTF8 (Join-Path $SourceRoot 'RavenMapPanel.Wpf\RavenMapPanel.Wpf.csproj')
    $pg = $x.Project.PropertyGroup | Where-Object { $null -ne $_.Version } | Select-Object -First 1
    return [string]$pg.Version
}
function Get-BuildConfig {
    Ensure-Dir $BuildDir
    if (-not (Test-Path -LiteralPath $BuildConfigPath)) {
        $cfg = [ordered]@{
            ClientApiUrl='https://api.ravenrusttr.com.tr'
            LicenseCenterUrl='https://license.ravenrusttr.com.tr'
            StoreUrl='https://www.shopier.com/ravenrust'
            SupportUrl=''
            RustRoot='C:\RavenMapServer\RustServer'
        }
        $cfg | ConvertTo-Json | Set-Content -Encoding UTF8 $BuildConfigPath
    }
    $cfg = Get-Content -Raw -Encoding UTF8 $BuildConfigPath | ConvertFrom-Json
    $changed = $false
    foreach($entry in @(
        @('LicenseCenterUrl','https://license.ravenrusttr.com.tr'),
        @('StoreUrl','https://www.shopier.com/ravenrust'),
        @('SupportUrl','')
    )) {
        if ($null -eq $cfg.PSObject.Properties[$entry[0]]) {
            $cfg | Add-Member -NotePropertyName $entry[0] -NotePropertyValue $entry[1]
            $changed = $true
        }
    }
    if ($changed) { Save-BuildConfig $cfg }
    return $cfg
}
function Save-BuildConfig($cfg) { Ensure-Dir $BuildDir; $cfg | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $BuildConfigPath }
function Ensure-DotNet {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) { throw '.NET SDK bulunamadi. .NET 10 SDK kurulu olmali.' }
    $v = (& dotnet --version).Trim()
    if (-not $v.StartsWith('10.')) { throw "Proje .NET 10 istiyor. Bulunan: $v" }
    Write-Host ".NET SDK: $v" -ForegroundColor DarkGray
}
function Verify-Source {
    $secretCheck = Join-Path $SourceRoot 'Raven.ReleaseTool\Internal\verify_source_secrets.ps1'
    if (Test-Path $secretCheck) { & powershell -NoProfile -ExecutionPolicy Bypass -File $secretCheck -Path $SourceRoot; if ($LASTEXITCODE -ne 0) { throw 'Kaynak secret kontrolu basarisiz.' } }
    $xaml = Get-Content -Raw -Encoding UTF8 (Join-Path $SourceRoot 'RavenMapPanel.Wpf\MainWindow.xaml')
    $appXaml = Get-Content -Raw -Encoding UTF8 (Join-Path $SourceRoot 'RavenMapPanel.Wpf\App.xaml')
    $util = Get-Content -Raw -Encoding UTF8 (Join-Path $SourceRoot 'RavenMapPanel.Wpf\MainWindow.Utility.cs')
    $admin = Get-Content -Raw -Encoding UTF8 (Join-Path $SourceRoot 'Raven.Server\AdminPages.cs')
    $models = Get-Content -Raw -Encoding UTF8 (Join-Path $SourceRoot 'RavenMapPanel.Core\Models.cs')
    $clientVersion = Get-Version
    [xml]$serverProject = Get-Content -Raw -Encoding UTF8 (Join-Path $SourceRoot 'Raven.Server\Raven.Server.csproj')
    $serverVersionGroup = $serverProject.Project.PropertyGroup | Where-Object { $null -ne $_.Version } | Select-Object -First 1
    $serverSourceVersion = [string]$serverVersionGroup.Version
    if ($serverSourceVersion -ne $clientVersion) { throw "Client/Server surumleri uyusmuyor. Client=$clientVersion Server=$serverSourceVersion" }
    if ($xaml -match 'RAVEN DUYURU') { throw 'Eski RAVEN DUYURU rozeti kaynakta bulundu.' }
    if ($appXaml -match 'StartupUri\s*=') { throw 'App.xaml StartupUri kullanamaz. MainWindow lisans dogrulamasindan sonra App.xaml.cs tarafindan manuel acilir.' }

    # App.xaml icindeki bir ResourceDictionary kaynak dosyasi eksikse WPF publish basarili
    # olabilir ancak EXE daha OnStartup calismadan sessizce coker. Bunu release oncesi yakala.
    foreach ($resource in @('Resources\Styles.xaml','Resources\Converters.xaml')) {
        $resourcePath = Join-Path (Join-Path $SourceRoot 'RavenMapPanel.Wpf') $resource
        if (-not (Test-Path -LiteralPath $resourcePath)) { throw "WPF runtime resource eksik: RavenMapPanel.Wpf\$resource" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot 'RavenMapPanel.Wpf\Program.cs'))) {
        throw 'Client startup crash logger eksik: RavenMapPanel.Wpf\Program.cs'
    }

    # UI metinlerine bagimli kontrol YAPMA. Metin/encoding degisikligi build'i
    # gereksiz yere bozmamali; gercek uretim/dogrulama kod yapisini kontrol et.
    if ($util -notmatch 'UpdateProductionSummary\s*\(' -or
        $util -notmatch 'CurrentValidationStatusDetail\s*\(' -or
        $util -notmatch 'MapValidationService\.Evaluate') {
        throw 'Harita uretim/dogrulama kod yapisi bulunamadi.'
    }

    # Admin UI metnini degistirmek build'i bozmamali. Form alanlarini kontrol et.
    if ($admin -notmatch "name='rememberMe'" -or $admin -notmatch "announcementImageUrl") {
        throw 'Admin hatirla/duyuru alanlari bulunamadi.'
    }

    if ($models -notmatch 'IconSize\s*\{\s*get;\s*set;\s*\}\s*=\s*72') { throw '72px ikon varsayilani bulunamadi.' }

    # Monument ikon klasoru yalnizca aktif config tarafindan kullanilan dosyalari
    # icermeli. Ad degisikliginden sonra kalan eski kopyalar client paketini
    # gereksiz buyutur ve hangi ikonun gercekten kullanildigini belirsizlestirir.
    $monumentConfigPath = Join-Path $SourceRoot 'RavenMapPanel.Wpf\Assets\config\monuments.json'
    $monumentIconRoot = Join-Path $SourceRoot 'RavenMapPanel.Wpf\Assets\icons'
    if (-not (Test-Path -LiteralPath $monumentConfigPath)) { throw 'Monument config dosyasi eksik: Assets\config\monuments.json' }
    if (-not (Test-Path -LiteralPath $monumentIconRoot)) { throw 'Monument ikon klasoru eksik: Assets\icons' }

    $monumentConfig = Get-Content -Raw -Encoding UTF8 $monumentConfigPath | ConvertFrom-Json
    $monuments = @($monumentConfig.monuments)
    $withoutIcon = @($monuments | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.icon) } | ForEach-Object { [string]$_.id })
    if ($withoutIcon.Count -gt 0) {
        throw "Ikon dosyasi belirtilmeyen monumentler: $($withoutIcon -join ', ')"
    }

    $referencedIcons = @($monuments | ForEach-Object { [string]$_.icon } | Sort-Object -Unique)
    $existingIcons = @(Get-ChildItem -LiteralPath $monumentIconRoot -File -Filter '*.png' | ForEach-Object { $_.Name } | Sort-Object -Unique)
    $missingIcons = @($referencedIcons | Where-Object { $existingIcons -notcontains $_ })
    $unusedIcons = @($existingIcons | Where-Object { $referencedIcons -notcontains $_ })
    if ($missingIcons.Count -gt 0) {
        throw "Config tarafindan kullanilan ikon dosyalari eksik: $($missingIcons -join ', ')"
    }
    if ($unusedIcons.Count -gt 0) {
        throw "Kullanilmayan/eski ikon dosyalari bulundu: $($unusedIcons -join ', ')"
    }
    Write-Host ("Monument ikonlari: {0} aktif / {1} dosya" -f $referencedIcons.Count, $existingIcons.Count) -ForegroundColor DarkGreen

    # Yeni baseline: normal owner ayarlari Admin Panel'den, kullanici sorunlari tek katalogdan yonetilir.
    foreach ($required in @(
        'Raven.Server\OwnerSettingsService.cs',
        'Raven.Server\UserIssueCatalog.cs',
        'Tools\SetupVdsAddress.ps1')) {
        if (-not (Test-Path (Join-Path $SourceRoot $required))) { throw "Yeni Raven baseline dosyasi eksik: $required" }
    }
    if ($admin -notmatch '/admin/settings/business' -and $admin -notmatch '/settings/business') { throw 'Admin merkezi sistem ayarlari endpoint/formu bulunamadi.' }
    Write-Host 'Kaynak kontrolu: OK' -ForegroundColor Green
}
function New-BuildStamp {
    $id = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    $utc = (Get-Date).ToUniversalTime().ToString('o')
    $client = @"
namespace RavenMapPanel;

internal static class BuildStamp
{
    public const string Id = "$id";
    public const string BuiltUtc = "$utc";
}
"@
    $server = @"
internal static class BuildStamp
{
    public const string Id = "$id";
    public const string BuiltUtc = "$utc";
}
"@
    Set-Content -Encoding UTF8 (Join-Path $SourceRoot 'RavenMapPanel.Wpf\BuildStamp.cs') $client
    Set-Content -Encoding UTF8 (Join-Path $SourceRoot 'Raven.Server\BuildStamp.cs') $server
    return $id
}
function Resolve-OwnerPrivateKey([string]$fileName) {
    $ownerRoot = Join-Path $SystemRoot 'OWNER_SECRETS'
    $candidates = @(
        (Join-Path $ownerRoot $fileName),
        (Join-Path $ownerRoot (Join-Path 'CURRENT' $fileName)),
        (Join-Path $ownerRoot (Join-Path 'CURRENT_NEW' $fileName))
    )
    foreach($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return [IO.Path]::GetFullPath($candidate) }
    }
    if (Test-Path -LiteralPath $ownerRoot) {
        $found = Get-ChildItem -LiteralPath $ownerRoot -Filter $fileName -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/]KeyBackups[\\/]' -and $_.FullName -notmatch '[\\/]MIGRATION_OLD[\\/]' } |
            Select-Object -First 1
        if ($null -ne $found) { return $found.FullName }
    }
    throw "Owner anahtari bulunamadi: OWNER_SECRETS\$fileName`nImzali SERVER/client update veya ilk VDS OWNER paketi icin gerekli owner anahtarini OWNER_SECRETS klasorune geri yukle."
}
function Resolve-LicenseKey {
    return (Resolve-OwnerPrivateKey 'license_private.pem')
}
function Resolve-UpdateKey {
    return (Resolve-OwnerPrivateKey 'update_private.pem')
}
function Resolve-OldUpdateKey {
    $p = Join-Path $SystemRoot 'OWNER_SECRETS\MIGRATION_OLD\update_private.pem'
    if (Test-Path -LiteralPath $p) { return $p }
    throw 'OWNER_SECRETS\MIGRATION_OLD\update_private.pem bulunamadi.'
}
function Show-OwnerKeyStatus {
    Title 'RAVEN OWNER ANAHTAR DURUMU'
    Write-Host 'Bu anahtarlar normal Server Update buildinde KULLANILMAZ.' -ForegroundColor Green
    Write-Host 'Yalniz ilk VDS FULL OWNER paketi ve imzali client update icin gerekir.' -ForegroundColor DarkGray
    Write-Host ''
    foreach($name in @('license_private.pem','update_private.pem')) {
        try {
            $p = Resolve-OwnerPrivateKey $name
            Write-Host ("OK  {0}" -f $name) -ForegroundColor Green
            Write-Host ("    {0}" -f $p) -ForegroundColor DarkGray
        } catch {
            Write-Host ("YOK {0}" -f $name) -ForegroundColor Yellow
        }
    }
    Write-Host ''
    Write-Host 'Tercih edilen sade konum:' -ForegroundColor Cyan
    Write-Host '  OWNER_SECRETS\license_private.pem'
    Write-Host '  OWNER_SECRETS\update_private.pem'
    Write-Host 'Eski CURRENT_NEW yapisi da geriye donuk uyumluluk icin otomatik bulunur.' -ForegroundColor DarkGray
}
function Ensure-Harmony {
    param([switch]$Force)
    $cfg = Get-BuildConfig
    $dll = Join-Path $SourceRoot 'RavenMapPanel.Wpf\Assets\HarmonyMods\CustomGenerator.dll'
    $src = Join-Path $SourceRoot 'HarmonyCustomGenerator-main\CustomGenerator'
    $need = $Force -or -not (Test-Path $dll)
    if (-not $need) {
        $dllTime = (Get-Item $dll).LastWriteTimeUtc
        $newer = Get-ChildItem -LiteralPath $src -Recurse -File -Filter *.cs | Where-Object { $_.LastWriteTimeUtc -gt $dllTime } | Select-Object -First 1
        $need = $null -ne $newer
    }
    if (-not $need) { Write-Host 'Harmony: guncel' -ForegroundColor DarkGreen; return }
    $rust = [string]$cfg.RustRoot
    if (-not (Test-Path (Join-Path $rust 'RustDedicated_Data\Managed\Assembly-CSharp.dll'))) {
        Write-Host "Kayitli Rust yolu gecersiz: $rust" -ForegroundColor Yellow
        $rust = Read-Host 'RustDedicated ana klasoru'
        if (-not (Test-Path (Join-Path $rust 'RustDedicated_Data\Managed\Assembly-CSharp.dll'))) { throw 'Rust Managed klasoru bulunamadi.' }
        $cfg.RustRoot = $rust; Save-BuildConfig $cfg
    }
    Write-Host 'Harmony yeniden derleniyor...' -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $SourceRoot 'HarmonyCustomGenerator-main\DERLE_CustomGenerator.ps1') -RustRoot $rust
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dll)) { throw 'Harmony build basarisiz.' }
    Write-Host 'Harmony: OK' -ForegroundColor Green
}
function Write-ClientConfig([string]$dir, $cfg) {
    $api = ([string]$cfg.ClientApiUrl).TrimEnd('/')
    if ([string]::IsNullOrWhiteSpace($api)) { $api = 'https://api.ravenrusttr.com.tr' }
    $licenseCenter = Assert-ProductionHttps (([string]$cfg.LicenseCenterUrl).TrimEnd('/'))
    Ensure-Dir (Join-Path $dir 'Config')
    [ordered]@{
        BaseUrl=$api
        LicenseCenterUrl=$licenseCenter
        LicenseProduct='raven_map'
        StoreUrl=([string]$cfg.StoreUrl).Trim()
        SupportUrl=([string]$cfg.SupportUrl).Trim()
        AllowInsecureHttpForTesting=$false
    } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $dir 'Config\raven_server.json')
}
function Test-RavenApi([string]$api) {
    try {
        $base = $api.TrimEnd('/')
        $h = Invoke-RestMethod ($base + '/health') -TimeoutSec 6
        if (-not [string]::IsNullOrWhiteSpace([string]$h.authBaseUrl) -and ([string]$h.authBaseUrl).TrimEnd('/') -ne $base) {
            throw "Steam/auth adresi uyusmuyor. Client=$base ServerAuth=$($h.authBaseUrl)"
        }
        Write-Host "API OK: v$($h.version) · Build $($h.buildId) · $($h.environment)" -ForegroundColor Green
        return $true
    } catch { Write-Host "API HATA: $($_.Exception.Message)" -ForegroundColor Red; return $false }
}
function Build-Client {
    param([switch]$Sales, [string]$BuildId)
    if (-not $Sales) { throw 'Yerel CLIENT kopyasi kaldirildi. Musteri paketi icin Build-Client -Sales kullanin.' }
    Ensure-DotNet; Verify-Source; Ensure-Harmony
    Invoke-CoreTests
    if ([string]::IsNullOrWhiteSpace($BuildId)) { $BuildId = New-BuildStamp }
    $cfg = Get-BuildConfig
    $api = ([string]$cfg.ClientApiUrl).TrimEnd('/')
    if ([string]::IsNullOrWhiteSpace($api)) { $api = 'https://api.ravenrusttr.com.tr' }
    $licenseCenter = Assert-ProductionHttps (([string]$cfg.LicenseCenterUrl).TrimEnd('/'))
    Write-Host "Lisans merkezi: $licenseCenter" -ForegroundColor DarkGray
    Write-Host 'Raven Map lisansi bu PHP merkezinden kontrol edilir; ayri VDS/Raven.Server gerekmez.' -ForegroundColor DarkGray

    $project = Join-Path $SourceRoot 'RavenMapPanel.Wpf\RavenMapPanel.Wpf.csproj'
    $stage = Join-Path $env:TEMP ('RavenClientInstall_' + [guid]::NewGuid().ToString('N'))
    $candidate = $null
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    Ensure-Dir $stage
    try {
        Write-Host 'Client publish...' -ForegroundColor Cyan
        & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $stage
        if ($LASTEXITCODE -ne 0) { throw 'Client publish basarisiz.' }
        if (-not (Test-Path (Join-Path $stage 'RavenMapPanel.exe'))) { throw 'RavenMapPanel.exe olusmadi.' }
        $verifySales = Join-Path $SourceRoot 'Raven.ReleaseTool\Internal\verify_sales_output.ps1'
        if (Test-Path $verifySales) { & powershell -NoProfile -ExecutionPolicy Bypass -File $verifySales -Path $stage; if ($LASTEXITCODE -ne 0) { throw 'Client paket guvenlik kontrolu basarisiz.' } }

        Write-ClientConfig $stage $cfg

        $ver = Get-Version
        $candidate = Join-Path $env:TEMP ('RavenClientInstallOutput_' + [guid]::NewGuid().ToString('N'))
        Ensure-Dir $candidate
        $zip = Join-Path $candidate "RavenMapPanel_Client_v$ver.zip"
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
        [void](Write-ArtifactHash $zip)
        Write-ArtifactInfo -Directory $candidate -Kind 'CLIENT_INSTALL' -BuildId $BuildId -ApiUrl $licenseCenter
        Publish-OutputDirectory -Candidate $candidate -Destination (Get-ReleaseOutputPath 'CLIENT_INSTALL')
        $candidate = $null
        Update-ReleaseSummary -Kind 'CLIENT_INSTALL' -BuildId $BuildId
        Show-OutputResult 'CLIENT_INSTALL'
    }
    finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        if (-not [string]::IsNullOrWhiteSpace($candidate)) { Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Write-Host "Build: $BuildId" -ForegroundColor DarkGray
}
function Invoke-ClientCompileSmoke {
    Ensure-DotNet
    $project = Join-Path $SourceRoot 'RavenMapPanel.Wpf\RavenMapPanel.Wpf.csproj'
    $stage = Join-Path $env:TEMP ('RavenClientCompileSmoke_' + [guid]::NewGuid().ToString('N'))
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    Ensure-Dir $stage
    try {
        Write-Host 'Client compile/publish smoke testi...' -ForegroundColor Cyan
        & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $stage
        if ($LASTEXITCODE -ne 0) {
            throw 'Client compile/publish smoke testi basarisiz. Ilk VDS paketi olusturulmadi.'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $stage 'RavenMapPanel.exe'))) {
            throw 'Client smoke publish RavenMapPanel.exe uretmedi. Ilk VDS paketi olusturulmadi.'
        }
        Write-Host 'Client compile/publish smoke: OK' -ForegroundColor Green
    }
    finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function New-CaddyConfig {
    param(
        [Parameter(Mandatory=$true)][string]$Destination,
        [Parameter(Mandatory=$true)][string]$ApiUrl
    )

    $api = Assert-ProductionHttps $ApiUrl
    $uri = [Uri]$api
    if ([string]::IsNullOrWhiteSpace($uri.Host)) { throw "Caddy domaini belirlenemedi: $ApiUrl" }

    $text = @"
$($uri.Host) {
    reverse_proxy 127.0.0.1:5088
}
"@
    [IO.File]::WriteAllText($Destination, $text, [Text.UTF8Encoding]::new($false))
    Write-Host "Caddyfile: $($uri.Host) -> 127.0.0.1:5088" -ForegroundColor Green
}

function New-FirstVdsConfig {
    param([Parameter(Mandatory=$true)][string]$Destination)

    # ILK VDS FULL paketi hicbir zaman workspace SERVER\Config'teki eski/stale
    # dosyayi tasimaz. Her seferinde temiz source template'ten yeni owner config
    # uretilir. Shopier/urun/destek ayarlari daha sonra Admin Panel'den yapilir.
    $template = Join-Path $SourceRoot 'Raven.Server\raven_server.json'
    if (-not (Test-Path -LiteralPath $template)) { throw "Temiz server config template bulunamadi: $template" }

    try {
        $cfg = Get-Content -Raw -Encoding UTF8 $template | ConvertFrom-Json
    } catch {
        throw "Kaynak raven_server.json gecersiz JSON: $($_.Exception.Message)"
    }

    $buildCfg = Get-BuildConfig
    $api = ([string]$buildCfg.ClientApiUrl).Trim()
    if ([string]::IsNullOrWhiteSpace($api)) { $api = 'http://127.0.0.1:5088' }
    try { $uri = [Uri]$api } catch { throw "Build API adresi gecersiz: $api" }
    if ($uri.Scheme -notin @('http','https')) { throw "Build API adresi http/https olmali: $api" }

    $cfg.Raven.PublicBaseUrl = $api.TrimEnd('/')
    $cfg.Raven.ListenUrl = 'http://0.0.0.0:5088'
    $cfg.Raven.Environment = if ($uri.Scheme -eq 'https') { 'Production' } else { 'Development' }

    # Ilk kurulum temiz baslar. Hassas PAT veya eski isletme ayarlari FULL pakete
    # yanlislikla tasinmaz; Admin Panel ilk acilistan sonra bunlari yonetir.
    $cfg.Shopier.Enabled = $false
    $cfg.Shopier.ApiKey = ''

    Ensure-Dir (Split-Path -Parent $Destination)
    $cfg | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $Destination

    # ZIP olusmadan once gercek parse testi. Bozuk JSON VDS'ye asla gitmesin.
    try {
        $roundTrip = Get-Content -Raw -Encoding UTF8 $Destination | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace([string]$roundTrip.Raven.PublicBaseUrl)) {
            throw 'Raven.PublicBaseUrl bos.'
        }
    } catch {
        throw "Olusturulan ilk VDS config JSON dogrulamasi basarisiz: $($_.Exception.Message)"
    }

    Write-Host "Ilk VDS temiz config: $Destination" -ForegroundColor Green
    Write-Host "Public API: $($cfg.Raven.PublicBaseUrl)" -ForegroundColor DarkGray
}
function New-ServerBuildStage {
    param([string]$BuildId)
    # Bu fonksiyonun success stream'inde yalnizca gecici paket yolu kalmali.
    # Aksi halde alt kontrollerin/metinlerin ciktisi, cagiran tarafta yolun bir
    # diziye donusmesine ve Join-Path hatalarina neden olur.
    Ensure-DotNet | Out-Host
    Verify-Source | Out-Host
    if ([string]::IsNullOrWhiteSpace($BuildId)) { $BuildId = New-BuildStamp }
    $root = Join-Path $env:TEMP ('RavenServerBuild_' + [guid]::NewGuid().ToString('N'))
    $app = Join-Path $root 'App'
    Ensure-Dir $app
    Ensure-Dir (Join-Path $root 'Tools')
    try {
        Write-Host 'Server publish...' -ForegroundColor Cyan
        & dotnet publish (Join-Path $SourceRoot 'Raven.Server\Raven.Server.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $app | Out-Host
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $app 'Raven.Server.exe'))) { throw 'Server publish basarisiz.' }
        Sync-PrefabContent -DestinationRoot (Join-Path $root 'Content\Prefabs') | Out-Host
        Copy-Item (Join-Path $SourceRoot 'ServerTools\RAVEN_SERVER.bat') (Join-Path $root 'RAVEN_SERVER.bat') -Force
        Copy-Item (Join-Path $SourceRoot 'ServerTools\KUR_RAVEN_SERVER.bat') (Join-Path $root 'KUR_RAVEN_SERVER.bat') -Force
        Copy-Item (Join-Path $SourceRoot 'ServerTools\KALDIR_RAVEN_SERVER.bat') (Join-Path $root 'KALDIR_RAVEN_SERVER.bat') -Force
        Copy-Item (Join-Path $SourceRoot 'ServerTools\CLOUDFLARE_DOMAIN_REHBERI.txt') (Join-Path $root 'CLOUDFLARE_DOMAIN_REHBERI.txt') -Force
        Copy-Item (Join-Path $SourceRoot 'ServerTools\SetupServerSecrets.ps1') (Join-Path $root 'Tools\SetupServerSecrets.ps1') -Force
        Copy-Item (Join-Path $SourceRoot 'ServerTools\ApplyServerUpdate.ps1') (Join-Path $root 'Tools\ApplyServerUpdate.ps1') -Force
        Copy-Item (Join-Path $SourceRoot 'ServerTools\ShopierConfigTest.ps1') (Join-Path $root 'Tools\ShopierConfigTest.ps1') -Force
        Copy-Item (Join-Path $SourceRoot 'ServerTools\Install-RavenServer.ps1') (Join-Path $root 'Tools\Install-RavenServer.ps1') -Force
        Copy-Item (Join-Path $SourceRoot 'ServerTools\Uninstall-RavenServer.ps1') (Join-Path $root 'Tools\Uninstall-RavenServer.ps1') -Force
        Copy-Item (Join-Path $SourceRoot 'ServerTools\Raven-HealthMonitor.ps1') (Join-Path $root 'Tools\Raven-HealthMonitor.ps1') -Force
        return $root
    }
    catch {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
}
function Build-Server {
    param([string]$BuildId)
    Ensure-DotNet; Verify-Source
    Invoke-ServerTests
    $api = Assert-ProductionHttps (([string](Get-BuildConfig).ClientApiUrl).TrimEnd('/'))
    Write-Host "Paket API: $api" -ForegroundColor DarkGray
    Write-Host 'Canli API erisimi server buildini engellemez.' -ForegroundColor DarkGray
    if ([string]::IsNullOrWhiteSpace($BuildId)) { $BuildId = New-BuildStamp }
    $ver = Get-Version
    $serverStage = $null
    $updateStage = Join-Path $env:TEMP ('RavenServerUpdate_' + [guid]::NewGuid().ToString('N'))
    $candidate = $null
    try {
        $serverStage = New-ServerBuildStage -BuildId $BuildId
        Ensure-Dir (Join-Path $updateStage 'Update')
        foreach($name in @('App','Content','Tools','RAVEN_SERVER.bat','KUR_RAVEN_SERVER.bat','KALDIR_RAVEN_SERVER.bat','CLOUDFLARE_DOMAIN_REHBERI.txt')) {
            Copy-Item (Join-Path $serverStage $name) (Join-Path $updateStage 'Update') -Recurse -Force
        }
        # Imzali manifest: paket icerigi kayit altina alinir; ardindan ZIP'in tamami RSA ile imzalanir.
        $manifestRoot = [IO.Path]::GetFullPath((Join-Path $updateStage 'Update'))
        $manifestFiles = @(Get-ChildItem -LiteralPath $manifestRoot -Recurse -File | ForEach-Object {
            $relativePath = $_.FullName.Substring($manifestRoot.Length).TrimStart([char[]]@([char]92,[char]47)).Replace('\','/')
            [ordered]@{ path=$relativePath; size=$_.Length; sha256=Get-Sha256Hex $_.FullName }
        })
        [ordered]@{ product='Raven.Server'; version=$ver; buildId=$BuildId; createdUtc=[DateTimeOffset]::UtcNow.ToString('O'); files=$manifestFiles } |
            ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $updateStage 'Update\server-update-manifest.json')

        $updateKey = Resolve-UpdateKey
        $candidate = Join-Path $env:TEMP ('RavenServerUpdateOutput_' + [guid]::NewGuid().ToString('N'))
        Ensure-Dir $candidate
        $updateZip = Join-Path $candidate "RavenServer_Update_v$ver.zip"
        Compress-Archive -Path (Join-Path $updateStage '*') -DestinationPath $updateZip -Force
        & dotnet run --project (Join-Path $SourceRoot 'Raven.ReleaseTool\Raven.ReleaseTool.csproj') -c Release -- --sign-file $updateZip --key $updateKey
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path ($updateZip + '.sig')) -or -not (Test-Path ($updateZip + '.sha256'))) {
            throw 'Server update RSA imzasi olusturulamadi.'
        }
        Write-ArtifactInfo -Directory $candidate -Kind 'SERVER_UPDATE' -BuildId $BuildId -ApiUrl $api
        Publish-OutputDirectory -Candidate $candidate -Destination (Get-ReleaseOutputPath 'SERVER_UPDATE')
        $candidate = $null
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($serverStage)) { Remove-Item -LiteralPath $serverStage -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $updateStage -Recurse -Force -ErrorAction SilentlyContinue
        if (-not [string]::IsNullOrWhiteSpace($candidate)) { Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue }
    }

    Update-ReleaseSummary -Kind 'SERVER_UPDATE' -BuildId $BuildId
    Write-Host 'VDS Config/Data/Secrets korunur. Update paketi uygulamadan once RSA-SHA256 ile dogrulanir.' -ForegroundColor DarkGreen
    Write-Host "Build: $BuildId" -ForegroundColor DarkGray
    Show-OutputResult 'SERVER_UPDATE'
}

function Invoke-ServerTests {
    Ensure-DotNet
    $testProject = Join-Path $SourceRoot 'Raven.Server.Tests\Raven.Server.Tests.csproj'
    if (-not (Test-Path -LiteralPath $testProject)) { throw "Server test projesi bulunamadi: $testProject" }
    Write-Host 'Server testleri...' -ForegroundColor Cyan
    & dotnet test $testProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Raven.Server.Tests basarisiz. Ilk VDS paketi olusturulmadi.' }
    Write-Host 'Server testleri: OK' -ForegroundColor Green
}

function Invoke-ServerCompileSmoke {
    $stage = $null
    try {
        Write-Host 'Server gecici paketleme smoke testi...' -ForegroundColor Cyan
        $stage = New-ServerBuildStage -BuildId 'VERIFY'
        foreach($required in @(
            'App\Raven.Server.exe',
            'RAVEN_SERVER.bat',
            'KUR_RAVEN_SERVER.bat',
            'KALDIR_RAVEN_SERVER.bat',
            'CLOUDFLARE_DOMAIN_REHBERI.txt',
            'Tools\SetupServerSecrets.ps1',
            'Tools\ApplyServerUpdate.ps1',
            'Tools\ShopierConfigTest.ps1',
            'Tools\Install-RavenServer.ps1',
            'Tools\Uninstall-RavenServer.ps1',
            'Tools\Raven-HealthMonitor.ps1',
            'Content\Prefabs'
        )) {
            if (-not (Test-Path -LiteralPath (Join-Path $stage $required))) {
                throw "Server gecici paketi zorunlu yolu icermiyor: $required"
            }
        }
        foreach($forbidden in @('Config','Data','Secrets','Updates')) {
            if (Test-Path -LiteralPath (Join-Path $stage $forbidden)) {
                throw "Server update gecici paketine yasakli klasor girdi: $forbidden"
            }
        }
        Write-Host 'Server gecici paketleme smoke testi: OK' -ForegroundColor Green
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($stage)) {
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-CoreTests {
    Ensure-DotNet
    $testProject = Join-Path $SourceRoot 'RavenMapPanel.Core.Tests\RavenMapPanel.Core.Tests.csproj'
    if (-not (Test-Path -LiteralPath $testProject)) { throw "Core test projesi bulunamadi: $testProject" }
    Write-Host 'Client/Core testleri...' -ForegroundColor Cyan
    & dotnet test $testProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'RavenMapPanel.Core.Tests basarisiz. Paket olusturulmadi.' }
    Write-Host 'Client/Core testleri: OK' -ForegroundColor Green
}

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Test-FullOwnerPackage {
    param([Parameter(Mandatory=$true)][string]$ZipPath)

    if (-not (Test-Path -LiteralPath $ZipPath)) { throw "Preflight ZIP bulunamadi: $ZipPath" }
    $testRoot = Join-Path $env:TEMP ('RavenFullPreflight_' + [guid]::NewGuid().ToString('N'))
    Ensure-Dir $testRoot
    $process = $null
    try {
        Write-Host 'Ilk VDS paket preflight testi...' -ForegroundColor Cyan
        Expand-Archive -LiteralPath $ZipPath -DestinationPath $testRoot -Force

        $required = @(
            'App\Raven.Server.exe',
            'Config\raven_server.json',
            'Caddyfile',
            'Secrets\license_private.pem',
            'Secrets\update_public.pem',
            'RAVEN_SERVER.bat',
            'KUR_RAVEN_SERVER.bat',
            'KALDIR_RAVEN_SERVER.bat',
            'CLOUDFLARE_DOMAIN_REHBERI.txt',
            'Tools\SetupServerSecrets.ps1',
            'Tools\ApplyServerUpdate.ps1',
            'Tools\Install-RavenServer.ps1',
            'Tools\Uninstall-RavenServer.ps1',
            'Tools\Raven-HealthMonitor.ps1'
        )
        foreach($rel in $required) {
            $full = Join-Path $testRoot $rel
            if (-not (Test-Path -LiteralPath $full)) { throw "Ilk VDS paketinde zorunlu dosya eksik: $rel" }
        }

        $configPath = Join-Path $testRoot 'Config\raven_server.json'
        try { $cfg = Get-Content -Raw -Encoding UTF8 $configPath | ConvertFrom-Json }
        catch { throw "Ilk VDS paketindeki config gecersiz JSON: $($_.Exception.Message)" }

        $publicUrl = ([string]$cfg.Raven.PublicBaseUrl).Trim()
        if ([string]::IsNullOrWhiteSpace($publicUrl) -or $publicUrl -match 'VDS_IP_ADRESIN') {
            throw "Ilk VDS PublicBaseUrl gecersiz: $publicUrl"
        }
        try { $u = [Uri]$publicUrl } catch { throw "Ilk VDS PublicBaseUrl parse edilemiyor: $publicUrl" }
        if ($u.Scheme -ne 'https') { throw "Ilk VDS PublicBaseUrl mutlaka HTTPS olmali: $publicUrl" }

        $caddyPath = Join-Path $testRoot 'Caddyfile'
        $caddyText = Get-Content -Raw -Encoding UTF8 $caddyPath
        $expectedHost = ([Uri]$publicUrl).Host
        if ($caddyText -notmatch [Regex]::Escape($expectedHost) -or $caddyText -notmatch '127\.0\.0\.1:5088') {
            throw "Ilk VDS Caddyfile beklenen route'u icermiyor: $expectedHost -> 127.0.0.1:5088"
        }

        $privateKeyText = Get-Content -Raw -Encoding ASCII (Join-Path $testRoot 'Secrets\license_private.pem')
        if ($privateKeyText -notmatch '-----BEGIN (RSA )?PRIVATE KEY-----') { throw 'Ilk VDS license_private.pem PEM formatinda degil.' }
        $publicKeyText = Get-Content -Raw -Encoding ASCII (Join-Path $testRoot 'Secrets\update_public.pem')
        if ($publicKeyText -notmatch '-----BEGIN (RSA )?PUBLIC KEY-----') { throw 'Ilk VDS update_public.pem PEM formatinda degil.' }

        # Gercek EXE'yi gecici local config ile ayağa kaldirip /health smoke testi yap.
        # ZIP'in kendisi degismez; yalniz TEMP'e acilan kopya test edilir.
        $port = Get-FreeTcpPort
        $local = "http://127.0.0.1:$port"
        $cfg.Raven.PublicBaseUrl = $local
        $cfg.Raven.ListenUrl = $local
        $cfg.Raven.Environment = 'Development'
        $cfg.Shopier.Enabled = $false
        $cfg.Shopier.ApiKey = ''
        $cfg | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $configPath

        $exe = Join-Path $testRoot 'App\Raven.Server.exe'
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.WorkingDirectory = (Split-Path -Parent $exe)
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.EnvironmentVariables['RAVEN_SERVER_ROOT'] = $testRoot
        $psi.EnvironmentVariables['RAVEN_CONFIG_FILE'] = $configPath
        $psi.EnvironmentVariables['RAVEN_ADMIN_PASSWORD'] = 'RavenPreflight!123456789'
        $psi.EnvironmentVariables['RAVEN_ADMIN_TOTP_SECRET'] = 'JBSWY3DPEHPK3PXP'
        $psi.EnvironmentVariables['RAVEN_NONINTERACTIVE'] = '1'
        $process = [Diagnostics.Process]::Start($psi)

        $deadline = (Get-Date).AddSeconds(25)
        $healthOk = $false
        while((Get-Date) -lt $deadline) {
            if ($process.HasExited) { break }
            try {
                $h = Invoke-RestMethod ($local + '/health') -TimeoutSec 2
                if ($null -ne $h -and -not [string]::IsNullOrWhiteSpace([string]$h.version)) {
                    $healthOk = $true
                    break
                }
            } catch {}
            Start-Sleep -Milliseconds 500
        }

        if (-not $healthOk) {
            if ($null -ne $process -and -not $process.HasExited) {
                try { $process.Kill() } catch {}
                try { $process.WaitForExit(3000) | Out-Null } catch {}
            }
            $stdout = ''
            $stderr = ''
            try { $stdout = $process.StandardOutput.ReadToEnd() } catch {}
            try { $stderr = $process.StandardError.ReadToEnd() } catch {}
            $detail = (($stderr + "`n" + $stdout).Trim())
            if ($detail.Length -gt 1800) { $detail = $detail.Substring(0,1800) + '...' }
            throw "Ilk VDS runtime smoke testi basarisiz. Paket VDS'ye GONDERILMEMELI.`n$detail"
        }

        Write-Host "Paket JSON: OK · Dosyalar: OK · Raven.Server runtime /health: OK ($local)" -ForegroundColor Green
        Write-Host 'ILK VDS PAKETI PREFLIGHT: BASARILI' -ForegroundColor Green
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) {
            try { $process.Kill() } catch {}
            try { $process.WaitForExit(3000) | Out-Null } catch {}
        }
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-FirstInstallWizard {
    Title 'RAVEN ILK VDS KURULUM SIHIRBAZI'
    Write-Host 'Bu akis VDSye dosya gondermeden ONCE yerelde her seyi dogrular.' -ForegroundColor Cyan
    Write-Host 'Preflight gecmezse FULL ZIP teslim edilmez.' -ForegroundColor DarkGray
    Write-Host ''

    Ensure-DotNet
    $cfg = Get-BuildConfig
    $currentApi = ([string]$cfg.ClientApiUrl).Trim()
    Write-Host "Mevcut VDS API: $currentApi" -ForegroundColor DarkGray
    $entered = Read-Host 'VDS public API adresi/IP (Enter = mevcut degeri kullan)'
    if (-not [string]::IsNullOrWhiteSpace($entered)) {
        $value = $entered.Trim()
        if ($value -notmatch '^https?://') {
            $value = 'http://' + $value
            if ($value -notmatch ':\d+$') { $value += ':5088' }
        }
        try { $uri = [Uri]$value } catch { throw "VDS API adresi gecersiz: $value" }
        if ($uri.Scheme -notin @('http','https')) { throw 'VDS API adresi http:// veya https:// olmali.' }
        $cfg.ClientApiUrl = $value.TrimEnd('/')
        Save-BuildConfig $cfg
    }

    $cfg = Get-BuildConfig
    $api = ([string]$cfg.ClientApiUrl).Trim()
    if ([string]::IsNullOrWhiteSpace($api) -or $api -match '127\.0\.0\.1|VDS_IP_ADRESIN') {
        throw "Ilk VDS icin gercek public API/IP girilmeli. Mevcut: $api"
    }

    $licenseKey = Resolve-LicenseKey
    Write-Host "Owner license key: OK ($licenseKey)" -ForegroundColor Green
    Write-Host ''
    Build-FullOwnerServer
    Write-Host ''
    Write-Host 'ILK KURULUM YEREL TARAF TAMAM.' -ForegroundColor Green
    Write-Host 'Artik OUTPUT altindaki SERVER_INSTALL_OWNER_ONLY ZIP VDSye gonderilebilir.' -ForegroundColor Cyan
    Write-Host 'Bu ZIP JSON + zorunlu dosyalar + gercek Raven.Server /health smoke testinden gecti.' -ForegroundColor DarkGreen
}

function Build-FullOwnerServer {
    Ensure-DotNet; Verify-Source
    Invoke-CoreTests
    Invoke-ClientCompileSmoke
    Invoke-ServerTests
    $api = Assert-ProductionHttps (([string](Get-BuildConfig).ClientApiUrl).TrimEnd('/'))
    Write-Host "Paket API: $api" -ForegroundColor DarkGray
    Write-Host 'FULL server paketi yerel preflight ile dogrulanir; public API henuz yayinda olmak zorunda degildir.' -ForegroundColor DarkGray
    $BuildId = New-BuildStamp

    # Ilk kez VDS kurarken gerekli olan owner-only paket. Yalniz burada license private key gerekir.
    $licenseKey = Resolve-LicenseKey
    $updatePub = Join-Path $SourceRoot 'Raven.Server\Secrets\update_public.pem'
    if (-not (Test-Path -LiteralPath $updatePub)) { throw 'Raven.Server\Secrets\update_public.pem bulunamadi.' }

    $ver = Get-Version
    $serverStage = $null
    $fullStage = Join-Path $env:TEMP ('RavenServerFull_' + [guid]::NewGuid().ToString('N'))
    Ensure-Dir $fullStage
    $candidateDir = Join-Path $env:TEMP ('RavenServerInstallOutput_' + [guid]::NewGuid().ToString('N'))
    Ensure-Dir $candidateDir

    $fullZip = Join-Path $candidateDir "RavenServer_Full_v${ver}_OWNER_ONLY.zip"
    $candidateZip = Join-Path $env:TEMP ('RavenServerFullCandidate_' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        $serverStage = New-ServerBuildStage -BuildId $BuildId
        foreach($name in @('App','Content','Tools','RAVEN_SERVER.bat','KUR_RAVEN_SERVER.bat','KALDIR_RAVEN_SERVER.bat','CLOUDFLARE_DOMAIN_REHBERI.txt')) {
            Copy-Item (Join-Path $serverStage $name) $fullStage -Recurse -Force
        }
        # Config her zaman temiz source template'ten uretilir; eski calisma kopyasi tasinmaz.
        New-FirstVdsConfig -Destination (Join-Path $fullStage 'Config\raven_server.json')
        New-CaddyConfig -Destination (Join-Path $fullStage 'Caddyfile') -ApiUrl $api
        Ensure-Dir (Join-Path $fullStage 'Secrets')
        Copy-Item -LiteralPath $licenseKey -Destination (Join-Path $fullStage 'Secrets\license_private.pem') -Force
        Copy-Item -LiteralPath $updatePub -Destination (Join-Path $fullStage 'Secrets\update_public.pem') -Force
        Ensure-Dir (Join-Path $fullStage 'Data')
        Ensure-Dir (Join-Path $fullStage 'Updates')
        Compress-Archive -Path (Join-Path $fullStage '*') -DestinationPath $candidateZip -Force
        Test-FullOwnerPackage -ZipPath $candidateZip
        Move-Item -LiteralPath $candidateZip -Destination $fullZip -Force
        [void](Write-ArtifactHash $fullZip)
        Write-ArtifactInfo -Directory $candidateDir -Kind 'SERVER_INSTALL_OWNER_ONLY' -BuildId $BuildId -ApiUrl $api
        Publish-OutputDirectory -Candidate $candidateDir -Destination (Get-ReleaseOutputPath 'SERVER_INSTALL_OWNER_ONLY')
        $candidateDir = $null
    }
    finally {
        Remove-Item -LiteralPath $candidateZip -Force -ErrorAction SilentlyContinue
        if (-not [string]::IsNullOrWhiteSpace($serverStage)) { Remove-Item -LiteralPath $serverStage -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $fullStage -Recurse -Force -ErrorAction SilentlyContinue
        if (-not [string]::IsNullOrWhiteSpace($candidateDir)) { Remove-Item -LiteralPath $candidateDir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    Update-ReleaseSummary -Kind 'SERVER_INSTALL_OWNER_ONLY' -BuildId $BuildId
    Write-Host 'DIKKAT: Bu paket license private key icerir. Yalniz sana ait VDS icindir.' -ForegroundColor Yellow
    Write-Host "Build: $BuildId" -ForegroundColor DarkGray
    Show-OutputResult 'SERVER_INSTALL_OWNER_ONLY'
}
function Build-Update {
    param([switch]$MigrationOld)
    Ensure-DotNet; Verify-Source; Ensure-Harmony
    Invoke-CoreTests
    $api = Assert-ProductionHttps (([string](Get-BuildConfig).LicenseCenterUrl).TrimEnd('/'))
    Write-Host "Lisans merkezi: $api" -ForegroundColor DarkGray
    Write-Host 'Canli API erisimi client update buildini engellemez.' -ForegroundColor DarkGray
    $buildId = New-BuildStamp
    $key = if ($MigrationOld) { Resolve-OldUpdateKey } else { Resolve-UpdateKey }
    $ver = Get-Version
    $stage = Join-Path $env:TEMP ('RavenUpdate_' + [guid]::NewGuid().ToString('N'))
    $pub = Join-Path $stage 'publish'; $rel = Join-Path $stage 'release'
    Ensure-Dir $pub; Ensure-Dir $rel
    try {
        & dotnet publish (Join-Path $SourceRoot 'RavenMapPanel.Wpf\RavenMapPanel.Wpf.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $pub
        if ($LASTEXITCODE -ne 0) { throw 'Update client publish basarisiz.' }
        $verifySales = Join-Path $SourceRoot 'Raven.ReleaseTool\Internal\verify_sales_output.ps1'
        if (Test-Path $verifySales) { & powershell -NoProfile -ExecutionPolicy Bypass -File $verifySales -Path $pub; if ($LASTEXITCODE -ne 0) { throw 'Update publish guvenlik kontrolu basarisiz.' } }
        & dotnet run --project (Join-Path $SourceRoot 'Raven.ReleaseTool\Raven.ReleaseTool.csproj') -c Release -- --input $pub --version $ver --output $rel --key $key
        if ($LASTEXITCODE -ne 0) { throw 'Update paketi imzalanamadi.' }
        Write-ArtifactInfo -Directory $rel -Kind 'CLIENT_UPDATE' -BuildId $buildId -ApiUrl $api
        $out = if ($MigrationOld) { Join-Path (Get-VersionOutputRoot) 'CLIENT_UPDATE_TRANSITION' } else { Get-ReleaseOutputPath 'CLIENT_UPDATE' }
        Publish-OutputDirectory -Candidate $rel -Destination $out
        if ($MigrationOld) {
            Write-Host "ESKI CLIENT GECIS PAKETI hazir: $out" -ForegroundColor Yellow
            if ($env:RAVEN_NO_OPEN -ne '1') { Start-Process explorer.exe $out }
        }
        else {
            Update-ReleaseSummary -Kind 'CLIENT_UPDATE' -BuildId $buildId
            Show-OutputResult 'CLIENT_UPDATE'
        }
    }
    finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Build: $buildId" -ForegroundColor DarkGray
}
function Edit-Settings {
    $cfg = Get-BuildConfig
    Title 'RAVEN BUILD AYARLARI'
    Write-Host "Lisans merkezi : $($cfg.LicenseCenterUrl)"
    Write-Host "Shopier URL    : $($cfg.StoreUrl)"
    Write-Host "Destek URL     : $($cfg.SupportUrl)"
    Write-Host "Legacy API     : $($cfg.ClientApiUrl)"
    Write-Host "Rust Root      : $($cfg.RustRoot)"
    Write-Host ''
    $licenseCenter = Read-Host 'Yeni HTTPS lisans merkezi adresi (Enter = degistirme)'
    if (-not [string]::IsNullOrWhiteSpace($licenseCenter)) { $cfg.LicenseCenterUrl=Assert-ProductionHttps $licenseCenter.Trim() }
    $store = Read-Host 'Shopier satin alma URL (Enter = degistirme)'
    if (-not [string]::IsNullOrWhiteSpace($store)) { $cfg.StoreUrl=Assert-ProductionHttps $store.Trim() }
    $support = Read-Host 'Destek URL (Enter = degistirme)'
    if (-not [string]::IsNullOrWhiteSpace($support)) { $cfg.SupportUrl=Assert-ProductionHttps $support.Trim() }
    $rust = Read-Host 'Yeni Rust root (Enter = degistirme)'
    if (-not [string]::IsNullOrWhiteSpace($rust)) { $cfg.RustRoot=$rust.Trim() }
    Save-BuildConfig $cfg
    Write-Host 'Ayarlar kaydedildi.' -ForegroundColor Green
}

function Set-ReleaseVersion {
    $currentText = Get-Version
    Title 'RAVEN YENI SURUM'
    Write-Host "Mevcut surum: $currentText" -ForegroundColor Cyan
    $entered = (Read-Host 'Yeni surum (ornek: 1.8.0)').Trim()
    if ($entered -notmatch '^\d+\.\d+\.\d+$') { throw 'Surum X.Y.Z biciminde olmali. Ornek: 1.8.0' }
    $current = [Version]$currentText
    $next = [Version]$entered
    if ($next -le $current) { throw "Yeni surum mevcut surumden buyuk olmali: $currentText" }

    foreach($relative in @('RavenMapPanel.Wpf\RavenMapPanel.Wpf.csproj','Raven.Server\Raven.Server.csproj')) {
        $path = Join-Path $SourceRoot $relative
        $content = Get-Content -Raw -Encoding UTF8 $path
        $content = [Text.RegularExpressions.Regex]::Replace($content, '<Version>[^<]+</Version>', "<Version>$entered</Version>", 1)
        if ($content -match '<FileVersion>') {
            $content = [Text.RegularExpressions.Regex]::Replace($content, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$entered.0</FileVersion>", 1)
        }
        [IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false))
    }
    if ((Get-Version) -ne $entered) { throw 'Surum dosyalara dogru yazilamadi.' }
    Write-Host "Yeni surum hazir: v$entered" -ForegroundColor Green
    Write-Host "Yeni ciktilar OUTPUT\v$entered altinda olusacak." -ForegroundColor DarkGreen
}

function Invoke-RavenCommand([string]$Name) {
    switch($Name) {
        'ClientInstall' { Build-Client -Sales }
        'ClientUpdate' { Build-Update }
        'ServerUpdate' { Build-Server }
        'ServerInstall' { Build-FullOwnerServer }
        'SetVersion' { Set-ReleaseVersion }
        'Settings' { Edit-Settings }
        'OpenOutput' { Ensure-Dir $OutputRoot; Start-Process explorer.exe $OutputRoot }
        'Verify' { Verify-Source; Invoke-CoreTests; Invoke-ClientCompileSmoke; Invoke-ServerTests; Invoke-ServerCompileSmoke; Write-Host 'Tum kontroller basarili.' -ForegroundColor Green }
        default { throw "Bilinmeyen Raven komutu: $Name" }
    }
}

if (-not [string]::IsNullOrWhiteSpace($Command)) {
    try {
        Invoke-RavenCommand $Command
        exit 0
    }
    catch {
        Write-Host ''
        Write-Host ('HATA: ' + $_.Exception.Message) -ForegroundColor Red
        exit 1
    }
}

while ($true) {
    $ver = Get-Version
    $cfg = Get-BuildConfig
    Title "RAVEN RELEASE CENTER  v$ver"
    Write-Host "LISANS    : $($cfg.LicenseCenterUrl)" -ForegroundColor DarkGray
    Write-Host "CIKTILAR  : $(Get-VersionOutputRoot)" -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  1. MUSTERI CLIENT ZIP OLUSTUR'
    Write-Host '  2. CLIENT GUNCELLEMESI OLUSTUR'
    Write-Host '  3. LEGACY SERVER GUNCELLEMESI OLUSTUR (OPSIYONEL)'
    Write-Host '  4. LEGACY VDS FULL SERVER PAKETI (MAP LISANSI ICIN GEREKMEZ)'
    Write-Host ''
    Write-Host '  S. YENI SURUM NUMARASI'
    Write-Host '  A. AYARLAR (LISANS MERKEZI / SHOPIER / DESTEK / Rust yolu)'
    Write-Host '  V. SISTEMI DOGRULA'
    Write-Host '  O. OUTPUT KLASORUNU AC'
    Write-Host '  0. CIKIS'
    Write-Host ''
    $choice = (Read-Host 'Secim').Trim().ToUpperInvariant()
    try {
        switch($choice) {
            '1' { Invoke-RavenCommand 'ClientInstall'; Pause-Raven }
            '2' { Invoke-RavenCommand 'ClientUpdate'; Pause-Raven }
            '3' { Invoke-RavenCommand 'ServerUpdate'; Pause-Raven }
            '4' { Invoke-RavenCommand 'ServerInstall'; Pause-Raven }
            'S' { Invoke-RavenCommand 'SetVersion'; Pause-Raven }
            'A' { Invoke-RavenCommand 'Settings'; Pause-Raven }
            'V' { Invoke-RavenCommand 'Verify'; Pause-Raven }
            'O' { Invoke-RavenCommand 'OpenOutput' }
            '0' { exit 0 }
            default { Write-Host 'Gecersiz secim.' -ForegroundColor Yellow; Start-Sleep 1 }
        }
    } catch {
        Write-Host ''
        Write-Host ('HATA: ' + $_.Exception.Message) -ForegroundColor Red
        Pause-Raven
    }
}
