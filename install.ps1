# Self-elevate to admin (ShinraMeter may live under Program Files).
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @(
        "-NoProfile","-ExecutionPolicy","Bypass","-File","`"$PSCommandPath`""
    )
    exit
}

Add-Type -AssemblyName System.Windows.Forms

function Find-ShinraFolder {
    param([string]$base)
    if ([string]::IsNullOrWhiteSpace($base) -or -not (Test-Path $base)) { return $null }
    $hits = Get-ChildItem -Path $base -Filter "DamageMeter.dll" -Recurse -File -ErrorAction SilentlyContinue
    if (-not $hits) { return $null }
    $pref = $hits | Where-Object { $_.Directory.Name -ieq "ShinraMeter" } | Select-Object -First 1
    if ($pref) { return $pref.Directory.FullName }
    return $hits[0].Directory.FullName
}

function Test-ToolboxRunning {
    foreach ($n in @("TeraToolbox","tera-toolbox")) {
        if (Get-Process -Name $n -ErrorAction SilentlyContinue) { return $true }
    }
    return $false
}

Write-Host ""
Write-Host "  ============================================" -ForegroundColor Green
Write-Host "   ShinraMeter Rotation Patch - Installer" -ForegroundColor Green
Write-Host "  ============================================" -ForegroundColor Green
Write-Host ""
Write-Host "   IMPORTANT: close TeraToolbox completely before continuing." -ForegroundColor Yellow
Write-Host ""

# Patch DLL lives in a 'release' subfolder, or next to this script.
$releaseDir = Join-Path $PSScriptRoot "release"
if (-not (Test-Path "$releaseDir\DamageMeter.dll")) { $releaseDir = $PSScriptRoot }
if (-not (Test-Path "$releaseDir\DamageMeter.dll")) {
    Write-Host "  ERROR: could not find DamageMeter.dll next to this script." -ForegroundColor Red
    Write-Host "  Make sure you extracted the WHOLE zip." -ForegroundColor Red
    Read-Host "  Press Enter to exit"; exit 1
}

# 1. Auto-detect, with confirm + folder picker fallback
$common = @(
    "$env:USERPROFILE\Desktop\TeraToolbox","$env:USERPROFILE\Desktop\TeraToolbox Private",
    "$env:USERPROFILE\Documents\TeraToolbox","$env:USERPROFILE\Downloads\TeraToolbox",
    "${env:ProgramFiles(x86)}\TeraToolbox","$env:ProgramFiles\TeraToolbox",
    "C:\TeraToolbox","C:\TeraToolbox Private","D:\TeraToolbox"
)
$shinra = $null
foreach ($c in $common) { $shinra = Find-ShinraFolder $c; if ($shinra) { break } }

if ($shinra) {
    Write-Host "  Found ShinraMeter at:" -ForegroundColor Cyan
    Write-Host "    $shinra"; Write-Host ""
    $ans = Read-Host "  Use this folder? Press Enter for yes, or type N to choose another"
    if ($ans -match '^[nN]') { $shinra = $null }
}
if (-not $shinra) {
    Write-Host ""
    Write-Host "  A window will open. Select your TeraToolbox folder" -ForegroundColor Yellow
    Write-Host "  (or the ShinraMeter folder itself) and click OK." -ForegroundColor Yellow
    Start-Sleep -Milliseconds 400
    while (-not $shinra) {
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = "Select your TeraToolbox folder (or the ShinraMeter folder)"
        $dlg.ShowNewFolderButton = $false
        if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            Write-Host "  Cancelled. Nothing was changed." -ForegroundColor Red
            Read-Host "  Press Enter to exit"; exit 1
        }
        $shinra = Find-ShinraFolder $dlg.SelectedPath
        if (-not $shinra) { Write-Host "  No ShinraMeter found there. Try again." -ForegroundColor Red }
    }
    Write-Host "  Using: $shinra" -ForegroundColor Cyan
}

if (Test-ToolboxRunning) {
    Write-Host ""
    Write-Host "  TeraToolbox seems to be running. Close it completely," -ForegroundColor Yellow
    Write-Host "  then press Enter to continue." -ForegroundColor Yellow
    Read-Host
}

# 2. Back up originals (once)
Write-Host ""
Write-Host "  Backing up original files..."
foreach ($f in @("DamageMeter.dll","module.json","manifest.json")) {
    $src = Join-Path $shinra $f; $bak = "$src.prepatch.bak"
    if ((Test-Path $src) -and -not (Test-Path $bak)) { Copy-Item $src $bak -Force; Write-Host "    backed up: $f" }
}

# Clean any leftover from the old (external-DLL) version of this patch
$oldDll = Join-Path $shinra "ShinraRotationPatch.dll"
if (Test-Path $oldDll) { Remove-Item $oldDll -Force; Write-Host "    removed old ShinraRotationPatch.dll" }

# 3. Install the merged DamageMeter.dll
Write-Host ""
Write-Host "  Installing patched DamageMeter.dll..."
try {
    Copy-Item "$releaseDir\DamageMeter.dll" (Join-Path $shinra "DamageMeter.dll") -Force -ErrorAction Stop
    Write-Host "    installed: DamageMeter.dll"
} catch {
    $msg = $_.Exception.Message
    Write-Host ""
    Write-Host "  ============================================" -ForegroundColor Red
    if ($msg -match "being used|another process|0x80070020|in use") {
        Write-Host "   TeraToolbox is still open and locking the file." -ForegroundColor Red
        Write-Host "   Close it completely, then run this again." -ForegroundColor Red
    } elseif ($msg -match "denied|Unauthorized") {
        Write-Host "   Windows blocked writing. Right-click install.bat ->" -ForegroundColor Red
        Write-Host "   Run as administrator." -ForegroundColor Red
    } else { Write-Host "   $msg" -ForegroundColor Red }
    Write-Host "  ============================================" -ForegroundColor Red
    Read-Host "  Press Enter to exit"; exit 1
}

# 4. Turn OFF auto-update in module.json (so the toolbox won't overwrite the patch)
$modPath = Join-Path $shinra "module.json"
if (Test-Path $modPath) {
    try {
        $mod = Get-Content $modPath -Raw | ConvertFrom-Json
        if ($mod.PSObject.Properties.Name -contains 'disableAutoUpdate') { $mod.disableAutoUpdate = $true }
        else { $mod | Add-Member -NotePropertyName disableAutoUpdate -NotePropertyValue $true }
        $json = $mod | ConvertTo-Json -Depth 20
        [System.IO.File]::WriteAllText($modPath, $json, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "    auto-update disabled in module.json"
    } catch { Write-Host "    WARNING: could not edit module.json: $($_.Exception.Message)" -ForegroundColor Yellow }
}

# 5. Recompute manifest.json hash for the new DamageMeter.dll (toolbox validates this)
$manPath = Join-Path $shinra "manifest.json"
if (Test-Path $manPath) {
    try {
        $man = Get-Content $manPath -Raw | ConvertFrom-Json
        $newHash = (Get-FileHash (Join-Path $shinra "DamageMeter.dll") -Algorithm SHA256).Hash.ToLower()
        if ($man.files.PSObject.Properties.Name -contains 'DamageMeter.dll') { $man.files.'DamageMeter.dll' = $newHash }
        $json = $man | ConvertTo-Json -Depth 30
        [System.IO.File]::WriteAllText($manPath, $json, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "    manifest updated for DamageMeter.dll"
    } catch { Write-Host "    WARNING: could not update manifest: $($_.Exception.Message)" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "  ============================================" -ForegroundColor Green
Write-Host "   Done! Start TeraToolbox to use the patch." -ForegroundColor Green
Write-Host ""
Write-Host "   Originals saved as *.prepatch.bak"
Write-Host "   To undo, run uninstall.bat"
Write-Host "  ============================================" -ForegroundColor Green
Write-Host ""
Read-Host "  Press Enter to exit"
