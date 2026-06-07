@echo off
title Voice Chat Server ^& Dashboard
color 0B
echo ==========================================================
echo           LOW-LATENCY VOICE CHAT SERVER ^& DASHBOARD
echo ==========================================================
echo.
echo  Starting multi-protocol voice server and Blazor dashboard...
echo  Please wait a moment for dotnet compilation to complete...
echo.
echo  Dashboard URL: http://localhost:5000/
echo.
:: Open the dashboard in the default browser automatically
start "" http://localhost:5000/
echo.

dotnet run --project Server -c Release
if %ERRORLEVEL% neq 0 (
    color 0C
    echo.
    echo [ERROR] Failed to start the server. Make sure .NET 8 SDK is installed.
    echo.
    pause
)
