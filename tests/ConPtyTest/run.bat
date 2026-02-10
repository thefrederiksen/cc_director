@echo off
cd /d "%~dp0"
dotnet build --verbosity quiet
if %errorlevel% neq 0 exit /b %errorlevel%
dotnet run --no-build
