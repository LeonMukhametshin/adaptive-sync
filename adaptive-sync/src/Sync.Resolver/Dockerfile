FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/Sync.Core/Sync.Core.csproj src/Sync.Core/
COPY src/Sync.Strategies/Sync.Strategies.csproj src/Sync.Strategies/
COPY src/Sync.Resolver/Sync.Resolver.csproj src/Sync.Resolver/
RUN dotnet restore src/Sync.Resolver/Sync.Resolver.csproj
COPY src/Sync.Core/ src/Sync.Core/
COPY src/Sync.Strategies/ src/Sync.Strategies/
COPY src/Sync.Resolver/ src/Sync.Resolver/
RUN dotnet publish src/Sync.Resolver/Sync.Resolver.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sync.Resolver.dll"]
