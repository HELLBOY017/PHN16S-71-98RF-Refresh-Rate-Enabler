@echo off
setlocal
cd /d "%~dp0"
echo ======================================================================
echo  Staging Refresh Rates for NVIDIA RTX 5060 Laptop GPU
echo ======================================================================
echo.

taskkill /f /im "PHN16S-71-98RF Refresh Rate Enabler.exe" 2>nul
copy /y "dist\PHN16S-71-98RF Refresh Rate Enabler.exe" ".\PHN16S-71-98RF Refresh Rate Enabler.exe" >nul 2>&1

".\dist\PHN16S-71-98RF Refresh Rate Enabler.exe" --stage --force --restart
if %ERRORLEVEL% EQU 0 (
    echo.
    echo ======================================================================
    echo  [SUCCESS] Refresh rates successfully staged and driver reloaded!
    echo  Check Windows Settings ^> System ^> Display ^> Advanced display.
    echo ======================================================================
) else (
    echo.
    echo ======================================================================
    echo  [ERROR] Staging failed or administrator approval was declined.
    echo ======================================================================
)
echo.
pause
