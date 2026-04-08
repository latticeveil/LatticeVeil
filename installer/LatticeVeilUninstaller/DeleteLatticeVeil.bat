@echo off
setlocal

set "target=%~1"

if not defined target set "target=%~dp0"
if "%target:~-1%"=="\" set "target=%target:~0,-1%"

if not defined target (
    exit /b
)

if "%target%"=="" exit /b
if "%target:~1,2%"==":\" if "%target:~3%"=="" exit /b

for %%I in ("%target%") do (
    set "target_parent=%%~dpI"
    set "target_name=%%~nxI"
)

if not defined target_parent exit /b
if "%target_parent:~-1%"=="\" set "target_parent=%target_parent:~0,-1%"

timeout /t 2 >nul
pushd "%target_parent%" >nul 2>&1
rd /s /q "%target_name%" 2>nul
popd >nul 2>&1

exit
