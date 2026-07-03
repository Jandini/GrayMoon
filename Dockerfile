FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-agent
WORKDIR /src

# SemVer injected by build script or CI (disables GitVersion.MsBuild inside container)
ARG VERSION=1.0.0

COPY . .

# Publish Agent (framework-dependent, multi-file) for Linux x64 and Windows x64; pack as zip for download
RUN apt-get update && apt-get install -y --no-install-recommends zip && rm -rf /var/lib/apt/lists/*
RUN dotnet publish "src/GrayMoon.Agent/GrayMoon.Agent.csproj" -c Release -r linux-x64 -o /agent/publish-linux /p:Version=$VERSION /p:DisableGitVersionTask=true
RUN dotnet publish "src/GrayMoon.Agent/GrayMoon.Agent.csproj" -c Release -r win-x64 -o /agent/publish-win /p:Version=$VERSION /p:DisableGitVersionTask=true
RUN cd /agent/publish-linux && zip -q -r /agent/graymoon-agent-linux.zip .
RUN cd /agent/publish-win && zip -q -r /agent/graymoon-agent-windows.zip .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-app
WORKDIR /src

ARG VERSION=1.0.0

COPY . .
RUN dotnet publish "src/GrayMoon.App/GrayMoon.App.csproj" -c Release -o /app/publish \
  /p:UseAppHost=false \
  /p:Version=$VERSION /p:DisableGitVersionTask=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build-app /app/publish .
# Pack agent executables for download (runs on host)
RUN mkdir -p /app/agent
COPY --from=build-agent /agent/graymoon-agent-linux.zip /app/agent/graymoon-agent-linux.zip
COPY --from=build-agent /agent/graymoon-agent-windows.zip /app/agent/graymoon-agent-windows.zip

# Database stored in /app/db for easy volume persistence: -v ./data:/app/db
VOLUME ["/app/db"]

ENV ASPNETCORE_URLS=http://+:8384
ENV ASPNETCORE_HTTP_PORTS=8384
EXPOSE 8384

ENTRYPOINT ["dotnet", "GrayMoon.App.dll"]
