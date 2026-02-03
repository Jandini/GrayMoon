@echo off
setlocal enabledelayedexpansion

REM Ensure GitVersion.Tool is available and get SemVer
for /f "delims=" %%i in ('dotnet gitversion /output json /showvariable SemVer') do set GITVERSION_SEMVER=%%i

if "%GITVERSION_SEMVER%"=="" set GITVERSION_SEMVER=0.0.0

echo Building with version %GITVERSION_SEMVER%

wsl docker build -t jandini/graymoon:latest --build-arg VERSION=%GITVERSION_SEMVER% .

start http://localhost:8384
wsl docker run -p 8384:8384 -v ./db:/app/db -e Workspace__RootPath=C:\Workspace jandini/graymoon:latest
