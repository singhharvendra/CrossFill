@echo off
REM CrossFill - one-click setup and run on Windows
cd /d "%~dp0"
if not exist .venv (
    echo Creating virtual environment...
    python -m venv .venv || goto :error
    call .venv\Scripts\activate.bat
    python -m pip install -e ".[web]" || goto :error
    playwright install chromium
) else (
    call .venv\Scripts\activate.bat
)
start "" http://127.0.0.1:5000
crossfill serve
goto :eof

:error
echo Setup failed. Make sure Python 3.9+ is installed and on PATH.
pause
