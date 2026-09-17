param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference='Stop'
$root=(Resolve-Path $Path).Path
$forbiddenExtensions=@('.cs','.csproj','.sln','.pdb','.pem','.map','.bat','.ps1')
$bad=Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
    $forbiddenExtensions -contains $_.Extension.ToLowerInvariant() -or
    $_.Name -match '(?i)(private[_-]?key|license_private|update_private)'
}
if($bad){
    Write-Host 'SATIS PAKETI GUVENLIK KONTROLU BASARISIZ:' -ForegroundColor Red
    $bad | ForEach-Object { Write-Host ('  ' + $_.FullName) -ForegroundColor Red }
    exit 1
}
$exe=Join-Path $root 'RavenMapPanel.exe'
if(-not (Test-Path $exe)){ throw 'RavenMapPanel.exe publish klasorunde bulunamadi.' }
Write-Host 'Sales output security check: OK' -ForegroundColor Green
