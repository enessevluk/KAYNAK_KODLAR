param([ValidateSet('User','Machine')][string]$Scope='Machine')
$ErrorActionPreference='Stop'

if ($Scope -eq 'Machine') {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  $p = New-Object Security.Principal.WindowsPrincipal($id)
  if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Machine environment variable ayarlamak icin BAT dosyasini Yonetici olarak calistirin.'
  }
}

function Plain([Security.SecureString]$s) {
  $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
  try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

function Base32([byte[]]$bytes) {
  $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
  $bitText = ''
  foreach ($b in $bytes) {
    $bitText += [Convert]::ToString([int]$b, 2).PadLeft(8, '0')
  }
  while (($bitText.Length % 5) -ne 0) { $bitText += '0' }
  $out = New-Object System.Text.StringBuilder
  for ($i = 0; $i -lt $bitText.Length; $i += 5) {
    $idx = [Convert]::ToInt32($bitText.Substring($i, 5), 2)
    [void]$out.Append($alphabet[$idx])
  }
  return $out.ToString()
}

function New-CryptoBytes([int]$Count) {
  $bytes = New-Object byte[] $Count
  $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
  try { $rng.GetBytes($bytes) }
  finally { if ($null -ne $rng) { $rng.Dispose() } }
  return $bytes
}

$target = [Enum]::Parse([EnvironmentVariableTarget], $Scope)

Write-Host '=== RAVEN SERVER SECRET AYARLARI ===' -ForegroundColor Cyan
Write-Host "Scope: $Scope"

$admin = Read-Host 'Admin panel sifresi' -AsSecureString
$adminText = Plain $admin
if ([string]::IsNullOrWhiteSpace($adminText) -or $adminText.Length -lt 12) {
  throw 'Admin sifresi en az 12 karakter olmali.'
}
[Environment]::SetEnvironmentVariable('RAVEN_ADMIN_PASSWORD', $adminText, $target)

$existing = [Environment]::GetEnvironmentVariable('RAVEN_ADMIN_TOTP_SECRET', $target)
$useExisting = $false
if (-not [string]::IsNullOrWhiteSpace($existing)) {
  $ans = Read-Host 'Mevcut TOTP secret korunsun mu? (E/H) [E]'
  $useExisting = [string]::IsNullOrWhiteSpace($ans) -or $ans -match '^(e|evet|y|yes)$'
}

if ($useExisting) {
  $totp = $existing
} else {
  $totp = Base32 (New-CryptoBytes 20)
  [Environment]::SetEnvironmentVariable('RAVEN_ADMIN_TOTP_SECRET', $totp, $target)
}


Write-Host ''
Write-Host 'Admin guvenlik degerleri kaydedildi.' -ForegroundColor Green
Write-Host 'Admin TOTP secret (Authenticator uygulamasina MANUEL ekle; bunu kimseyle paylasma):' -ForegroundColor Yellow
Write-Host $totp
Write-Host 'Shopier / urun / Discord-destek ayarlari Admin Panel > Sistem Ayarlari bolumunden yonetilir.' -ForegroundColor DarkGray
Write-Host 'Raven.Server processini yeniden baslatin. Guncel RAVEN_SERVER.bat her baslatmada kayitli secretlari yeniden yukler.'
