$ErrorActionPreference = 'Stop'
$installRoot = [Environment]::GetEnvironmentVariable('RAVEN_SERVER_ROOT','Machine')
if ([string]::IsNullOrWhiteSpace($installRoot)) { $installRoot = Join-Path $env:ProgramData 'RavenMap\Server' }
$statePath = Join-Path $installRoot 'Data\health-monitor-state.json'
$logPath = Join-Path $installRoot 'Logs\health-monitor.log'
New-Item -ItemType Directory -Path (Split-Path -Parent $statePath) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $logPath) -Force | Out-Null

$failures = 0
if (Test-Path -LiteralPath $statePath) {
    try { $failures = [int](Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).consecutiveFailures } catch { $failures = 0 }
}

try {
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5088/health' -TimeoutSec 8
    if ($health.ok -ne $true -and $null -eq $health.version) { throw 'Beklenmeyen health cevabi.' }
    $failures = 0
} catch {
    $failures++
    if ($failures -ge 3) {
        Restart-Service -Name 'RavenMapServer' -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 5
        if ((Get-Service -Name 'RavenMapCaddy' -ErrorAction SilentlyContinue).Status -ne 'Running') {
            Start-Service -Name 'RavenMapCaddy' -ErrorAction SilentlyContinue
        }
        Add-Content -LiteralPath $logPath -Value ("{0} RavenMapServer uc health hatasi sonrasi yeniden baslatildi." -f [DateTimeOffset]::Now.ToString('O'))
        $failures = 0
    }
}

$state = @{ checkedUtc=[DateTimeOffset]::UtcNow.ToString('O'); consecutiveFailures=$failures }
[IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
