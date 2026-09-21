@echo off
setlocal

REM Set base URL for raw files
set "baseurl=https://raw.githubusercontent.com/CarbonCommunity/Carbon.QuickStart/main/win"

REM List of files to download
set files=run.bat update_edge.bat update_preview.bat update_production.bat update_rustbeta_staging.bat update_server.bat

REM Download each file
for %%f in (%files%) do (
    if exist "%%f" (
        choice /M "%%f already exists. Do you want to replace it?"
        if errorlevel 2 (
            echo Skipping %%f
        ) else (
            echo Downloading %%f...
            powershell -Command "Invoke-WebRequest -Uri '%baseurl%/%%f' -OutFile '%%f'"
        )
    ) else (
        echo Downloading %%f...
        powershell -Command "Invoke-WebRequest -Uri '%baseurl%/%%f' -OutFile '%%f'"
    )
)

echo All done.
pause
