FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/Sync.Core/Sync.Core.csproj src/Sync.Core/
COPY src/Sync.Strategies/Sync.Strategies.csproj src/Sync.Strategies/
COPY src/Sync.Server/Sync.Server.csproj src/Sync.Server/
RUN dotnet restore src/Sync.Server/Sync.Server.csproj
COPY src/Sync.Core/ src/Sync.Core/
COPY src/Sync.Strategies/ src/Sync.Strategies/
COPY src/Sync.Server/ src/Sync.Server/
RUN dotnet publish src/Sync.Server/Sync.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sync.Server.dll"]
