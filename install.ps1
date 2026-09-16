# Self-elevate to admin (ShinraMeter may live under Program Files).
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @(
        "-NoProfile","-ExecutionPolicy","Bypass","-File","`"$PSCommandPath`""
    )
    exit
}

Add-Type -AssemblyName System.Windows.Forms

# Safety net for the whole script: without this, any unhandled error (a
# permission-denied folder deep in a scan, a JSON parse failure, anything)
# closes this elevated window instantly with zero message -- the person
# running it just sees the installer vanish for no visible reason. This
# guarantees they always see what went wrong before the window closes.
trap {
    Write-Host ""
    Write-Host "  ============================================" -ForegroundColor Red
    Write-Host "   Unexpected error, installer stopped:" -ForegroundColor Red
    Write-Host "   $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  ============================================" -ForegroundColor Red
    Write-Host ""
    Read-Host "  Press Enter to exit"
    exit 1
}

# Classic System.Windows.Forms.FolderBrowserDialog (SHBrowseForFolder under
# the hood) has no address bar at all -- you can't type or paste a path into
# it, only click through the tree. This uses the standard workaround: the
# modern Explorer-style OpenFileDialog in "pick a folder" mode, which DOES
# have a normal path field you can paste into and press Enter on.
function Select-FolderDialog {
    param([string]$description = "Select a folder")
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = $description
    $dlg.ValidateNames = $false
    $dlg.CheckFileExists = $false
    $dlg.CheckPathExists = $true
    $dlg.FileName = "Select Folder"
    $dlg.Filter = "Folders|`n"
    if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return $null }
    # Pasting a full folder path directly into the box and pressing Enter
    # can leave that exact path in FileName on some Windows builds (rather
    # than navigating into it first) -- use it as-is when it's already a
    # real folder, otherwise fall back to stripping the placeholder name.
    if (Test-Path $dlg.FileName -PathType Container) { return $dlg.FileName }
    return [System.IO.Path]::GetDirectoryName($dlg.FileName)
}

# Scans every base folder and returns every DISTINCT folder that contains a
# DamageMeter.dll (a person can have more than one -- e.g. a stock
# TeraToolbox install for one server plus a separate private-server client
# like Crazy-eSports-ClassicPlus for another. Every match across every base
# is collected here; nothing stops at the first hit.
function Find-ShinraFolders {
    param([string[]]$bases)
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($base in $bases) {
        if ([string]::IsNullOrWhiteSpace($base) -or -not (Test-Path $base)) { continue }
        try {
            $hits = Get-ChildItem -Path $base -Filter "DamageMeter.dll" -Recurse -File -ErrorAction SilentlyContinue
        } catch {
            # One bad subfolder under this base (a locked/broken cloud-sync
            # placeholder, a restricted app-container, whatever) must not
            # abort scanning the rest of the bases -- skip it and continue.
            continue
        }
        foreach ($h in $hits) {
            $dir = $h.Directory.FullName
            if (-not $found.Contains($dir)) { [void]$found.Add($dir) }
        }
    }
    # The leading comma is load-bearing, not style: PowerShell auto-unwraps a
    # single-item collection into a bare scalar the instant it crosses a
    # `return`/pipeline boundary -- with exactly one folder found, callers
    # got back the STRING itself instead of a 1-item list. $allHits[0] on a
    # string indexes its first CHARACTER, not "the first item" -- silently
    # producing the drive letter alone ('C') as the install target. Real
    # report 2026-09-14: that 'C' then resolved relative to the elevated
    # process's System32 working directory, failing on
    # 'C:\WINDOWS\system32\C\DamageMeter.dll'. The comma forces this to stay
    # a real array for 0, 1, or many results -- verified against all three
    # counts before shipping this fix.
    return ,$found
}

# A single folder version of the search above, used by the manual
# folder-picker fallback (still only expects one match there).
function Find-ShinraFolder {
    param([string]$base)
    $hits = Find-ShinraFolders @($base)
    if ($hits.Count -eq 0) { return $null }
    $pref = $hits | Where-Object { (Split-Path $_ -Leaf) -ieq "ShinraMeter" } | Select-Object -First 1
    if ($pref) { return $pref }
    return $hits[0]
}

# Per-target check: is DamageMeter.dll in THIS specific folder actually
# locked right now? A process-name check (the old approach) only catches
# clients literally called TeraToolbox/tera-toolbox -- Crazy-eSports-
# ClassicPlus runs as "ShinraMeter"/"TERA"/"TERA Europe Classic+ Launcher"
# instead, so that check silently never fired for it. Probing the file
# itself works regardless of what the launcher is named.
function Test-FileLocked {
    param([string]$path)
    if (-not (Test-Path $path)) { return $false }
    try {
        $s = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $s.Close()
        return $false
    } catch { return $true }
}

Write-Host ""
Write-Host "  ============================================" -ForegroundColor Green
Write-Host "   ShinraMeter Rotation Patch - Installer" -ForegroundColor Green
Write-Host "  ============================================" -ForegroundColor Green
Write-Host ""
Write-Host "   IMPORTANT: close TeraToolbox (and any other meter client)" -ForegroundColor Yellow
Write-Host "   completely before continuing." -ForegroundColor Yellow
Write-Host ""

# Patch DLL lives in a 'release' subfolder, or next to this script.
$releaseDir = Join-Path $PSScriptRoot "release"
if (-not (Test-Path "$releaseDir\DamageMeter.dll")) { $releaseDir = $PSScriptRoot }
if (-not (Test-Path "$releaseDir\DamageMeter.dll")) {
    Write-Host "  ERROR: could not find DamageMeter.dll next to this script." -ForegroundColor Red
    Write-Host "  Make sure you extracted the WHOLE zip." -ForegroundColor Red
    Read-Host "  Press Enter to exit"; exit 1
}

# 1. Auto-detect every meter install on this PC, with confirm + folder
#    picker fallback when nothing (or more than one, see below) is found.
$common = @(
    "$env:USERPROFILE\Desktop\TeraToolbox","$env:USERPROFILE\Desktop\TeraToolbox Private",
    "$env:USERPROFILE\Documents\TeraToolbox","$env:USERPROFILE\Downloads\TeraToolbox",
    "${env:ProgramFiles(x86)}\TeraToolbox","$env:ProgramFiles\TeraToolbox",
    "C:\TeraToolbox","C:\TeraToolbox Private","D:\TeraToolbox",
    # Private-server launchers (Crazy-eSports-ClassicPlus and similar) tend
    # to install under AppData rather than any of the paths above.
    "$env:APPDATA","$env:LOCALAPPDATA"
)
Write-Host "  Scanning your PC for ShinraMeter installs (can take a minute, especially the AppData check)..." -ForegroundColor DarkGray
$allHits = Find-ShinraFolders $common

$targets = @()
if ($allHits.Count -eq 1) {
    $shinra = $allHits[0]
    Write-Host "  Found ShinraMeter at:" -ForegroundColor Cyan
    Write-Host "    $shinra"; Write-Host ""
    $ans = Read-Host "  Use this folder? Press Enter for yes, or type N to choose another"
    if ($ans -match '^[nN]') { $shinra = $null } else { $targets = @($shinra) }
} elseif ($allHits.Count -ge 2) {
    Write-Host "  Found more than one meter install on this PC:" -ForegroundColor Cyan
    for ($i = 0; $i -lt $allHits.Count; $i++) { Write-Host ("    [{0}] {1}" -f ($i + 1), $allHits[$i]) }
    Write-Host ""
    while ($targets.Count -eq 0) {
        $ans = Read-Host "  Type a number to patch just that one, or A to patch ALL of them"
        if ($ans -match '^[aA]$') { $targets = $allHits }
        elseif ($ans -match '^\d+$' -and [int]$ans -ge 1 -and [int]$ans -le $allHits.Count) { $targets = @($allHits[[int]$ans - 1]) }
        else { Write-Host "  Not a valid choice, try again." -ForegroundColor Red }
    }
}
if ($targets.Count -eq 0) {
    Write-Host ""
    Write-Host "  A window will open. Type or paste the path to your" -ForegroundColor Yellow
    Write-Host "  TeraToolbox folder (or the ShinraMeter folder itself)" -ForegroundColor Yellow
    Write-Host "  in the 'File name' box and press Enter, or browse to it." -ForegroundColor Yellow
    Start-Sleep -Milliseconds 400
    $picked = $null
    while (-not $picked) {
        $selected = Select-FolderDialog "Select your TeraToolbox folder (or the ShinraMeter folder)"
        if (-not $selected) {
            Write-Host "  Cancelled. Nothing was changed." -ForegroundColor Red
            Read-Host "  Press Enter to exit"; exit 1
        }
        $picked = Find-ShinraFolder $selected
        if (-not $picked) { Write-Host "  No ShinraMeter found there. Try again." -ForegroundColor Red }
    }
    Write-Host "  Using: $picked" -ForegroundColor Cyan
    $targets = @($picked)
}

# Installs the patch into one specific meter folder: picks the matching
# prebuilt DLL variant, backs up originals, copies the patch in, updates
# module.json/manifest.json if present. Returns $true/$false.
function Install-ToShinra {
    param([string]$shinra)

    Write-Host ""
    Write-Host "  --- $shinra ---" -ForegroundColor Cyan

    if (Test-FileLocked (Join-Path $shinra "DamageMeter.dll")) {
        Write-Host "  This meter's DamageMeter.dll is still open (client running)." -ForegroundColor Yellow
        Write-Host "  Close it completely, then press Enter to continue." -ForegroundColor Yellow
        Read-Host
    }

    # Pick the right patch build for this specific meter. The default
    # release/DamageMeter.dll is built against a stock TeraToolbox
    # ShinraMeter (references DamageMeter.Sniffing.ToolboxSniffer
    # internally). Some private server clients (e.g.
    # Crazy-eSports-ClassicPlus) ship a fork whose DamageMeter.Sniffing.dll
    # never defines that type at all -- installing the default build there
    # crashes on launch with a TypeLoadException. Detected with a plain
    # substring probe on their own Sniffing.dll (no .NET reflection needed,
    # works from Windows PowerShell against any target framework).
    $sourceDll = Join-Path $releaseDir "DamageMeter.dll"
    $classicPlusDll = Join-Path $releaseDir "DamageMeter.classicplus.dll"
    $sniffDll = Join-Path $shinra "DamageMeter.Sniffing.dll"
    if ((Test-Path $classicPlusDll) -and (Test-Path $sniffDll)) {
        try {
            $usesToolboxSniffer = [bool](Select-String -Path $sniffDll -Pattern "ToolboxSniffer" -SimpleMatch -Quiet)
            if (-not $usesToolboxSniffer) {
                $sourceDll = $classicPlusDll
                Write-Host "  Detected a Classic+ / Crazy-eSports style meter -- using the matching patch build." -ForegroundColor Cyan
            }
        } catch { } # any read error -> fall back to the default build
    }

    Write-Host "  Backing up original files..."
    foreach ($f in @("DamageMeter.dll","module.json","manifest.json")) {
        $src = Join-Path $shinra $f; $bak = "$src.prepatch.bak"
        if ((Test-Path $src) -and -not (Test-Path $bak)) { Copy-Item $src $bak -Force; Write-Host "    backed up: $f" }
    }

    # Clean any leftover from the old (external-DLL) version of this patch
    $oldDll = Join-Path $shinra "ShinraRotationPatch.dll"
    if (Test-Path $oldDll) { Remove-Item $oldDll -Force; Write-Host "    removed old ShinraRotationPatch.dll" }

    Write-Host "  Installing patched DamageMeter.dll..."
    try {
        Copy-Item $sourceDll (Join-Path $shinra "DamageMeter.dll") -Force -ErrorAction Stop
        Write-Host "    installed: DamageMeter.dll"
    } catch {
        $msg = $_.Exception.Message
        Write-Host ""
        if ($msg -match "being used|another process|0x80070020|in use") {
            Write-Host "   The client is still open and locking the file. Close it completely, then run this again." -ForegroundColor Red
        } elseif ($msg -match "denied|Unauthorized") {
            Write-Host "   Windows blocked writing. Right-click install.bat -> Run as administrator." -ForegroundColor Red
        } else { Write-Host "   $msg" -ForegroundColor Red }
        return $false
    }

    # Turn OFF "Export packets logs" (window.xml <packets_collect>). That option re-parses
    # the session's stored packets through the meter's live parser when a boss dies, which
    # swaps the protocol version under the running meter and makes it silently drop every
    # new buff/debuff until restart. The patched DLL already neutralizes the exporter; this
    # also fixes the setting itself so an unpatched meter (e.g. after uninstall) is safe too.
    # Safe to edit here: the client was required to be closed above, so the meter cannot
    # overwrite window.xml on exit.
    foreach ($cfgName in @("window.xml", "window_backup.xml")) {
        $cfg = Join-Path $shinra ("resources\config\" + $cfgName)
        if (-not (Test-Path $cfg)) { continue }
        try {
            $raw = [System.IO.File]::ReadAllText($cfg)
            if ($raw -match '<packets_collect>\s*true\s*</packets_collect>') {
                $cfgBak = "$cfg.prepatch.bak"
                if (-not (Test-Path $cfgBak)) { Copy-Item $cfg $cfgBak -Force }
                $raw = [regex]::Replace($raw, '<packets_collect>\s*true\s*</packets_collect>', '<packets_collect>false</packets_collect>')
                [System.IO.File]::WriteAllText($cfg, $raw, (New-Object System.Text.UTF8Encoding($false)))
                Write-Host "    turned off 'Export packets logs' in $cfgName (it breaks party buff tracking)"
            }
        } catch {
            Write-Host "    could not update $cfgName ($($_.Exception.Message)) - untick 'Export packets logs' in the meter settings manually" -ForegroundColor Yellow
        }
    }

    # Turn OFF auto-update in module.json (so the toolbox won't overwrite the patch)
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

    # Recompute manifest.json hash for the new DamageMeter.dll (toolbox validates this)
    $manPath = Join-Path $shinra "manifest.json"
    if (Test-Path $manPath) {
        try {
            $man = Get-Content $manPath -Raw | ConvertFrom-Json
            $newHash = (Get-FileHash (Join-Path $shinra "DamageMeter.dll") -Algorithm SHA256).Hash.ToLower()
            if ($man.files.PSObject.Properties.Name -contains 'DamageMeter.dll') { $man.files.'DamageMeter.dll' = $newHash }
            # drop stale entry from the old external-DLL version of this patch
            if ($man.files.PSObject.Properties.Name -contains 'ShinraRotationPatch.dll') {
                $man.files.PSObject.Properties.Remove('ShinraRotationPatch.dll')
            }
            $json = $man | ConvertTo-Json -Depth 30
            [System.IO.File]::WriteAllText($manPath, $json, (New-Object System.Text.UTF8Encoding($false)))
            Write-Host "    manifest updated for DamageMeter.dll"
        } catch { Write-Host "    WARNING: could not update manifest: $($_.Exception.Message)" -ForegroundColor Yellow }
    }

    return $true
}

$results = @{}
foreach ($t in $targets) { $results[$t] = Install-ToShinra $t }

Write-Host ""
Write-Host "  ============================================" -ForegroundColor Green
$anyFail = $false
foreach ($t in $targets) {
    if ($results[$t]) { Write-Host "   OK   $t" -ForegroundColor Green }
    else { Write-Host "   FAIL $t" -ForegroundColor Red; $anyFail = $true }
}
if (-not $anyFail) {
    Write-Host ""
    Write-Host "   Done! Start the client(s) to use the patch." -ForegroundColor Green
}
Write-Host ""
Write-Host "   Originals saved as *.prepatch.bak"
Write-Host "   To undo, run uninstall.bat"
Write-Host "  ============================================" -ForegroundColor Green
Write-Host ""
Read-Host "  Press Enter to exit"
