@echo off
setlocal EnableDelayedExpansion
title ShinraMeter Rotation Patch - Uninstaller
color 0C

echo.
echo  ============================================
echo   ShinraMeter Rotation Patch - Uninstaller
echo  ============================================
echo.

set "SHINRA="

:: Auto-detect: search common TeraToolbox locations for our backup
for %%D in (
    "%USERPROFILE%\Desktop\TeraToolbox"
    "%USERPROFILE%\Desktop\TeraToolbox Private"
    "%USERPROFILE%\Documents\TeraToolbox"
    "%USERPROFILE%\Downloads\TeraToolbox"
    "C:\TeraToolbox"
    "C:\TeraToolbox Private"
    "D:\TeraToolbox"
) do (
    if exist "%%~D" (
        call :resolve "%%~D"
        if defined SHINRA goto :found
    )
)

:ask
echo  Could not find the patch automatically.
echo.
echo  Drag your TeraToolbox folder onto this window, or type the path,
echo  then press Enter.
echo.
set /p "TBPATH=  Path: "
set "TBPATH=!TBPATH:"=!"
if "!TBPATH!"=="" goto :ask
if not exist "!TBPATH!" (
    echo.
    echo  That folder does not exist. Try again.
    echo.
    goto :ask
)
call :resolve "!TBPATH!"
if defined SHINRA goto :found
echo.
echo  Could not find a backup (DamageMeter.dll.prepatch.bak) in that folder.
echo  The patch may not be installed there.
echo.
goto :ask

:found
echo.
echo  Found patched ShinraMeter at:
echo    !SHINRA!
echo.

echo  Restoring original files from backups...
for %%F in (DamageMeter.dll ShinraMeter.deps.json module.json) do (
    if exist "!SHINRA!\%%F.prepatch.bak" (
        copy /Y "!SHINRA!\%%F.prepatch.bak" "!SHINRA!\%%F" >nul && echo    restored: %%F
    )
)

echo  Removing patch files...
if exist "!SHINRA!\ShinraRotationPatch.dll" (
    del "!SHINRA!\ShinraRotationPatch.dll" && echo    removed: ShinraRotationPatch.dll
)

echo  Removing backups...
for %%F in (DamageMeter.dll ShinraMeter.deps.json module.json) do (
    if exist "!SHINRA!\%%F.prepatch.bak" del "!SHINRA!\%%F.prepatch.bak" >nul 2>&1 && echo    removed: %%F.prepatch.bak
)

echo.
echo  ============================================
echo   Done! Your ShinraMeter is back to normal.
echo   Restart TeraToolbox to apply.
echo  ============================================
echo.
pause
exit /b

:: ---------------------------------------------------------------------------
:: :resolve - finds the patched ShinraMeter folder under a base dir by looking
::   for the backup file. Prefers a folder named exactly "ShinraMeter".
::   Sets SHINRA (no trailing backslash).
:: ---------------------------------------------------------------------------
:resolve
set "SHINRA="
for /f "delims=" %%F in ('dir /s /b "%~1\DamageMeter.dll.prepatch.bak" 2^>nul ^| findstr /i /e /c:"\\ShinraMeter\\DamageMeter.dll.prepatch.bak"') do (
    set "SHINRA=%%~dpF"
    goto :resolve_strip
)
for /f "delims=" %%F in ('dir /s /b "%~1\DamageMeter.dll.prepatch.bak" 2^>nul') do (
    set "SHINRA=%%~dpF"
    goto :resolve_strip
)
exit /b
:resolve_strip
if "!SHINRA:~-1!"=="\" set "SHINRA=!SHINRA:~0,-1!"
exit /b
