@echo off
title OneClickInstaller - Bootstrapper
setlocal enabledelayedexpansion

REM --- Setup logging (append so we keep log across reboots) ---
set "LOGFILE=%~dp0install_bootstrap.log"
if not exist "%LOGFILE%" echo. > "%LOGFILE%"
call :LOG "====================================================="
call :LOG "  OneClickInstaller - Bootstrap Launcher"
call :LOG "  Started: %DATE% %TIME%"
call :LOG "====================================================="
call :LOG ""

set "NEED_RESTART=0"

echo.
echo ===================================================
echo    OneClickInstaller - Bootstrap Launcher
echo ===================================================
echo.
echo Log file: %LOGFILE%
echo.

REM --- Check for Administrator privileges, auto-elevate if not ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    call :LOG "Not running as Administrator. Auto-elevating..."
    echo Requesting administrator privileges...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs" >nul 2>&1
    exit /b 0
)

call :LOG "Running with Administrator privileges."
echo Running with Administrator privileges.
echo.

REM =========================================================
REM  Step 1: Install .NET Framework 3.5 SP1
REM =========================================================
call :LOG "[Step 1] Checking / Installing .NET Framework 3.5 SP1..."
echo [Step 1] Checking / Installing .NET Framework 3.5 SP1...
echo.

REM Check if .NET 3.5 is already installed
set "DOTNET35_INSTALLED=0"
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5" /v Install >nul 2>&1
if !errorlevel!==0 (
    for /f "tokens=3" %%a in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5" /v Install 2^>nul ^| findstr /i "Install"') do set DOTNET35_INSTALLED=%%a
)
if "!DOTNET35_INSTALLED!"=="1" (
    call :LOG ".NET Framework 3.5 is already installed. Skipping."
    echo .NET Framework 3.5 is already installed. Skipping.
    echo.
    goto STEP2_DOTNET48
)

REM --- Verify sxs source folder exists ---
set "DOTNET35_SXS=%~dp0SW\dotnetfx35\sxs"
call :LOG "DISM source path: !DOTNET35_SXS!"

if not exist "!DOTNET35_SXS!" (
    call :LOG "ERROR: .NET 3.5 sxs folder not found at: !DOTNET35_SXS!"
    echo ERROR: sxs folder not found at: !DOTNET35_SXS!
    echo Please copy the sxs folder from your Windows ISO sources folder.
    exit /b 1
)

call :LOG ".NET Framework 3.5 not installed. Installing via DISM..."
echo .NET Framework 3.5 not found. Installing silently via DISM...
echo This may take several minutes. Please wait...
echo.
call :LOG "DISM installation started at: %TIME%"

Dism.exe /online /enable-feature /featurename:NetFX3 /All /NoRestart /Source:"!DOTNET35_SXS!" /LimitAccess
set DOTNET35_EXIT=!errorlevel!

call :LOG "DISM installation finished at: %TIME%"
call :LOG "DISM .NET 3.5 exit code: !DOTNET35_EXIT!"

if !DOTNET35_EXIT!==0 (
    call :LOG "SUCCESS: .NET Framework 3.5 installed successfully."
    echo .NET Framework 3.5 installed successfully.
) else if !DOTNET35_EXIT!==3010 (
    call :LOG "SUCCESS: .NET Framework 3.5 installed. Restart required."
    echo .NET Framework 3.5 installed. Restart will be needed.
    set "NEED_RESTART=1"
) else if !DOTNET35_EXIT!==1058 (
    call :LOG "ERROR: Windows Update service is disabled. DISM could not complete."
    echo ERROR: Windows Update service is disabled. Please enable it and retry.
    exit /b 1
) else (
    call :LOG "WARNING: DISM returned unexpected exit code: !DOTNET35_EXIT!. Continuing..."
    echo WARNING: DISM returned exit code: !DOTNET35_EXIT!
)
echo.

REM =========================================================
REM  Step 2: Install .NET Framework 4.8
REM =========================================================
:STEP2_DOTNET48
call :LOG "[Step 2] Checking / Installing .NET Framework 4.8..."
echo [Step 2] Checking / Installing .NET Framework 4.8...
echo.

REM Check if .NET 4.8 is already installed (release key >= 528040)
set DOTNET_RELEASE=0
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release >nul 2>&1
if %errorlevel% neq 0 (
    call :LOG ".NET 4 registry key not found."
    goto INSTALL_DOTNET48
)

reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release > "%TEMP%\dotnet_check.txt" 2>&1
call :LOG "Registry query output:"
type "%TEMP%\dotnet_check.txt" >> "%LOGFILE%"
for /f "tokens=3" %%a in ('findstr /i "Release" "%TEMP%\dotnet_check.txt"') do set DOTNET_RELEASE=%%a
del "%TEMP%\dotnet_check.txt" >nul 2>&1
call :LOG "Detected .NET release key: !DOTNET_RELEASE!"

if !DOTNET_RELEASE! GEQ 528040 (
    call :LOG ".NET Framework 4.8 already installed. Skipping."
    echo .NET Framework 4.8 is already installed. Skipping.
    echo.
    reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" /v OneClickInstaller /f >nul 2>&1
    goto CHECK_REBOOT
)

:INSTALL_DOTNET48
call :LOG ".NET Framework 4.8 not found or outdated. Installing..."
echo .NET Framework 4.8 not found. Installing silently...
echo This may take several minutes. Please wait...
echo.

set "DOTNET_INSTALLER=%~dp0SW\.NET4.8\NDP48-x86-x64-AllOS-ENU.exe"
call :LOG "Installer path: !DOTNET_INSTALLER!"

if not exist "!DOTNET_INSTALLER!" (
    call :LOG "ERROR: .NET 4.8 installer not found at: !DOTNET_INSTALLER!"
    echo ERROR: .NET 4.8 installer not found.
    exit /b 1
)

call :LOG "Launching .NET 4.8 installer..."
call :LOG "Installation started at: %TIME%"

"!DOTNET_INSTALLER!" /q /norestart
set DOTNET_EXIT=!errorlevel!

call :LOG "Installation finished at: %TIME%"
call :LOG ".NET 4.8 installer exit code: !DOTNET_EXIT!"

if !DOTNET_EXIT!==0 (
    call :LOG "SUCCESS: .NET Framework 4.8 installed successfully."
    echo .NET Framework 4.8 installed successfully.
) else if !DOTNET_EXIT!==1641 (
    call :LOG "SUCCESS: .NET Framework 4.8 installed. Restart required."
    echo .NET Framework 4.8 installed. Restart will be needed.
    set "NEED_RESTART=1"
) else if !DOTNET_EXIT!==3010 (
    call :LOG "SUCCESS: .NET Framework 4.8 installed. Restart required."
    echo .NET Framework 4.8 installed. Restart will be needed.
    set "NEED_RESTART=1"
) else if !DOTNET_EXIT!==5100 (
    call :LOG "INFO: .NET 4.8 or newer already installed."
    echo .NET Framework 4.8 or newer is already installed.
) else (
    call :LOG "WARNING: Unexpected exit code: !DOTNET_EXIT!. Continuing..."
    echo WARNING: .NET 4.8 returned exit code: !DOTNET_EXIT!
)
echo.

REM =========================================================
REM  Check if reboot is needed (for either .NET install)
REM =========================================================
:CHECK_REBOOT
if "!NEED_RESTART!"=="1" (
    call :LOG ""
    call :LOG "REBOOT REQUIRED: .NET installations need a restart."
    call :LOG "Registering INSTALL.bat in RunOnce to auto-resume after reboot..."
    echo.
    echo ===================================================
    echo   REBOOTING - .NET installations require a restart.
    echo   Installation will resume automatically after reboot.
    echo ===================================================
    echo.

    set "BAT_PATH=%~f0"
    reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" /v OneClickInstaller /t REG_SZ /d "cmd /c powershell -Command \"Start-Process cmd -ArgumentList '/c \"\"!BAT_PATH!\"\"' -Verb RunAs\"" /f >nul 2>&1
    if !errorlevel!==0 (
        call :LOG "RunOnce registry key set successfully."
    ) else (
        call :LOG "WARNING: Failed to set RunOnce key."
    )

    call :LOG "Rebooting system in 15 seconds..."
    echo Rebooting in 15 seconds...
    shutdown /r /t 15 /c "Restarting to complete .NET Framework installation. OneClickInstaller will resume automatically."
    exit /b 0
)

REM =========================================================
REM  Step 3: Launch OneClickInstaller
REM =========================================================
:LAUNCH_INSTALLER
call :LOG ""
call :LOG "[Step 3] Launching OneClickInstaller..."
echo [Step 3] Launching OneClickInstaller...
echo.

if not exist "%~dp0OneClickInstaller.exe" (
    call :LOG "ERROR: OneClickInstaller.exe not found at: %~dp0OneClickInstaller.exe"
    echo ERROR: OneClickInstaller.exe not found.
    exit /b 1
)

call :LOG "Launching: %~dp0OneClickInstaller.exe"
start "" "%~dp0OneClickInstaller.exe"
call :LOG "OneClickInstaller launched at: %TIME%"
echo OneClickInstaller launched.
echo.
echo Log saved to: %LOGFILE%
exit /b 0

REM --- Logging subroutine ---
:LOG
echo %~1
echo [%TIME%] %~1 >> "%LOGFILE%"
goto :eof