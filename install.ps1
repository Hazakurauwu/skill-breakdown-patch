# Self-elevate to admin. ShinraMeter often lives under Program Files, which
# needs admin rights to modify.
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

# Patch files live in a 'release' subfolder, or right next to this script
# (depends on how the zip was extracted). Accept both.
$releaseDir = Join-Path $PSScriptRoot "release"
if (-not (Test-Path "$releaseDir\DamageMeter.dll")) { $releaseDir = $PSScriptRoot }
if (-not (Test-Path "$releaseDir\DamageMeter.dll")) {
    Write-Host "  ERROR: could not find the patch files (DamageMeter.dll)." -ForegroundColor Red
    Write-Host "  Make sure you extracted the WHOLE zip and that install.bat is" -ForegroundColor Red
    Write-Host "  in the same place as the .dll files. Then run install.bat again." -ForegroundColor Red
    Read-Host "  Press Enter to exit"
    exit 1
}

# 1. Auto-detect in common locations (including Program Files)
$common = @(
    "$env:USERPROFILE\Desktop\TeraToolbox",
    "$env:USERPROFILE\Desktop\TeraToolbox Private",
    "$env:USERPROFILE\Documents\TeraToolbox",
    "$env:USERPROFILE\Downloads\TeraToolbox",
    "${env:ProgramFiles(x86)}\TeraToolbox",
    "$env:ProgramFiles\TeraToolbox",
    "C:\TeraToolbox",
    "C:\TeraToolbox Private",
    "D:\TeraToolbox"
)
$shinra = $null
foreach ($c in $common) {
    $shinra = Find-ShinraFolder $c
    if ($shinra) { break }
}

# 2. Confirm the detected folder, or open a folder picker
if ($shinra) {
    Write-Host "  Found ShinraMeter at:" -ForegroundColor Cyan
    Write-Host "    $shinra"
    Write-Host ""
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
            Read-Host "  Press Enter to exit"
            exit 1
        }
        $shinra = Find-ShinraFolder $dlg.SelectedPath
        if (-not $shinra) {
            Write-Host "  No ShinraMeter (DamageMeter.dll) found there. Try again." -ForegroundColor Red
        }
    }
    Write-Host "  Using: $shinra" -ForegroundColor Cyan
}

# 3. Make sure the toolbox is closed before we touch the files
if (Test-ToolboxRunning) {
    Write-Host ""
    Write-Host "  TeraToolbox (or the meter) seems to be running." -ForegroundColor Yellow
    Write-Host "  Please close it completely, then press Enter to continue." -ForegroundColor Yellow
    Read-Host
}

# 4. Back up originals (only once, so re-running is safe)
Write-Host ""
Write-Host "  Backing up original files..."
foreach ($f in @("DamageMeter.dll","ShinraMeter.deps.json","module.json","manifest.json")) {
    $src = Join-Path $shinra $f
    $bak = "$src.prepatch.bak"
    if ((Test-Path $src) -and -not (Test-Path $bak)) {
        Copy-Item $src $bak -Force
        Write-Host "    backed up: $f"
    }
}

# 5. Install. DamageMeter.dll is the file that gets locked while the meter runs.
Write-Host ""
Write-Host "  Installing patch files..."
try {
    Copy-Item "$releaseDir\DamageMeter.dll" (Join-Path $shinra "DamageMeter.dll") -Force -ErrorAction Stop
    Write-Host "    installed: DamageMeter.dll"
} catch {
    $msg = $_.Exception.Message
    Write-Host ""
    Write-Host "  ============================================" -ForegroundColor Red
    if ($msg -match "being used|another process|0x80070020|in use") {
        Write-Host "   TeraToolbox is still open and locking the file." -ForegroundColor Red
        Write-Host ""
        Write-Host "   Close it completely (check the system tray near" -ForegroundColor Red
        Write-Host "   the clock), then run this installer again." -ForegroundColor Red
    } elseif ($msg -match "denied|Unauthorized") {
        Write-Host "   Windows blocked writing to that folder." -ForegroundColor Red
        Write-Host "   Try right-clicking install.bat and choosing" -ForegroundColor Red
        Write-Host "   'Run as administrator'." -ForegroundColor Red
    } else {
        Write-Host "   $msg" -ForegroundColor Red
    }
    Write-Host "  ============================================" -ForegroundColor Red
    Write-Host ""
    Read-Host "  Press Enter to exit"
    exit 1
}

foreach ($f in @("ShinraRotationPatch.dll","ShinraMeter.deps.json","module.json","manifest.json")) {
    Copy-Item (Join-Path $releaseDir $f) (Join-Path $shinra $f) -Force
    Write-Host "    installed: $f"
}

Write-Host ""
Write-Host "  ============================================" -ForegroundColor Green
Write-Host "   Done! Start TeraToolbox to use the patch." -ForegroundColor Green
Write-Host ""
Write-Host "   Your originals are saved as *.prepatch.bak"
Write-Host "   To undo, run uninstall.bat"
Write-Host "  ============================================" -ForegroundColor Green
Write-Host ""
Read-Host "  Press Enter to exit"
