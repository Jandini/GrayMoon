FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# SemVer injected by build script or CI (disables GitVersion.MsBuild inside container)
ARG VERSION=1.0.0

COPY GrayMoon.sln ./
COPY src/GrayMoon.App/GrayMoon.App.csproj src/GrayMoon.App/
COPY src/GrayMoon.Agent/GrayMoon.Agent.csproj src/GrayMoon.Agent/
RUN dotnet restore "GrayMoon.sln" /p:DisableGitVersionTask=true

COPY . .
RUN dotnet publish "src/GrayMoon.App/GrayMoon.App.csproj" -c Release -o /app/publish \
  /p:UseAppHost=false \
  /p:Version=$VERSION \
  /p:DisableGitVersionTask=true

# Publish Agent as single-file trimmed self-contained (Linux x64 and Windows x64 for download)
RUN dotnet publish "src/GrayMoon.Agent/GrayMoon.Agent.csproj" -c Release -r linux-x64 -o /agent/publish-linux
RUN dotnet publish "src/GrayMoon.Agent/GrayMoon.Agent.csproj" -c Release -r win-x64 -o /agent/publish-win

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .
# Pack agent executables for download (runs on host)
RUN mkdir -p /app/agent
COPY --from=build /agent/publish-linux/graymoon-agent /app/agent/graymoon-agent
COPY --from=build /agent/publish-win/graymoon-agent.exe /app/agent/graymoon-agent.exe
RUN chmod +x /app/agent/graymoon-agent

# Database stored in /app/db for easy volume persistence: -v ./data:/app/db
VOLUME ["/app/db"]

ENV ASPNETCORE_URLS=http://+:8384
ENV ASPNETCORE_HTTP_PORTS=8384
EXPOSE 8384

ENTRYPOINT ["dotnet", "GrayMoon.App.dll"]
