@echo off
echo Starting CC Director in SANDBOX mode...
echo (Sessions will not be loaded or saved)
start "" "%~dp0src\CcDirector.Wpf\bin\Debug\net10.0-windows\win-x64\cc_director.exe" --sandbox
