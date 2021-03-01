# EbayKleinanzeigenCrawler
.Net 5.0 based crawler that parses Ebay Kleinanzeigen classified ads and notifies via a Telegram Bot.

# Features:
* Notifies you reliably about new articles within 5 minutes!
* Searches for keywords in title and description!
* Specify which search keywords to include and which to exclude!
* Specify a subscription by simply copying the URL from your browser
* Uses Telegram as interface for notifications and control
* Supports multiple Subscribers and Subscriptions
* You can easily add different interfaces (Console, E-Mail, SMS, ...)
* Persists data in JSON files

# Before use:
* Create your own Telegram Bot
* Paste the Bot's token into "TelegramBotToken" in TelegramManager.cs

# How to use:
* Send /help to the Telegram bot for instructions
* Currently only works with Desktop-Browser links, not mobile browser links. (https://www.ebay-kleinanzeigen.de/....)
* Ebay Kleinanzeigen obfuscates its HTML with JavaScript, when more than 40 queries are made within the last 5 minutes. This software considers this limit.
* This software is work in progress. There are many TODOs in the code. Feel free to contribute :-)