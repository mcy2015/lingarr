# Set default build configuration
ARG BUILD_CONFIGURATION=Release

FROM --platform=$BUILDPLATFORM node:24-slim AS client
ARG BUILD_CONFIGURATION

# Step 1: Prepare Client
COPY ./Lingarr.Client/ /Client
WORKDIR /Client
# Install dependencies
RUN npm ci
RUN npm run build

# Step 2: Prepare the base image for the server
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

# Step 3: Build the server
# Runs on the native build platform (FROM --platform=$BUILDPLATFORM) and
# publishes RID-less with UseAppHost=false: the managed output is
# architecture-neutral (native assets for every RID ship in runtimes/),
# so one build feeds both the amd64 and arm64 runtime bases — no
# cross-arch restore/build, which fails on GitHub runners (MSB4181).
# Pinned: the floating 10.0 tag (10.0.402 as of 2026-10) fails
# dotnet restore instantly on GitHub amd64 runners (MSB4181, no NU
# error) while 10.0.401 — the band upstream last built with — works.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
ARG BUILD_CONFIGURATION
ARG APP_VERSION=0.0.0-dev
WORKDIR /src
COPY ["Directory.Packages.props", "global.json", "./"]
COPY ["Lingarr.Contracts/Lingarr.Contracts.csproj", "Lingarr.Contracts/"]
COPY ["Lingarr.Server/Lingarr.Server.csproj", "Lingarr.Server/"]
COPY ["Lingarr.Core/Lingarr.Core.csproj", "Lingarr.Core/"]
COPY ["Lingarr.Migrations/Lingarr.Migrations.csproj", "Lingarr.Migrations/"]
RUN dotnet restore "./Lingarr.Server/./Lingarr.Server.csproj"
COPY . .
WORKDIR "/src/Lingarr.Server"
COPY --from=client /Client/dist/ ./wwwroot
RUN dotnet build "./Lingarr.Server.csproj" -c $BUILD_CONFIGURATION -o /app/build /p:Version=$APP_VERSION

# Step 4: Publish the application
FROM --platform=$BUILDPLATFORM build AS publish
ARG BUILD_CONFIGURATION
ARG APP_VERSION=0.0.0-dev
RUN dotnet publish "./Lingarr.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:Version=$APP_VERSION

# Step 5: Final image
FROM base AS final
WORKDIR /app
# ffmpeg ships ffprobe, which the embedded subtitle track scan invokes
# (F2a: probe MKV/MP4 containers for subtitle streams). Cost: ~350 MB
# installed (codecs + libs) on top of the runtime image — the Debian package
# is the pragmatic multi-arch source (official repo serves amd64 and arm64),
# and only the probe runs, never a transcode. Deployments without Docker can
# instead install ffmpeg themselves or set LINGARR_FFPROBE_PATH.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
COPY --from=publish /app/publish .

VOLUME /app/config
# Start the application
ENTRYPOINT ["dotnet", "Lingarr.Server.dll"]