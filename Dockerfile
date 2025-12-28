# ==========================================
# 1. RUNTIME STAGE (The final container)
# ==========================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80

# Install System Dependencies
# - Tor: For proxying traffic
# - Python/Pip: For running yt-dlp
# - FFmpeg: For video merging/conversion
# - Netcat: For checking ports
# - Chromium Libs: Required for Puppeteer to run headless Chrome
RUN apt-get update && apt-get install -y \
    tor \
    python3 \
    python3-pip \
    python3-venv \
    ffmpeg \
    curl \
    wget \
    gnupg \
    netcat-openbsd \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libgdk-pixbuf2.0-0 \
    libgtk-3-0 \
    libgbm-dev \
    libnss3-dev \
    libxss-dev \
    fonts-liberation \
    libasound2t64 \
    && rm -rf /var/lib/apt/lists/*

# Fix: Allow pip to install packages globally (bypassing Ubuntu 24.04 protection)
RUN rm -f /usr/lib/python3.*/EXTERNALLY-MANAGED

# Setup Entrypoint Script
COPY entrypoint.sh /entrypoint.sh

# Fix: Strip Windows Line Endings (\r) in case the file was saved on Windows
RUN sed -i 's/\r$//' /entrypoint.sh
RUN chmod +x /entrypoint.sh

# ==========================================
# 2. BUILD STAGE (Compiling the C# code)
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Siphon.csproj", "."]
RUN dotnet restore "./Siphon.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "Siphon.csproj" -c Release -o /app/build

# ==========================================
# 3. PUBLISH STAGE
# ==========================================
FROM build AS publish
RUN dotnet publish "Siphon.csproj" -c Release -o /app/publish

# ==========================================
# 4. FINAL STAGE
# ==========================================
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# We set the entrypoint to the shell script, which will start Tor and then the App
ENTRYPOINT ["/entrypoint.sh"]