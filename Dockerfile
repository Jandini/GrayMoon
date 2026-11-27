FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-worker
WORKDIR /src

# SemVer injected by build script or CI (disables GitVersion.MsBuild inside container)
ARG VERSION=1.0.0

COPY . .

# The Worker is a Native AOT exe (set in its project): no .NET runtime on the host. The Linux exe is built here;
# clang and zlib1g-dev are what the AOT link step needs. Hosts need a glibc at least as new as this image's.
RUN apt-get update && apt-get install -y --no-install-recommends zip clang zlib1g-dev && rm -rf /var/lib/apt/lists/*
RUN dotnet publish "src/GrayMoon.Worker/GrayMoon.Worker.csproj" -c Release -r linux-x64 -o /worker/publish-linux /p:Version=$VERSION /p:DisableGitVersionTask=true /p:DebugType=None /p:DebugSymbols=false
RUN find /worker/publish-linux \( -name '*.pdb' -o -name '*.dbg' -o -name '*.Development.*' -o -name '*.Development' \) -delete
RUN cd /worker/publish-linux && zip -q -r /worker/graymoon-worker-linux.zip .

# The Windows exe cannot be compiled on Linux (Native AOT has no cross-OS compile). It is built on Windows before
# the image build and passed in as worker-dist/graymoon-worker-windows.zip: docker-build.yml does that in its
# worker-windows job; for a local image build run dev/Build-WindowsWorkerZip.ps1 first. Without the file the
# build stops here.
COPY worker-dist/graymoon-worker-windows.zip /worker/graymoon-worker-windows.zip

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-app
WORKDIR /src

ARG VERSION=1.0.0

COPY . .
RUN dotnet publish "src/GrayMoon.App/GrayMoon.App.csproj" -c Release -o /app/publish \
  /p:UseAppHost=false \
  /p:Version=$VERSION /p:DisableGitVersionTask=true \
  /p:DebugType=None /p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build-app /app/publish .
# Pack worker executables for download (runs on host)
RUN mkdir -p /app/worker
COPY --from=build-worker /worker/graymoon-worker-linux.zip /app/worker/graymoon-worker-linux.zip
COPY --from=build-worker /worker/graymoon-worker-windows.zip /app/worker/graymoon-worker-windows.zip

# Database stored in /app/db for easy volume persistence: -v ./data:/app/db
VOLUME ["/app/db"]

ENV ASPNETCORE_URLS=http://+:8384
ENV ASPNETCORE_HTTP_PORTS=8384
EXPOSE 8384

ENTRYPOINT ["dotnet", "GrayMoon.App.dll"]
