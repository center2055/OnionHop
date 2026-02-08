@echo off
setlocal

cd /d "%~dp0"

set "PORT=5173"
if not "%~1"=="" set "PORT=%~1"

echo.
echo OnionHop website server
echo ----------------------
echo Serving: %cd%
echo URL: http://localhost:%PORT%/
echo.

where python >nul 2>nul
if %errorlevel%==0 (
  start "" "http://localhost:%PORT%/"
  python server.py %PORT%
  exit /b %errorlevel%
)

where py >nul 2>nul
if %errorlevel%==0 (
  start "" "http://localhost:%PORT%/"
  py -3 server.py %PORT%
  exit /b %errorlevel%
)

echo ERROR: Python not found on PATH (python/py).
echo Install Python from https://www.python.org/ or enable the "py" launcher.
echo.
pause
exit /b 1

