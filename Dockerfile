FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /code

COPY . /code
RUN dotnet restore
RUN dotnet build -c Release

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /bot
COPY --from=build /code/EbayKleinanzeigenCrawler/bin/Release/net9.0 .
RUN chmod +x EbayKleinanzeigenCrawler

ENTRYPOINT ./EbayKleinanzeigenCrawler 
