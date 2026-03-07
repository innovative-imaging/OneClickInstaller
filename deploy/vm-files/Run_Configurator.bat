@echo off
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

SET "SCRIPT_DIR=%~dp0"
SET "LOG_FILE=Z:\OS_ClosedLoop.log"

REM Install .NET Desktop Runtime 9.0 silently if not already present
REM echo Checking .NET Desktop Runtime...
REM dotnet --list-runtimes 2>nul | findstr /C:"Microsoft.WindowsDesktop.App 9.0" >nul
REM if %errorlevel% neq 0 (
    echo Installing .NET Desktop Runtime 9.0...
    "%SCRIPT_DIR%dotnet-sdk-9.0.308-win-x64.exe" /install /quiet /norestart
    echo .NET Runtime installed.
REM ) else (
REM    echo .NET Desktop Runtime 9.0 already installed.
REM )

echo Starting Eikon Configurator...
echo Log Path: %LOG_FILE%
"%SCRIPT_DIR%EikonConfigurator\EikonConfigurator.exe" --bypass-hardware-check --log-path "%LOG_FILE%"
PAUSE