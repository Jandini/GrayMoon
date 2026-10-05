FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-worker
WORKDIR /src

# SemVer injected by build script or CI (disables GitVersion.MsBuild inside container)
ARG VERSION=1.0.0

COPY . .

# Publish Worker (framework-dependent, multi-file) for Linux x64 and Windows x64; pack as zip for download
RUN apt-get update && apt-get install -y --no-install-recommends zip && rm -rf /var/lib/apt/lists/*
RUN dotnet publish "src/GrayMoon.Worker/GrayMoon.Worker.csproj" -c Release -r linux-x64 -o /worker/publish-linux /p:Version=$VERSION /p:DisableGitVersionTask=true /p:DebugType=None /p:DebugSymbols=false /p:PublishReadyToRun=true
RUN dotnet publish "src/GrayMoon.Worker/GrayMoon.Worker.csproj" -c Release -r win-x64 -o /worker/publish-win /p:Version=$VERSION /p:DisableGitVersionTask=true /p:DebugType=None /p:DebugSymbols=false /p:PublishReadyToRun=true
RUN find /worker/publish-linux /worker/publish-win \( -name '*.pdb' -o -name '*.Development.*' -o -name '*.Development' \) -delete
RUN cd /worker/publish-linux && zip -q -r /worker/graymoon-worker-linux.zip .
RUN cd /worker/publish-win && zip -q -r /worker/graymoon-worker-windows.zip .

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
