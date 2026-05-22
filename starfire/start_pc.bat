@echo off
setlocal
cd /d "%~dp0"
echo ===================================
echo   STARFIRE COGITATOR PRIME
echo   INITIALIZING...
echo ===================================
python discovery.py
python server.py
endlocal
