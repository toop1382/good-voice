#!/bin/bash

# Voice Chat Server & Dashboard Start Script for Linux/macOS

echo "=========================================================="
echo "          LOW-LATENCY VOICE CHAT SERVER & DASHBOARD"
echo "=========================================================="
echo ""
echo " Starting multi-protocol voice server and Blazor dashboard..."
echo " Please wait a moment for dotnet compilation to complete..."
echo ""
echo " Dashboard URL: http://localhost:5000/"
echo ""

dotnet run --project Server -c Release
if [ $? -ne 0 ]; then
    echo ""
    echo "[ERROR] Failed to start the server. Make sure .NET 8 SDK is installed."
    echo ""
    exit 1
fi
