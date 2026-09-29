@echo off
setlocal
if exist "%~dp0dist\APS.exe" (
    start "" "%~dp0dist\APS.exe"
) else (
    echo Building first...
    call "%~dp0build.cmd"
    if exist "%~dp0dist\APS.exe" (
        start "" "%~dp0dist\APS.exe"
    )
)
endlocal
