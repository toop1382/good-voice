#!/bin/bash

# Installer script for Voice Chat Server as a systemd service on Linux

# Exit on any error
set -e

echo "=========================================================="
echo "          Voice Chat Server systemd Installer"
echo "=========================================================="
echo ""

# 1. Compile and publish in Release mode
echo "[1/4] Publishing dotnet server project..."
dotnet publish Server/Server.csproj -c Release -o ./publish

# 2. Setup directory
echo "[2/4] Creating target directory (/opt/voice-chat)..."
sudo mkdir -p /opt/voice-chat
sudo cp -r ./publish/* /opt/voice-chat/

# 3. Setup systemd service file
echo "[3/4] Copying service file to /etc/systemd/system/..."
sudo cp voice-chat.service /etc/systemd/system/voice-chat.service

# Find where dotnet is installed and substitute it in the service file
DOTNET_PATH=$(which dotnet || echo "/usr/bin/dotnet")
echo "  Using dotnet executable at: $DOTNET_PATH"
sudo sed -i "s|/usr/bin/dotnet|$DOTNET_PATH|g" /etc/systemd/system/voice-chat.service

# 4. Start and enable service
echo "[4/4] Activating systemd service..."
sudo systemctl daemon-reload
sudo systemctl enable voice-chat.service
sudo systemctl restart voice-chat.service

echo ""
echo "=========================================================="
echo " Setup complete! Service is active and running."
echo " Status:    sudo systemctl status voice-chat.service"
echo " Logs:      journalctl -u voice-chat.service -f"
echo " Dashboard: http://<server-ip>:5000/"
echo "=========================================================="
