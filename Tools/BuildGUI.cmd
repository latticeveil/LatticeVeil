@echo off
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"
set "BUILD_GUI=%SCRIPT_DIR%BuildGUI.ps1"
set "LOG_DIR=%REPO_ROOT%\.builder\logs"
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"
>> "%LOG_DIR%\BuildGUI-launch.log" echo [%date% %time%] CMD COMMAND: powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%BUILD_GUI%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%BUILD_GUI%"
>> "%LOG_DIR%\BuildGUI-launch.log" echo [%date% %time%] CMD EXIT CODE: %ERRORLEVEL%
