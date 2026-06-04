@echo off
setlocal EnableDelayedExpansion
title ShinraMeter Rotation Patch - Uninstaller
color 0C

echo.
echo  ============================================
echo   ShinraMeter Rotation Patch - Uninstaller
echo  ============================================
echo.

:: Try to auto-detect TeraToolbox location
set "FOUND="
for %%D in (
    "%USERPROFILE%\Desktop\TeraToolbox"
    "%USERPROFILE%\Desktop\TeraToolbox Private"
    "%USERPROFILE%\Documents\TeraToolbox"
    "C:\TeraToolbox"
    "D:\TeraToolbox"
) do (
    if exist "%%~D\mods\ShinraMeter\DamageMeter.dll" (
        set "FOUND=%%~D"
        goto :found
    )
)

:ask
echo  Could not auto-detect TeraToolbox folder.
echo  Enter the full path to your TeraToolbox folder:
echo.
set /p "TBPATH=  Path: "
if not exist "!TBPATH!\mods\ShinraMeter\DamageMeter.dll" (
    echo.
    echo  ERROR: DamageMeter.dll not found in !TBPATH!\mods\ShinraMeter\
    echo.
    goto :ask
)
set "FOUND=!TBPATH!"

:found
set "SHINRA=!FOUND!\mods\ShinraMeter"
echo.
echo  Found ShinraMeter at:
echo    !SHINRA!
echo.

:: Check backups exist
set "MISSING="
for %%F in (DamageMeter.dll ShinraMeter.deps.json module.json) do (
    if not exist "!SHINRA!\%%F.prepatch.bak" set "MISSING=!MISSING! %%F.prepatch.bak"
)
if not "!MISSING!"=="" (
    echo  ERROR: These backup files were not found:
    echo   !MISSING!
    echo.
    echo  Cannot restore without the original backups.
    echo  If you no longer have them, reinstall ShinraMeter manually.
    echo.
    pause
    exit /b 1
)

echo  Restoring original files from backups...
for %%F in (DamageMeter.dll ShinraMeter.deps.json module.json) do (
    copy /Y "!SHINRA!\%%F.prepatch.bak" "!SHINRA!\%%F" >nul && echo    restored: %%F
)

echo  Removing patch files...
if exist "!SHINRA!\ShinraRotationPatch.dll" (
    del "!SHINRA!\ShinraRotationPatch.dll" && echo    removed: ShinraRotationPatch.dll
)

echo  Removing backups...
for %%F in (DamageMeter.dll ShinraMeter.deps.json module.json) do (
    del "!SHINRA!\%%F.prepatch.bak" >nul 2>&1 && echo    removed: %%F.prepatch.bak
)

echo.
echo  ============================================
echo   Done! Your ShinraMeter is back to normal.
echo   Restart TeraToolbox to apply.
echo  ============================================
echo.
pause
