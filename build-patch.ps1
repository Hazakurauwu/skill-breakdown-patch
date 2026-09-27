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
if ($LASTEXITCODE -ne 0) { throw "pass1 failed (exit $LASTEXITCODE)" }

# The helper references Tera.Core.dll and Data.dll, and those genuinely DIFFER between
# meter forks (stock TeraToolbox vs Crazy-eSports-ClassicPlus ship different builds --
# verified by hash). Always compile the helper against the reference assemblies of the
# very meter we are patching, so a fork can never end up with IL bound to another fork's
# type/member layout. Same reasoning as building the patched DamageMeter.dll from that
# fork's own stock DLL rather than from one static prebuilt binary.
$libs = Join-Path $root "libs"
New-Item -ItemType Directory -Force -Path $libs | Out-Null
foreach ($refDll in @("Tera.Core.dll", "Data.dll")) {
    $src = Join-Path $MeterDir $refDll
    if (-not (Test-Path $src)) { throw "$refDll not found in $MeterDir" }
    Copy-Item $src (Join-Path $libs $refDll) -Force
}
Write-Host ("    reference libs taken from " + $MeterDir)

# The merged helper IL keeps the helper's own framework references, so the helper has to be
# compiled for the same .NET major the meter runs on. TeraToolbox's meter is net7, the Classic+
# fork is net8; v1.6 shipped a net8 helper inside the toolbox build and crashed every net7 meter
# at the end of the first fight. Read it from the meter itself instead of assuming.
$rtCfg = Join-Path $MeterDir "ShinraMeter.runtimeconfig.json"
if (-not (Test-Path $rtCfg)) { throw "ShinraMeter.runtimeconfig.json not found in $MeterDir" }
$rt = (Get-Content $rtCfg -Raw | ConvertFrom-Json).runtimeOptions
$fwVersions = @($rt.frameworks | ForEach-Object { $_.version }) + @($rt.framework.version) | Where-Object { $_ }
$meterMajor = ($fwVersions | ForEach-Object { [int]($_.Split('.')[0]) } | Measure-Object -Maximum).Maximum
if (-not $meterMajor) { throw "could not read the .NET version from $rtCfg" }
$helperTfm = "net$meterMajor.0-windows"
Write-Host ("    meter runs on .NET " + $meterMajor + " -> helper built as " + $helperTfm)

Write-Host "==> building helper against patched reference" -ForegroundColor Cyan
Remove-Item (Join-Path $helper "obj") -Recurse -Force -ErrorAction SilentlyContinue
dotnet build $helper -c Release -v quiet "-p:HelperTfm=$helperTfm" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "helper build failed (exit $LASTEXITCODE)" }
$helperDll = Join-Path $helper "bin\Release\$helperTfm\ShinraRotationPatch.dll"

Write-Host "==> mergeinject: merge RotationEnricher into DamageMeter + inject call" -ForegroundColor Cyan
$patched = Join-Path $work "DamageMeter.patched.dll"
dotnet $patcherDll mergeinject $p1 $helperDll $patched
if ($LASTEXITCODE -ne 0) { throw "mergeinject failed (exit $LASTEXITCODE)" }

Write-Host "==> verify merged dll is self-contained" -ForegroundColor Cyan
dotnet $patcherDll verify $patched $meterMajor
if ($LASTEXITCODE -ne 0) { throw "verify FAILED (exit $LASTEXITCODE) -- not shipping this binary" }

Write-Host "==> assembling $OutDir" -ForegroundColor Cyan
Copy-Item "$MeterDir\*" $OutDir -Recurse -Force
# keep the pristine original for rollback
Copy-Item $inputDll (Join-Path $OutDir "DamageMeter.dll.orig.bak") -Force
Copy-Item $patched   (Join-Path $OutDir "DamageMeter.dll") -Force
# no separate helper DLL anymore -- it is merged into DamageMeter.dll

function FileHash256($p) { (Get-FileHash -Algorithm SHA256 -Path $p).Hash.ToLower() }
$dmHash = FileHash256 (Join-Path $OutDir "DamageMeter.dll")

# Only the classic TeraToolbox mod layout ships a manifest.json with a
# per-file SHA-256 map that the toolbox checks on load. The Crazy-eSports-
# ClassicPlus launcher's "external mod" layout (registry.json at the
# launcher level, .rsa signature files instead) has no such file -- nothing
# to regenerate there, and trying to would just fail on a missing path.
$manifestPath = Join-Path $OutDir "manifest.json"
if (Test-Path $manifestPath) {
    Write-Host "==> regenerating manifest.json (SHA-256)" -ForegroundColor Cyan
    $man = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $man.files.'DamageMeter.dll' = $dmHash
    # helper is merged into DamageMeter.dll now -- no separate entry needed

    # re-emit with tabs to match the original manifest style
    $json = $man | ConvertTo-Json -Depth 10
    $json = ($json -split "`n" | ForEach-Object { ($_ -replace '    ', "`t") }) -join "`n"
    Set-Content -Path $manifestPath -Value $json -Encoding utf8
} else {
    Write-Host "==> no manifest.json in this meter layout -- skipping hash regeneration" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "DONE." -ForegroundColor Green
Write-Host ("  DamageMeter.dll (merged)  SHA-256 = " + $dmHash)
Write-Host ("  output: " + $OutDir)
Write-Host ""
Write-Host "The DamageMeter.dll is self-contained (RotationEnricher merged in)."
