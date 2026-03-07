@echo off
title Uninstall .NET Framework 4.8
setlocal enabledelayedexpansion

net session >nul 2>&1
if !errorlevel! neq 0 (
    echo Requesting administrator privileges...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b 0
)

echo ============================================
echo   Uninstall .NET Framework 4.8
echo ============================================
echo.

REM Try wusa first
echo Method 1: Trying wusa uninstall KB4486153...
wusa /uninstall /kb:4486153 /quiet /norestart
set WUSA_EXIT=!errorlevel!
echo wusa exit code: !WUSA_EXIT!
if !WUSA_EXIT!==0 goto DONE
if !WUSA_EXIT!==3010 goto DONE

echo wusa did not work. Trying next method...
echo.

REM Try finding the actual DISM package name dynamically
echo Method 2: Searching for .NET DISM packages...
dism /online /get-packages /format:table 2>nul | findstr /i "NetFx"
echo.

REM Try the control panel uninstall approach
echo Method 3: Trying via Windows Features...
DISM /Online /Disable-Feature /FeatureName:NetFx4 /Remove /NoRestart
echo DISM exit code: !errorlevel!

:DONE
echo.
echo Done. Please restart your computer.
pause
