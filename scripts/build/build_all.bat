@echo off
setlocal enabledelayedexpansion
echo ============================================================
echo   OneClickInstaller + EikonConfigurator - Clean Build
echo   Produces: Production + VM packages
echo ============================================================
echo.

set "ERRORS=0"
REM Resolve repository root (this script is in scripts\build\)
set "ROOT=%~dp0..\..\"
set "TEMPLATE=%ROOT%deploy\package-template"
set "CONFIGDEPLOY=%ROOT%config\deploy"

REM ---- Find MSBuild ----
set "MSBUILD_PATH="
for %%E in (Community Professional Enterprise) do (
    if exist "C:\Program Files\Microsoft Visual Studio\2022\%%E\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=C:\Program Files\Microsoft Visual Studio\2022\%%E\MSBuild\Current\Bin\MSBuild.exe"
    )
)
if "%MSBUILD_PATH%"=="" (
    echo ERROR: MSBuild not found. Install Visual Studio 2022.
    exit /b 1
)
echo [OK] MSBuild: %MSBUILD_PATH%

REM ---- Find dotnet ----
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: dotnet SDK not found on PATH.
    exit /b 1
)
echo [OK] dotnet SDK found
echo.

REM ============================================================
REM  Step 1: Clean + Build OneClickInstaller (.NET Framework 4.8)
REM ============================================================
echo [1/4] Building OneClickInstaller...
echo      Cleaning...
"%MSBUILD_PATH%" "%ROOT%src\OneClickInstaller\OneClickInstaller.csproj" /p:Configuration=Release /t:Clean /v:q >nul 2>&1

echo      Building...
"%MSBUILD_PATH%" "%ROOT%src\OneClickInstaller\OneClickInstaller.csproj" /p:Configuration=Release /v:q
if %errorlevel% neq 0 (
    echo      !! BUILD FAILED: OneClickInstaller
    set "ERRORS=1"
    goto :check
)
echo      [OK] OneClickInstaller.exe built
echo.

REM ============================================================
REM  Step 2: Clean + Publish EikonConfigurator (.NET 8, win-x64)
REM ============================================================
echo [2/4] Building EikonConfigurator...
set "EIKON_SRC=%ROOT%src\EikonConfigurator"
set "EIKON_OUT=%EIKON_SRC%\bin\publish"

echo      Cleaning...
if exist "%EIKON_OUT%" rmdir /s /q "%EIKON_OUT%"
dotnet clean "%EIKON_SRC%\EikonConfigurator.csproj" -c Release >nul 2>&1

echo      Publishing (self-contained, win-x64)...
dotnet publish "%EIKON_SRC%\EikonConfigurator.csproj" -c Release -r win-x64 --self-contained -o "%EIKON_OUT%"
if %errorlevel% neq 0 (
    echo      !! BUILD FAILED: EikonConfigurator
    set "ERRORS=1"
    goto :check
)
echo      [OK] EikonConfigurator published
echo.

REM ============================================================
REM  Step 3: Assemble Production package
REM ============================================================
echo [3/4] Assembling Production package...
set "PKG_PROD=%ROOT%deploy\output\OneClickInstaller_Production"
if exist "%PKG_PROD%" rmdir /s /q "%PKG_PROD%"
mkdir "%PKG_PROD%"

REM Copy template files
copy /y "%TEMPLATE%\INSTALL.bat" "%PKG_PROD%\" >nul
copy /y "%TEMPLATE%\UNINSTALL_DOTNET48.bat" "%PKG_PROD%\" >nul
copy /y "%TEMPLATE%\README.txt" "%PKG_PROD%\" >nul
copy /y "%TEMPLATE%\OneClickInstaller.exe.config" "%PKG_PROD%\" >nul

REM Copy build artifacts
copy /y "%ROOT%src\OneClickInstaller\bin\Release\OneClickInstaller.exe" "%PKG_PROD%\" >nul

REM Copy production config as installer-config.json
copy /y "%CONFIGDEPLOY%\installer-config.production.json" "%PKG_PROD%\installer-config.json" >nul

REM Copy EikonConfigurator
mkdir "%PKG_PROD%\EikonConfigurator"
xcopy /s /y /q "%EIKON_OUT%\*" "%PKG_PROD%\EikonConfigurator\" >nul
copy /y "%CONFIGDEPLOY%\hardware-config.production.json" "%PKG_PROD%\EikonConfigurator\hardware-config.json" >nul

REM Create SW placeholder
mkdir "%PKG_PROD%\SW"
echo Copy the software installer media into this SW\ folder before deployment. > "%PKG_PROD%\SW\_COPY_INSTALLERS_HERE.txt"

echo      [OK] Production package assembled
echo.

REM ============================================================
REM  Step 4: Assemble VM package
REM ============================================================
echo [4/4] Assembling VM package...
set "PKG_VM=%ROOT%deploy\output\OneClickInstaller_VM"
if exist "%PKG_VM%" rmdir /s /q "%PKG_VM%"
mkdir "%PKG_VM%"

REM Copy template files
copy /y "%TEMPLATE%\INSTALL.bat" "%PKG_VM%\" >nul
copy /y "%TEMPLATE%\UNINSTALL_DOTNET48.bat" "%PKG_VM%\" >nul
copy /y "%TEMPLATE%\README.txt" "%PKG_VM%\" >nul
copy /y "%TEMPLATE%\OneClickInstaller.exe.config" "%PKG_VM%\" >nul

REM Copy build artifacts
copy /y "%ROOT%src\OneClickInstaller\bin\Release\OneClickInstaller.exe" "%PKG_VM%\" >nul

REM Copy VM config as installer-config.json
copy /y "%CONFIGDEPLOY%\installer-config.vm.json" "%PKG_VM%\installer-config.json" >nul

REM Copy EikonConfigurator
mkdir "%PKG_VM%\EikonConfigurator"
xcopy /s /y /q "%EIKON_OUT%\*" "%PKG_VM%\EikonConfigurator\" >nul
copy /y "%CONFIGDEPLOY%\hardware-config.vm.json" "%PKG_VM%\EikonConfigurator\hardware-config.json" >nul

REM Create SW placeholder
mkdir "%PKG_VM%\SW"
echo Copy the software installer media into this SW\ folder before deployment. > "%PKG_VM%\SW\_COPY_INSTALLERS_HERE.txt"

echo      [OK] VM package assembled
echo.

:check
echo.
echo ============================================================
if %ERRORS% equ 0 (
    echo   BUILD SUCCESSFUL - Both packages ready
    echo   Production: %PKG_PROD%
    echo   VM:         %PKG_VM%
) else (
    echo   BUILD FAILED - Check errors above
)
echo ============================================================
echo.
pause
