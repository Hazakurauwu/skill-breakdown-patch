# Reproducible build of the ShinraMeter rotation patch.
#
# Produces, into -OutDir, a ready-to-host copy of the meter where:
#   * DamageMeter.dll  = IL-patched (adds Members.dealtSkillLog + InternalsVisibleTo +
#                        a call to RotationEnricher.Enrich(stats) in AutomatedExport)
#   * ShinraRotationPatch.dll = the helper that fills the hit-by-hit dealtSkillLog
#   * manifest.json    = regenerated SHA-256 for DamageMeter.dll + new entry for the helper
#
# Re-run this whenever the upstream meter updates (it relocates the injection point by
# type/method name, so it survives recompiles).
#
# Usage:
#   pwsh ./build-patch.ps1 -MeterDir "<folder with the meter dlls>" -OutDir "<output>"

param(
    [Parameter(Mandatory)][string]$MeterDir,
    [Parameter(Mandatory)][string]$OutDir
)

$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$work    = Join-Path $root "work"
$patcher = Join-Path $root "patcher"
$helper  = Join-Path $root "helper"
New-Item -ItemType Directory -Force -Path $work, $OutDir | Out-Null

$inputDll = Join-Path $MeterDir "DamageMeter.dll"
if (-not (Test-Path $inputDll)) { throw "DamageMeter.dll not found in $MeterDir" }

Write-Host "==> building patcher" -ForegroundColor Cyan
dotnet build $patcher -c Release -v quiet | Out-Null
$patcherDll = Join-Path $patcher "bin\Release\net8.0\Patcher.dll"

Write-Host "==> pass1: add field + InternalsVisibleTo" -ForegroundColor Cyan
$p1 = Join-Path $work "DamageMeter.p1.dll"
dotnet $patcherDll pass1 $inputDll $p1

Write-Host "==> building helper against patched reference" -ForegroundColor Cyan
dotnet build $helper -c Release -v quiet | Out-Null
$helperDll = Join-Path $helper "bin\Release\ShinraRotationPatch.dll"

Write-Host "==> mergeinject: merge RotationEnricher into DamageMeter + inject call" -ForegroundColor Cyan
$patched = Join-Path $work "DamageMeter.patched.dll"
dotnet $patcherDll mergeinject $p1 $helperDll $patched

Write-Host "==> verify merged dll is self-contained" -ForegroundColor Cyan
dotnet $patcherDll verify $patched

Write-Host "==> assembling $OutDir" -ForegroundColor Cyan
Copy-Item "$MeterDir\*" $OutDir -Recurse -Force
# keep the pristine original for rollback
Copy-Item $inputDll (Join-Path $OutDir "DamageMeter.dll.orig.bak") -Force
Copy-Item $patched   (Join-Path $OutDir "DamageMeter.dll") -Force
# no separate helper DLL anymore -- it is merged into DamageMeter.dll

Write-Host "==> regenerating manifest.json (SHA-256)" -ForegroundColor Cyan
$manifestPath = Join-Path $OutDir "manifest.json"
$man = Get-Content $manifestPath -Raw | ConvertFrom-Json
function FileHash256($p) { (Get-FileHash -Algorithm SHA256 -Path $p).Hash.ToLower() }

$dmHash = FileHash256 (Join-Path $OutDir "DamageMeter.dll")
$man.files.'DamageMeter.dll' = $dmHash
# helper is merged into DamageMeter.dll now -- no separate entry needed

# re-emit with tabs to match the original manifest style
$json = $man | ConvertTo-Json -Depth 10
$json = ($json -split "`n" | ForEach-Object { ($_ -replace '    ', "`t") }) -join "`n"
Set-Content -Path $manifestPath -Value $json -Encoding utf8

Write-Host ""
Write-Host "DONE." -ForegroundColor Green
Write-Host ("  DamageMeter.dll (merged)  SHA-256 = " + $dmHash)
Write-Host ("  output: " + $OutDir)
Write-Host ""
Write-Host "The DamageMeter.dll is self-contained (RotationEnricher merged in)."
