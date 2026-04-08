@echo off
setlocal

:: Get the folder where THIS batch file is located
set "dir=%~dp0"
if "%dir:~-1%"=="\" set "dir=%dir:~0,-1%"

:: Get the name of that folder
for %%I in ("%dir%") do set "name=%%~nxI"

:: Only proceed if the folder is named "LatticeVeil"
if /i "%name%" neq "LatticeVeil" (
    echo.
    echo ERROR: This script must be placed and run from INSIDE a folder named "LatticeVeil".
    echo Current folder: %dir%
    echo.
    pause
    exit /b
)

echo.
echo FINAL CLEANUP: Deleting entire LatticeVeil folder and all contents...
echo.

:: Delete the entire folder (including this batch file)
rd /s /q "%dir%" 2>nul

exit