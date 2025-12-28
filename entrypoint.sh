#!/bin/bash

# 1. Update yt-dlp (Keep this, it's good practice)
echo "Updating yt-dlp..."
pip3 install --upgrade yt-dlp --break-system-packages

# 2. Ensure Directories Exist (Optional but safe)
mkdir -p /var/lib/tor
mkdir -p /etc/tor
chmod 700 /var/lib/tor

# 3. Start App
# We DO NOT start Tor here anymore. 
# The C# SystemBootstrapper will start Tor with the correct settings automatically.
echo "Starting Siphon..."
dotnet Siphon.dll