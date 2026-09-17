param(
    [string]$RustRoot = "C:\RavenMapServer\RustServer",
    [switch]$InstallToRust
)

$ErrorActionPreference = "Stop"
$managed = Join-Path $RustRoot "RustDedicated_Data\Managed"
if (-not (Test-Path -LiteralPath (Join-Path $managed "Assembly-CSharp.dll"))) {
    throw "Rust Managed klasoru bulunamadi: $managed"
}

dotnet msbuild (Join-Path $PSScriptRoot "CustomGenerator.sln") `
    /t:Rebuild /p:Configuration=Release "/p:RustServerRoot=$RustRoot" "/p:RustManagedDir=$managed" /v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $PSScriptRoot "CustomGenerator\bin\Release\CustomGenerator.dll"
if ($InstallToRust) {
    $rustMods = Join-Path $RustRoot "HarmonyMods"
    New-Item -ItemType Directory -Path $rustMods -Force | Out-Null
    Copy-Item -LiteralPath $dll -Destination (Join-Path $rustMods "CustomGenerator.dll") -Force
}
Write-Host "Basarili: $dll"
