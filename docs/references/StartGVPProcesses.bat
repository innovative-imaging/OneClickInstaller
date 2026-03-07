@echo off
REM ============================================
REM GVP-Pro Startup Script
REM OpenMP Optimized for 1024x1024 16-bit Images
REM Intel i5-10400 Processor with AVX2
REM ============================================

:START
echo Starting GVP-Pro service...

REM Kill existing processes
tasklist /fi "imagename eq GVPPro.exe" |find ":" > nul
if errorlevel 1 taskkill /F /IM "GVPPro.exe"
tasklist /fi "imagename eq GVPPro.ImageAcquisitionService.exe" |find ":" > nul
if errorlevel 1 taskkill /F /IM "GVPPro.ImageAcquisitionService.exe"
tasklist /fi "imagename eq GVPPro.ImageProcessingService.exe" |find ":" > nul
if errorlevel 1 taskkill /F /IM "GVPPro.ImageProcessingService.exe"
tasklist /fi "imagename eq GVPPro.ImageStore.Service.exe" |find ":" > nul
if errorlevel 1 taskkill /F /IM "GVPPro.ImageStore.Service.exe"

REM Wait for processes to terminate
echo Waiting for processes to terminate...
ping 127.0.0.1 -n 3 -w 1000 >nul

REM Change directory to the executables' folder
cd /d D:\GVP-Pro\App\

REM Start Image Acquisition Service (no OpenMP optimization)
echo Starting Image Acquisition Service...
start "" /min cmd /c "GVPPro.ImageAcquisitionService.exe"

REM Wait before starting next service
ping 127.0.0.1 -n 2 -w 1000 >nul

echo Starting Image Processing Service...
start "" /min cmd /c "call run_imageprocessing.bat realtime"

REM Wait before starting main application
ping 127.0.0.1 -n 2 -w 1000 >nul

REM Start main GVP-Pro application
echo Starting GVP-Pro main application...
start "" "GVPPro.exe"

echo All services started successfully!

:MONITOR_LOOP
timeout /t 5 /nobreak >nul

REM Check Main App
tasklist /FI "IMAGENAME eq GVPPro.exe" 2>NUL | find /I /N "GVPPro.exe">NUL
if "%ERRORLEVEL%"=="1" (
    echo [Watchdog] GVPPro crashed or closed. Restarting all...
    goto START
)

REM Check Acquisition Service
tasklist /FI "IMAGENAME eq GVPPro.ImageAcquisitionService.exe" 2>NUL | find /I /N "GVPPro.ImageAcquisitionService.exe">NUL
if "%ERRORLEVEL%"=="1" (
    echo [Watchdog] Acquisition Service died. Restarting all...
    goto START
)

REM Check Processing Service (Assuming the bat launches this exe)
tasklist /FI "IMAGENAME eq GVPPro.ImageProcessingService.exe" 2>NUL | find /I /N "GVPPro.ImageProcessingService.exe">NUL
if "%ERRORLEVEL%"=="1" (
    echo [Watchdog] Processing Service died. Restarting all...
    goto START
)

goto MONITOR_LOOP