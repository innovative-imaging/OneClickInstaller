@echo off

:KillServices
tasklist /fi "imagename eq GVPPro.ImageAcquisitionService.exe" |find ":" > nul
if errorlevel 1 taskkill /F /IM "GVPPro.ImageAcquisitionService.exe"
tasklist /fi "imagename eq GVPPro.ImageProcessingService.exe" |find ":" > nul
if errorlevel 1 taskkill /F /IM "GVPPro.ImageProcessingService.exe"
tasklist /fi "imagename eq GVPPro.ImageStore.Service.exe" |find ":" > nul
if errorlevel 1 taskkill /F /IM "GVPPro.ImageStore.Service.exe"

if /I "%1"=="Services" goto End

:KillGVPPro
tasklist /fi "imagename eq GVPPro.exe" |find ":" > nul
if errorlevel 1 taskkill /F /IM "GVPPro.exe"

:End
exit /b 0