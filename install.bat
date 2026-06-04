@echo off
setlocal EnableDelayedExpansion
title ShinraMeter Rotation Patch - Installer
color 0A

echo.
echo  ============================================
echo   ShinraMeter Rotation Patch - Installer
echo  ============================================
echo.
echo   IMPORTANT: Close TeraToolbox completely before continuing,
echo   otherwise the meter files cannot be replaced.
echo.
pause
echo.

set "SHINRA="

:: Auto-detect: search common TeraToolbox locations
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
echo  Could not find ShinraMeter automatically.
echo.
echo  Drag your TeraToolbox folder onto this window, or type the path,
echo  then press Enter.
echo  (example: C:\Users\YourName\Desktop\TeraToolbox)
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
echo  Could not find DamageMeter.dll inside that folder.
echo  Make sure ShinraMeter is installed in your TeraToolbox.
echo.
goto :ask

:found
echo.
echo  Found ShinraMeter at:
echo    !SHINRA!
echo.

echo  Backing up original files...
for %%F in (DamageMeter.dll ShinraMeter.deps.json module.json) do (
    if exist "!SHINRA!\%%F" (
        if not exist "!SHINRA!\%%F.prepatch.bak" (
            copy /Y "!SHINRA!\%%F" "!SHINRA!\%%F.prepatch.bak" >nul
            echo    backed up: %%F
        ) else (
            echo    backup already exists: %%F
        )
    )
)
echo.

echo  Installing patch files...
set "RELEASE=%~dp0release"

:: DamageMeter.dll is the critical one - if it's locked, the toolbox is still open
copy /Y "!RELEASE!\DamageMeter.dll" "!SHINRA!\DamageMeter.dll" >nul 2>&1
if errorlevel 1 (
    echo.
    echo  ============================================
    echo   ERROR: Could not replace DamageMeter.dll
    echo.
    echo   TeraToolbox is still running and locking the file.
    echo   Close TeraToolbox completely (check the system tray),
    echo   then run this installer again.
    echo  ============================================
    echo.
    pause
    exit /b 1
)
echo    installed: DamageMeter.dll
copy /Y "!RELEASE!\ShinraRotationPatch.dll"  "!SHINRA!\ShinraRotationPatch.dll"  >nul && echo    installed: ShinraRotationPatch.dll
copy /Y "!RELEASE!\ShinraMeter.deps.json"    "!SHINRA!\ShinraMeter.deps.json"    >nul && echo    installed: ShinraMeter.deps.json
copy /Y "!RELEASE!\module.json"              "!SHINRA!\module.json"              >nul && echo    installed: module.json

echo.
echo  ============================================
echo   Done! Start TeraToolbox to use the patch.
echo.
echo   Your originals are saved as *.prepatch.bak
echo   To undo, run uninstall.bat
echo  ============================================
echo.
pause
exit /b

:: ---------------------------------------------------------------------------
:: :resolve - finds the ShinraMeter folder under a base dir.
::   Prefers a folder named exactly "ShinraMeter"; falls back to any folder
::   that contains DamageMeter.dll. Sets SHINRA (no trailing backslash).
:: ---------------------------------------------------------------------------
:resolve
set "SHINRA="
for /f "delims=" %%F in ('dir /s /b "%~1\DamageMeter.dll" 2^>nul ^| findstr /i /e /c:"\\ShinraMeter\\DamageMeter.dll"') do (
    set "SHINRA=%%~dpF"
    goto :resolve_strip
)
for /f "delims=" %%F in ('dir /s /b "%~1\DamageMeter.dll" 2^>nul') do (
    set "SHINRA=%%~dpF"
    goto :resolve_strip
)
exit /b
:resolve_strip
if "!SHINRA:~-1!"=="\" set "SHINRA=!SHINRA:~0,-1!"
exit /b
