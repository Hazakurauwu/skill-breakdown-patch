@echo off
setlocal EnableDelayedExpansion
title ShinraMeter Rotation Patch Installer
color 0A

echo.
echo  ============================================
echo   ShinraMeter Rotation Patch - Installer
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
echo  (example: C:\Users\YourName\Desktop\TeraToolbox)
echo.
set /p "TBPATH=  Path: "
if not exist "!TBPATH!\mods\ShinraMeter\DamageMeter.dll" (
    echo.
    echo  ERROR: DamageMeter.dll not found in !TBPATH!\mods\ShinraMeter\
    echo  Make sure you entered the TeraToolbox root folder.
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

:: Backup originals
echo  Backing up original files...
for %%F in (DamageMeter.dll ShinraMeter.deps.json module.json) do (
    if exist "!SHINRA!\%%F" (
        copy /Y "!SHINRA!\%%F" "!SHINRA!\%%F.prepatch.bak" >nul
        echo    backed up: %%F
    )
)
echo.

:: Copy patch files
echo  Installing patch files...
set "RELEASE=%~dp0release"
copy /Y "!RELEASE!\DamageMeter.dll"          "!SHINRA!\DamageMeter.dll"          >nul && echo    installed: DamageMeter.dll (patched)
copy /Y "!RELEASE!\ShinraRotationPatch.dll"  "!SHINRA!\ShinraRotationPatch.dll"  >nul && echo    installed: ShinraRotationPatch.dll
copy /Y "!RELEASE!\ShinraMeter.deps.json"    "!SHINRA!\ShinraMeter.deps.json"    >nul && echo    installed: ShinraMeter.deps.json
copy /Y "!RELEASE!\module.json"              "!SHINRA!\module.json"              >nul && echo    installed: module.json (auto-update disabled)

echo.
echo  ============================================
echo   Done! Restart TeraToolbox to apply.
echo.
echo   Your originals are saved as *.prepatch.bak
echo   To uninstall: copy the .bak files back and
echo   delete ShinraRotationPatch.dll
echo  ============================================
echo.
pause
