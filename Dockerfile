FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY GrayMoon.sln ./
COPY src/GrayMoon.App/GrayMoon.App.csproj src/GrayMoon.App/
RUN dotnet restore "GrayMoon.sln"

COPY . .
RUN dotnet publish "src/GrayMoon.App/GrayMoon.App.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8384
EXPOSE 8384

ENTRYPOINT ["dotnet", "GrayMoon.App.dll"]
