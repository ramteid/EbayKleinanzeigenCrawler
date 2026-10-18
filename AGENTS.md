# AGENTS BRIEFING

This document is written for automation agents that need a fast, accurate view of the repository. It summarizes runtime behavior, architecture, dependencies, and operational concerns.

---

## 1. What This Project Does

Kleinanzeigen Crawler is a .NET 8 console application that:

- Reads subscription definitions from Telegram conversations.
- Periodically queries Kleinanzeigen and WG-Gesucht search URLs.
- Applies include/exclude keyword filtering against ad titles/descriptions.
- Notifies subscribers via Telegram when new matches appear.
- Exposes Prometheus-compatible metrics and ships with a Grafana/Alertmanager stack.

The application is designed to run continuously inside Docker. A Redis instance stores hashes of processed URLs; subscriber metadata lives in JSON files.

---

## 2. High-Level Architecture

```
                        +-----------------------+
                        | TelegramManager     |
                        |  - Telegram Bot API   |
                        |  - Buttons/UI Flow    |
                        +-----------+-----------+
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
| `TelegramManager` | `Manager/TelegramManager.cs` | Handles Telegram polling, button-based conversations, session control, subscription CRUD and command routing. |
| `SessionManager` | `Manager/SessionManager.cs` | Maintains per-user session data with automatic timeout cleanup. |
| `SubscriptionService` | `Manager/SubscriptionService.cs` | Business logic for validating URLs, enforcing result limits, updating subscriber data. |
| `SubscriptionHandler` | `Subscriptions/SubscriptionHandler.cs` | Infinite loop that queries subscriptions, feeds parsers, matches results, calls Telegram notifications. |
| `ParserProvider` & `ParserBase` | `Parser/` | Provides parser implementations (Kleinanzeigen & WG-Gesucht) with consistent rate limiting and error handling. |
| `RedisUrlTracker` | `Persistence/RedisUrlTracker.cs` | SHA256 hashes of processed URLs with 90-day TTL, prevents duplicate notifications. |
| `SubscriptionPersistence` & `JsonStorage` | `Persistence/` | Thread-safe JSON persistence of `Subscriber` objects under `./vol`. |
| `MetricsService` | `Monitoring/MetricsService.cs` | Prometheus counters, gauges, histograms, summaries used throughout the app. |

---

## 3. Data & Persistence

### Subscriber Data
- Stored in `./vol/Subscribers.json`.
- Managed via `SubscriptionPersistence` using the `IDataStorage` abstraction (`JsonStorage`).
- Automatically restored on startup; saved after each mutation.

### Processed URLs
- Stored exclusively in Redis (`redis` service in docker-compose).
- URLs are hashed (SHA256) and stored per subscription with 90-day TTL.
- `RedisUrlTracker` handles `AddOrUpdate`, `IsUrlProcessed`, `UpdateLastFound`, etc.

### Logs
- Serilog writes rotating files to `./vol/logfile.txt` (1 MB cap, 90-day retention).

---

## 4. Telegram Bot Flow

1. Bot token is read from `TELEGRAM_BOT_TOKEN`.
2. `TelegramManager` polls messages & callback queries.
3. Slash commands (`/start`, `/help`, `/menu`, `/cancel`) still work but buttons are the primary UX.
4. Each user gets a `TelegramSession` stored in memory and keyed by chat ID.
5. Subscription wizard collects URL, include keywords, exclude keywords, initial pull preference, and title.
6. Subscriptions persist immediately and are picked up by the background handler.
7. Setting `ALLOW_NEW_USERS=false` disallows new subscribers; existing ones continue to work.

---

## 5. Crawling Loop

- Implemented in `SubscriptionHandler.ProcessAllSubscriptionsAsync`.
- Steps:
  1. Restore Redis tracker connection.
  2. Fetch enabled subscriptions.
  3. Group by parser to respect provider rate limits.
  4. For each subscription: fetch pages, parse results, check Redis, run keyword matching, notify Telegram, update Redis.
  5. Wait for `CHECK_INTERVAL_SECONDS` (default 60) and repeat.
- Metrics recorded: subscription processing success, HTTP requests, parse errors, notification latency, etc.

---

## 6. Monitoring Stack

Docker Compose services:
- `bot`: the crawler.
- `redis`: URL tracker backend (internal network only).
- `prometheus`: scrapes bot metrics (not exposed to host).
- `alertmanager`: handles Prometheus alerts (webhook target configurable at `monitoring/alertmanager.yml`).
- `grafana`: exposed on `localhost:3000` for dashboards and alert visualization.

Prometheus alerts (see `monitoring/alerts.yml`) include parse error bursts, HTTP error rate, stuck processing, etc. Alertmanager currently points to a placeholder webhook (`http://alert-webhook:8080/alerts`); update this to a real sink before production use.

---

## 7. Configuration Reference

| Variable | Default | Description |
|----------|---------|-------------|
| `TELEGRAM_BOT_TOKEN` | — (required) | Bot token from @BotFather. |
| `ALLOW_NEW_USERS` | `true` | If `false`, new Telegram users cannot register. |
| `ENABLE_PROMETHEUS` | `true` | Starts embedded metrics server (Kestrel). |
| `PROMETHEUS_PORT` | `9090` | Port used by the bot container for metrics (internal network). |
| `CHECK_INTERVAL_SECONDS` | `60` | Delay between crawl passes. |
| `SESSION_TIMEOUT_MINUTES` | `15` | Session inactivity timeout. |
| `DATA_PATH` | `data` | Relative path inside container mapped to `./vol`. |
| `REDIS_CONNECTION_STRING` | `redis:6379,abortConnect=false` | Connection string passed to `RedisUrlTracker`. |
| `GRAFANA_PASSWORD` | `admin` | Grafana admin password (docker-compose). |

`.env` or `.env.example` contains typical overrides. Docker Compose loads this file automatically.

---

## 8. Running & Testing

### Docker Workflow
```bash
docker compose build
docker compose up -d
docker compose logs -f bot
docker compose down
```

Grafana UI: `http://localhost:3000` (default credentials `admin / GRAFANA_PASSWORD`).

### Local Development
```bash
dotnet restore
dotnet build
cd KleinanzeigenCrawler
dotnet run                # requires TELEGRAM_BOT_TOKEN env var
```

### Tests
`dotnet test` runs the unit test suite (SessionManager, SubscriptionService, Parser parsing behavior, etc.). Tests are located under `KleinanzeigenCrawler.Tests`.

---

## 9. Common Pitfalls

- **Missing Redis**: the app crashes if it cannot connect; ensure `redis` service is running in docker-compose or update `REDIS_CONNECTION_STRING`.
- **Mobile URLs**: subscription creation rejects non-HTTPS desktop URLs.
- **Too many results**: subscriptions with >50 estimated results are rejected to protect the provider.
- **Rate limiting**: if you see 429/Retry-After, the QueryExecutor automatically backs off, but excessive subscriptions may still breach limits.
- **Alertmanager webhook**: set a real endpoint; default placeholder goes nowhere.
- **Data path**: `DATA_PATH` should stay inside the mounted volume (`./vol`) or data will be lost when the container restarts.

---

## 10. Repository Layout

```
KleinanzeigenCrawler/
  Configuration/          # AppConfiguration, ConfigurationLoader
  Interfaces/             # Contracts (IParser, IDataStorage, IOutgoingNotifications, etc.)
  Manager/                # TelegramManager, SessionManager, SubscriptionService, Formatting helpers
  Models/                 # Subscription, Subscriber, TelegramSession, AddSubscriptionData, Result
  Monitoring/             # MetricsService
  Parser/                 # ParserBase, ParserProvider, concrete site parsers
  Persistence/            # JsonStorage, SubscriptionPersistence, RedisUrlTracker
  Query/                  # QueryExecutor (rate-limited HTTP client), QueryCounter, UserAgentProvider
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
- Always check `docker compose logs bot` when debugging Telegram issues; Serilog logs are verbose.
- Use Grafana dashboards (`monitoring/grafana/dashboards/kleinanzeigen-crawler.json`) to verify parsing health before deploying changes.
- When modifying parsers, respect `ParserBase`’s `TimeToWaitBetweenMaxAmountOfRequests` and `AllowedRequestsPerTimespan`.
- To simulate alerts, manipulate Prometheus metrics via `curl`ing the `/metrics` endpoint inside the bot container or by temporarily lowering alert thresholds.

---

End of briefing.
