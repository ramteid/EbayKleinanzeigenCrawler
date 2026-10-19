FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["KleinanzeigenCrawler.sln", "./"]
COPY ["KleinanzeigenCrawler/KleinanzeigenCrawler.csproj", "KleinanzeigenCrawler/"]
COPY ["KleinanzeigenCrawler.Tests/KleinanzeigenCrawler.Tests.csproj", "KleinanzeigenCrawler.Tests/"]
RUN dotnet restore "KleinanzeigenCrawler.sln"

COPY . .
WORKDIR "/src/KleinanzeigenCrawler"
RUN dotnet build "KleinanzeigenCrawler.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "KleinanzeigenCrawler.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "KleinanzeigenCrawler.dll"]
