@echo off
setlocal
rem Local developer build. This is NOT how releases are made -- those are built by
rem .github/workflows/release.yml from a version tag. dist\ is gitignored.
rem
rem   build.cmd            fast framework-dependent build (needs .NET 8 installed)
rem   build.cmd selfcontained   what the release actually ships (~68 MB, slow)

rem EnableCompressionInSingleFile is a hard error (NETSDK1176) unless the publish is
rem self-contained, so it is passed here rather than set in APS.csproj.
set "SC=false"
set "LABEL=framework-dependent"
set "COMPRESS="
if /i "%~1"=="selfcontained" (
    set "SC=true"
    set "LABEL=self-contained"
    set "COMPRESS=-p:EnableCompressionInSingleFile=true"
)

echo ========================================================
echo Building APS - Audio Profile System (%LABEL%)
echo ========================================================
cd /d "%~dp0\src"
dotnet publish -c Release -r win-x64 --self-contained %SC% -p:PublishSingleFile=true %COMPRESS% -o "%~dp0\dist"
if %ERRORLEVEL% equ 0 (
    echo.
    echo Build Successful! Binary published to:
    echo %~dp0dist\APS.exe
) else (
    echo.
    echo Build Failed!
)
endlocal
