param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference='Stop'
# CMD/PowerShell quoting edge-case: a trailing backslash can leave a literal quote in the argument.
$Path = $Path.Trim().Trim('"')
$root=(Resolve-Path -LiteralPath $Path).Path
$bad=@()
Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -notmatch '[\\/](bin|obj|Release|KeyBackups)[\\/]' } |
  ForEach-Object {
    $name=$_.Name
    if ($name -match '(?i)private.*\.pem$|\.pfx$|\.p12$|\.key$|^\.env($|\.)') { $bad += $_.FullName; return }
    if ($_.Extension -eq '.pem') {
      try { if (Select-String -LiteralPath $_.FullName -Pattern '-----BEGIN .*PRIVATE KEY-----' -Quiet) { $bad += $_.FullName } } catch {}
    }
  }
if ($bad.Count -gt 0) {
  Write-Host 'HATA: Kaynak ağacında private secret bulundu:' -ForegroundColor Red
  $bad | Sort-Object -Unique | ForEach-Object { Write-Host ('  ' + $_) }
  exit 1
}
Write-Host 'Kaynak secret kontrolü: OK (private key yok).' -ForegroundColor Green
