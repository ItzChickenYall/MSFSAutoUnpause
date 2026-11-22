@echo off
echo Building MSFSAutoUnpause...
dotnet build "%~dp0MSFSAutoUnpause\MSFSAutoUnpause.csproj" -c Release
if %errorlevel% neq 0 (
    echo.
    echo BUILD FAILED. Make sure .NET 6 SDK is installed.
    pause
    exit /b 1
)
echo.
echo Build complete.
echo Output: %~dp0MSFSAutoUnpause\bin\Release\net6.0-windows10.0.19041.0\
echo (For a release zip, run publish.ps1 instead.)
pause
