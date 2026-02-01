FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# SemVer injected by build script or CI (disables GitVersion.MsBuild inside container)
ARG VERSION=0.0.0

COPY GrayMoon.sln ./
COPY src/GrayMoon.App/GrayMoon.App.csproj src/GrayMoon.App/
COPY src/GrayMoon.Agent/GrayMoon.Agent.csproj src/GrayMoon.Agent/
RUN dotnet restore "GrayMoon.sln"

COPY . .
RUN dotnet publish "src/GrayMoon.App/GrayMoon.App.csproj" -c Release -o /app/publish /p:UseAppHost=false \
    /p:GetVersion=false \
    /p:UpdateAssemblyInfo=false \
    /p:UpdateVersionProperties=false \
    /p:Version=${VERSION} \
    /p:InformationalVersion=${VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# App delegates git/GitVersion to GrayMoon.Agent (runs on host)
COPY --from=build /app/publish .

# Database stored in /app/db for easy volume persistence: -v ./data:/app/db
VOLUME ["/app/db"]

ENV ASPNETCORE_URLS=http://+:8384
ENV ASPNETCORE_HTTP_PORTS=8384
EXPOSE 8384

ENTRYPOINT ["dotnet", "GrayMoon.App.dll"]
