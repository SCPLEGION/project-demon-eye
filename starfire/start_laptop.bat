@echo off
setlocal
cd /d "%~dp0"
echo ===================================
echo   STARFIRE RELAY NODE
echo   INITIALIZING...
echo ===================================
adb reverse tcp:8765 tcp:8765
if errorlevel 1 (
    echo WARNING: adb reverse failed -- check that Quest is plugged in and authorized
) else (
    echo ADB TUNNEL: ESTABLISHED
)
python discovery.py
python relay.py
endlocal
