@echo off
REM CrossFill for .NET - one-click build and run on Windows
cd /d "%~dp0"
dotnet build src\CrossFill.Cli -c Release -v quiet || goto :error
set CROSSFILL=src\CrossFill.Cli\bin\Release\net11.0\crossfill.exe
if not exist "%LOCALAPPDATA%\ms-playwright" (
    echo Installing Chromium for Playwright...
    %CROSSFILL% playwright install chromium
)
start "" http://127.0.0.1:5000
%CROSSFILL% serve
goto :eof

:error
echo Build failed. Make sure the .NET 11 SDK is installed: https://dot.net
pause
