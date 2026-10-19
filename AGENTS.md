# AGENTS BRIEFING

This document is written for automation agents that need a fast, accurate view of the repository. It summarizes runtime behavior, architecture, dependencies, and operational concerns.

---

## 1. What This Project Does

Kleinanzeigen Crawler is a .NET 9 console application that:

- Reads subscription definitions from Telegram and Signal conversations.
- Periodically queries Kleinanzeigen and WG-Gesucht search URLs.
- Applies include/exclude keyword filtering against ad titles/descriptions.
- Notifies subscribers via Telegram and/or Signal (both can run at the same time) when new matches appear.
- Exposes Prometheus-compatible metrics and ships with a Grafana/Alertmanager stack.

The application is designed to run continuously inside Docker. A Redis instance stores hashes of processed URLs; subscriber metadata lives in JSON files.

---

## 2. High-Level Architecture

```
          +-----------------------+   +-----------------------+
          | TelegramManager       |   | SignalManager         |
          |  - Telegram Bot API   |   |  - Signal REST API    |
          |  - Buttons/UI Flow    |   |  - Text prompts       |
          +-----------+-----------+   +-----------+-----------+
                      |                           |
                      +-------------+-------------+
                                    |
                                    v
                        +-----------------------+
                        | SessionManager        |
                        |  - Conversation state |
                        |  - Data per user      |
                        +-----------+-----------+
                                    |
                                    v
   +----------------+    +----------+-----------+    +---------------------+
   | ParserProvider | -> | SubscriptionService  | -> | SubscriptionHandler |
   +----------------+    +----------+-----------+    +---------------------+
                                    |                         |
                                    v                         v
                        +-----------------------+   +-----------------------+
                        | SubscriptionPersistence|   | RedisUrlTracker       |
                        | (JSON on disk)        |   | (processed URL hashes) |
                        +-----------------------+   +-----------------------+
                                                        |
                                                        v
                                   +---------------------------------------+
                                   | Prometheus Metrics (MetricsService)    |
                                   +---------------------------------------+

```

### Key Components

| Component | Location | Responsibilities |
|-----------|----------|------------------|
| `Program` | `KleinanzeigenCrawler/Program.cs` | Bootstraps DI container, config, logging, metrics and runs `SubscriptionHandler`. |
| `TelegramManager` | `Manager/TelegramManager.cs` | Handles Telegram polling, button-based conversations, session control, subscription CRUD and command routing. Retries rate-limited sends and disables the subscriptions of users who blocked the bot. |
| `SignalManager` | `Manager/SignalManager.cs` | Handles Signal conversations through `ISignalClient`: per-user ordered message handling, chunked replies, notifications for Signal subscribers. |
| `TextConversationHandler` | `Manager/TextConversationHandler.cs` | Messenger-independent text dialog (commands and step-by-step prompts) used by `SignalManager`. |
| `SignalRestClient` | `Signal/SignalRestClient.cs` | Gateway to signal-cli-rest-api (json-rpc mode): WebSocket receive with reconnect backoff, `POST /v2/send`. |
| `CompositeNotifier` | `Manager/CompositeNotifier.cs` | `IOutgoingNotifications` implementation that fans a match out to all running messengers (`IMessenger.NotifyAsync` returns a `DeliveryReport`); a failing messenger does not affect the others. It throws only if nobody was reached because of transient failures, so `SubscriptionHandler` retries the result without notifying anybody twice. |
| `SessionManager` | `Manager/SessionManager.cs` | Maintains per-user session data with automatic timeout cleanup. |
| `AccessGate` | `Manager/AccessGate.cs` | Decides for every incoming message whether the sender may use the bot (one instance per messenger channel, used by `TelegramManager` and `TextConversationHandler`). Subscribers always pass. With `ACCESS_PASSWORD`, a new user must send the password once to become a subscriber; 10 wrong passwords block the user for good and the bot does not answer its messages anymore. `ALLOW_NEW_USERS=false` rejects new users before the password is asked. |
| `SubscriptionService` | `Manager/SubscriptionService.cs` | Business logic for validating URLs and titles, enforcing result limits, registering subscribers and updating subscriber data. One instance per messenger channel. |
| `SubscriptionHandler` | `Subscriptions/SubscriptionHandler.cs` | Infinite loop that queries subscriptions, feeds parsers, matches results, calls the notifier. |
| `ParserProvider` & `ParserBase` | `Parser/` | Provides parser implementations (Kleinanzeigen & WG-Gesucht) with consistent rate limiting and error handling. |
| `RedisUrlTracker` | `Persistence/RedisUrlTracker.cs` | SHA256 hashes of processed URLs with 90-day TTL, prevents duplicate notifications. |
| `SubscriptionPersistence` & `JsonStorage` | `Persistence/` | Thread-safe JSON persistence of `Subscriber` objects under `./data`. |
| `AccessAttemptPersistence` | `Persistence/AccessAttemptPersistence.cs` | Failed password attempts of users who are not subscribers, persisted in `./data/AccessAttempts.json` so that blocks survive restarts. |
| `MetricsService` | `Monitoring/MetricsService.cs` | Prometheus counters, gauges, histograms, summaries used throughout the app. |

---

## 3. Data & Persistence

### Subscriber Data
- Stored in `./data/Subscribers.json`.
- A subscriber is identified by its messenger `Channel` (`Telegram` or `Signal`) and its ID. Entries without a channel load as Telegram subscribers.
- Managed via `SubscriptionPersistence` using the `IDataStorage` abstraction (`JsonStorage`).
- Automatically restored on startup; saved after each mutation.
- A missing file starts with no subscribers. A file that exists but cannot be parsed stops the start-up, because continuing would overwrite it with the next change.

### Access Attempts
- Stored in `./data/AccessAttempts.json` by `AccessAttemptPersistence`: per messenger channel and user ID the number of wrong passwords (only users who are not subscribers; an entry with 10 failed attempts is a permanent block).
- To unblock a user, stop the bot, remove its entry from the file and start the bot (the bot rewrites the file on every change).
- A missing file starts empty. A file that exists but cannot be parsed stops the start-up, because continuing would unblock everybody.

### Processed URLs
- Stored exclusively in Redis (`redis` service in docker-compose).
- URLs are hashed (SHA256) and stored per subscription with a 90-day TTL that is refreshed while the URL is still listed.
- `RedisUrlTracker` implements `IUrlTracker` (`RefreshIfProcessedAsync`, `MarkProcessedAsync`). Redis errors are thrown, never treated as "not processed", so an outage cannot re-notify old ads.

### Logs
- Serilog writes daily rolling files to `./data/logfile<yyyyMMdd>.txt` (1 MB cap per file, 90-day retention).

---

## 4. Messenger Flows

Telegram and Signal run simultaneously when both are configured. Each messenger notifies only the subscribers of its own channel.

### Telegram

1. Telegram is enabled by `TELEGRAM_BOT_TOKEN`.
2. `TelegramManager` polls messages & callback queries.
3. Slash commands (`/start`, `/help`, `/menu`, `/cancel`) still work but buttons are the primary UX.
4. Each user gets a `ConversationSession` stored in memory and keyed by chat ID.
5. Subscription wizard collects URL, include keywords, exclude keywords, initial pull preference, and title.
6. Subscriptions persist immediately and are picked up by the background handler.
7. Setting `ALLOW_NEW_USERS=false` disallows new subscribers; existing ones continue to work.
8. New users are admitted by the `AccessGate` (see Access Control).

### Signal

1. Signal is enabled by `SIGNAL_NUMBER` and needs a signal-cli-rest-api container in json-rpc mode at `SIGNAL_API_URL` (compose service `signal-api`, profile `signal`).
2. `SignalRestClient` receives messages through the API's WebSocket and sends replies with `POST /v2/send`. Only text messages of one-to-one chats are handled.
3. `SignalManager` passes each message to `TextConversationHandler`: commands (`/add`, `/list`, `/edit`, `/enable`, `/disable`, `/delete`, `/cancel`, `/help`) and step-by-step prompts instead of buttons.
4. The messages of one user are handled in order; different users do not wait for each other.
5. The sender's Signal account ID identifies the subscriber.
6. New users are admitted by the `AccessGate` (see Access Control). The reply is null for blocked users, so nothing is sent.

### Access Control

Applies to every messenger. Without `ACCESS_PASSWORD`, new users are registered right away (unless `ALLOW_NEW_USERS=false`).

1. Subscribers (known per channel and ID) are never asked for the password.
2. The first message of an unknown user is answered with a request for the password. It is not counted as a wrong attempt, unless it is the password itself, which admits the user at once. Messages without text (button presses) never count.
3. Every further message is a password attempt: right -> the user becomes a subscriber for good and gets the welcome; wrong -> the attempt is saved and the reply names the attempts left.
4. The 10th wrong attempt blocks the user for good: one final notice, then no more answers and no more processing, even for the right password.
5. The password is compared case-sensitively without surrounding whitespace, in constant time on SHA256 hashes. Neither the password nor texts of users who are not subscribers are logged.

---

## 5. Crawling Loop

- Implemented in `SubscriptionHandler.ProcessAllSubscriptionsAsync`.
- Steps:
  1. Verify the Redis connection.
  2. Fetch enabled subscriptions.
  3. Group by parser to respect provider rate limits.
  4. For each subscription: fetch pages, parse results, check Redis, run keyword matching, notify subscribers, and remember each handled result in Redis right away. A result whose ad page cannot be loaded, or whose notification throws, is retried in the next passes and given up after 3 failed attempts. Ads answering 404/410 (`PageNotFoundException`) are treated as deleted and not retried. Delivery failures inside a messenger are logged and counted in the notification metrics, they do not make the crawler retry.
  5. Wait for `CHECK_INTERVAL_SECONDS` (default 60) and repeat.
- Metrics recorded: subscription processing success, HTTP requests, parse errors, notification latency, etc.

---

## 6. Monitoring Stack

Docker Compose services:
- `bot`: the crawler.
- `signal-api`: signal-cli-rest-api gateway for Signal (only with compose profile `signal`, published on `127.0.0.1:8085` for registering the number).
- `redis`: URL tracker backend (internal network only).
- `prometheus`: scrapes bot metrics (not exposed to host).
- `alertmanager`: sends Prometheus alerts through the Telegram bot to the chat `ALERT_TELEGRAM_CHAT_ID` (Compose secrets read by `monitoring/alertmanager.yml`). Its UI has no authentication and is published on `127.0.0.1:9093` only.
- `grafana`: published on port `3333` for dashboards and alert visualization.

Prometheus alerts (see `monitoring/alerts.yml`) include crawler down, parse error bursts, HTTP error rate, stuck processing, etc.

---

## 7. Configuration Reference

| Variable | Default | Description |
|----------|---------|-------------|
| `TELEGRAM_BOT_TOKEN` | — (optional) | Bot token from @BotFather. Telegram is disabled when empty. |
| `SIGNAL_NUMBER` | — (optional) | Dedicated Signal number of the bot in international format. Signal is disabled when empty. |
| `SIGNAL_API_URL` | `http://signal-api:8080` | Base URL of the signal-cli-rest-api container (json-rpc mode). |
| `ALLOW_NEW_USERS` | `true` | If `false`, new users cannot register on any messenger (the password is not asked). |
| `ACCESS_PASSWORD` | none (optional) | Password (at least 8 characters, case-sensitive) that every new user must send once on any messenger. Empty means new users need none. See Access Control. |
| `ENABLE_PROMETHEUS` | `true` | Starts embedded metrics server (Kestrel). |
| `PROMETHEUS_PORT` | `9090` | Port used by the bot container for metrics (internal network). |
| `CHECK_INTERVAL_SECONDS` | `60` | Delay between crawl passes. |
| `SESSION_TIMEOUT_MINUTES` | `15` | Conversation session inactivity timeout. |
| `DATA_PATH` | `data` | Relative path inside container mapped to `./data`. |
| `REDIS_CONNECTION_STRING` | `redis:6379,abortConnect=false` | Connection string passed to `RedisUrlTracker`. |
| `GRAFANA_PASSWORD` | — (required) | Grafana admin password (docker-compose). |
| `ALERT_TELEGRAM_CHAT_ID` | — (required) | Telegram chat that receives the alerts (docker-compose). Alerts are sent by the bot of `TELEGRAM_BOT_TOKEN`. |

At least one of `TELEGRAM_BOT_TOKEN` and `SIGNAL_NUMBER` is required. Docker Compose additionally requires `TELEGRAM_BOT_TOKEN` for alert delivery. Unset or empty numeric and boolean variables use the default; an invalid value (e.g. `CHECK_INTERVAL_SECONDS=0` or `ALLOW_NEW_USERS=flase`) stops the start-up with a message and exit code 1. `.env` or `.env.example` contains typical overrides. Docker Compose loads this file automatically.

---

## 8. Running & Testing

### Docker Workflow
```bash
docker compose build
docker compose up -d
docker compose logs -f bot
docker compose down
```

Grafana UI: `http://<docker-host>:3333` (user `admin`, password `GRAFANA_PASSWORD`).

### Local Development
```bash
dotnet restore
dotnet build
cd KleinanzeigenCrawler
dotnet run                # requires TELEGRAM_BOT_TOKEN and/or SIGNAL_NUMBER env var
```

The dev container (`.devcontainer/`: .NET 9 SDK plus a `redis` service) provides everything needed. Run `dotnet run --project KleinanzeigenCrawler` from the repository root there, so that `.env` is read.

### Tests
`dotnet test` runs the unit test suite (SessionManager, SubscriptionService, Parser parsing behavior, etc.). Tests are located under `KleinanzeigenCrawler.Tests`. The alert rules are tested with `promtool test rules monitoring/alerts_test.yml` (see README).

---

## 9. Common Pitfalls

- **Missing Redis**: the app exits on startup if it cannot connect (Compose starts it only after Redis is healthy); ensure `redis` service is running in docker-compose or update `REDIS_CONNECTION_STRING`. During a Redis outage the affected subscriptions fail for that pass.
- **Mobile URLs**: subscription creation rejects non-HTTPS desktop URLs.
- **Too many results**: subscriptions with >50 estimated results are rejected to protect the provider.
- **Rate limiting**: if you see 429/Retry-After, the QueryExecutor automatically backs off, but excessive subscriptions may still breach limits.
- **Alert delivery**: the alert chat must have started a conversation with the bot, otherwise Telegram rejects the alert messages (see `docker compose logs alertmanager`).
- **Data path**: `DATA_PATH` should stay inside the mounted volume (`./data`) or data will be lost when the container restarts.
- **Signal number**: use a dedicated number; everybody who writes to it talks to the bot unless `ACCESS_PASSWORD` is set. The number must be registered with the `signal-api` container, which must run in `json-rpc` mode, otherwise the bot logs the reason and keeps retrying.
- **Failed notifications**: sends are retried up to 3 times for rate limits and temporary errors (e.g. `signal-api` restarting). If nobody could be reached because of such an error, `SubscriptionHandler` tries the result again in the next passes (3 attempts in total). A notification that reached only part of its recipients is not repeated, so nobody is notified twice. Permanent failures such as a rejected message are only logged. Every failed send is counted in `crawler_notifications_sent_total{success="false"}` (label `channel` is `telegram` or `signal`). A Telegram user who blocked the bot gets all subscriptions disabled.

---

## 10. Repository Layout

```
KleinanzeigenCrawler/
  Configuration/          # AppConfiguration, ConfigurationLoader
  Interfaces/             # Contracts (IParser, IDataStorage, IOutgoingNotifications, IMessenger, ISignalClient, etc.)
  Manager/                # TelegramManager, SignalManager, TextConversationHandler, AccessGate, SessionManager, SubscriptionService, Formatting helpers
  Models/                 # Subscription, Subscriber, MessengerChannel, ConversationSession, AddSubscriptionData, Result
  Monitoring/             # MetricsService
  Parser/                 # ParserBase, ParserProvider, concrete site parsers
  Persistence/            # JsonStorage, SubscriptionPersistence, AccessAttemptPersistence, RedisUrlTracker
  Query/                  # QueryExecutor (rate-limited HTTP client), QueryCounter, UserAgentProvider
  Signal/                 # SignalRestClient (signal-cli-rest-api gateway)
  Subscriptions/          # SubscriptionHandler looping crawler

KleinanzeigenCrawler.Tests/
  Manager/                # SessionManager tests
  Parser/                 # Parser base behavior tests
  ...                     # Additional unit tests

monitoring/
  prometheus.yml
  alerts.yml
  alertmanager.yml
  grafana/ (dashboards + datasources)

docker-compose.yml        # Bot + monitoring stack
.env / .env.example       # Environment variable defaults
```

---

**Maintainer Tips**
- Always check `docker compose logs bot` when debugging Telegram or Signal issues (for Signal also `docker compose logs signal-api`); Serilog logs are verbose.
- Use Grafana dashboards (`monitoring/grafana/dashboards/kleinanzeigen-crawler.json`) to verify parsing health before deploying changes.
- When modifying parsers, respect `ParserBase`’s `TimeToWaitBetweenMaxAmountOfRequests` and `AllowedRequestsPerTimespan`.
- To simulate alerts, manipulate Prometheus metrics via `curl`ing the `/metrics` endpoint inside the bot container or by temporarily lowering alert thresholds.

---

End of briefing.
