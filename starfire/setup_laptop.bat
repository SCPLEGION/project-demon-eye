@echo off
setlocal
echo ============================================
echo  STARFIRE RELAY NODE -- SETUP
echo ============================================

python --version >NUL 2>&1
if errorlevel 1 (
    echo ERROR: python not found on PATH
    exit /b 1
)

echo [1/2] Installing Python packages...
python -m pip install --upgrade pip
python -m pip install websockets zeroconf psutil

echo [2/2] Probing ADB...
adb version >NUL 2>&1
if errorlevel 1 (
    echo   WARNING: adb not found on PATH -- install platform-tools and add to PATH
) else (
    adb devices
)

echo.
echo RELAY NODE -- SETUP COMPLETE
endlocal
