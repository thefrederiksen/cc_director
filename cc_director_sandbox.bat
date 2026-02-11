@echo off
echo Building CC Director...
dotnet build "%~dp0src\CcDirector.Wpf\CcDirector.Wpf.csproj" -c Debug --no-incremental
if errorlevel 1 (
    echo Build FAILED
    pause
    exit /b 1
)
echo.
echo Starting CC Director in SANDBOX mode...
echo (Sessions will not be loaded or saved)
start "" "%~dp0src\CcDirector.Wpf\bin\Debug\net10.0-windows\win-x64\cc_director.exe" --sandbox
