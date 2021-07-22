FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /code

COPY . /code
RUN dotnet restore
RUN dotnet build -c Release

FROM mcr.microsoft.com/dotnet/runtime:5.0
WORKDIR /bot
COPY --from=build /code/EbayKleinanzeigenCrawler/bin/Release/net5.0 .
RUN chmod +x EbayKleinanzeigenCrawler

ENTRYPOINT ./EbayKleinanzeigenCrawler 

# Note: the environment variable TELEGRAM_BOT_TOKEN has to be set when starting this container
#       using the --env TELEGRAM_BOT_TOKEN=<TOKEN> syntax