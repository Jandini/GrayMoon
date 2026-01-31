FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY GrayMoon.sln ./
COPY src/GrayMoon.App/GrayMoon.App.csproj src/GrayMoon.App/
RUN dotnet restore "GrayMoon.sln"

COPY . .
RUN dotnet publish "src/GrayMoon.App/GrayMoon.App.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Install GitVersion 5.12 to /gv for copying to runtime
RUN dotnet tool install GitVersion.Tool --version 5.12.0 --tool-path /gv

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Install git and copy GitVersion from build stage
RUN apt-get update && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

# Copy GitVersion to /app/tools (accessible by non-root app user in .NET 8 images)
COPY --from=build /gv /app/tools
RUN chmod -R 755 /app/tools
ENV PATH="${PATH}:/app/tools"

COPY --from=build /app/publish .

# Database stored in /app/db for easy volume persistence: -v ./data:/app/db
VOLUME ["/app/db"]

ENV ASPNETCORE_URLS=http://+:8384
EXPOSE 8384

ENTRYPOINT ["dotnet", "GrayMoon.App.dll"]
