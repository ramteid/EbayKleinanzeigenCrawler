FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /code

COPY . /code
RUN dotnet restore
RUN dotnet build -c Release

FROM mcr.microsoft.com/dotnet/runtime:6.0
WORKDIR /bot
COPY --from=build /code/EbayKleinanzeigenCrawler/bin/Release/net6.0 .
RUN chmod +x EbayKleinanzeigenCrawler

ENTRYPOINT ./EbayKleinanzeigenCrawler 
