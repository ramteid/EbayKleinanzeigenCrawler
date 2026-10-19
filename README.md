# Kleinanzeigen Crawler

Modern .NET 8 crawler that watches classified ads on **kleinanzeigen.de** and **wg-gesucht.de**, filters the results using customizable keywords, and notifies subscribers through a Telegram bot with a button-based UI. Prometheus + Grafana dashboards and Alertmanager integrations provide full observability.

---

## ✨ Features

- **Instant Telegram notifications** (usually within 0–5 minutes).
- **Include/exclude keyword filtering** applied to both title and description.
- **Multi-user, multi-subscription** support with per-user sessions.
- **Button-first Telegram UX** (no manual commands needed, though `/start` and friends still work).
- **Redis-backed URL deduplication** with SHA256 hashing and a 90-day TTL.
- **Prometheus metrics, Grafana dashboards, Alertmanager rules** bundled with Docker Compose.
- **Rate limiting respect**: QueryExecutor throttles requests (40 req / 5 min for Kleinanzeigen).
- **JSON persistence** for subscriber data (easy to back up with `./vol`).

---

## 📦 Supported Providers

| Provider | Parser | Notes |
|----------|--------|-------|
| https://www.kleinanzeigen.de | `KleinanzeigenParser` | Desktop URLs only. URLs of the former domain ebay-kleinanzeigen.de are rejected, it answers with 410 Gone. |
| https://www.wg-gesucht.de | `WgGesuchtParser` | Desktop URLs only. |

---

## 🚀 Quick Start

### 1. Requirements
- Docker with Compose v2 (`docker compose`, version 2.30 or newer, the Compose secrets read environment variables) **or** .NET SDK 8.0+
- A Telegram Bot token from [@BotFather](https://core.telegram.org/bots#3-how-do-i-create-a-bot)

### 2. Prepare environment

```bash
cp .env.example .env
vi .env         # set TELEGRAM_BOT_TOKEN and optional overrides
```

Minimum `.env`:
```
TELEGRAM_BOT_TOKEN=123456789:ABCDEF...
```

### 3. Run with Docker

```bash
docker compose build
docker compose up -d
docker compose logs -f bot
```

- Grafana: `http://<docker-host>:3333` (user `admin`, password `GRAFANA_PASSWORD`, required in `.env`)
- Alerts are sent through the Telegram bot to `ALERT_TELEGRAM_CHAT_ID` (required in `.env`, that chat must have started a conversation with the bot).
- Prometheus and Redis stay inside the Docker network. The Alertmanager UI is published on `127.0.0.1:9093` only.

Stop everything: `docker compose down`.

### 4. Run locally without Docker

```bash
dotnet restore
dotnet build
export TELEGRAM_BOT_TOKEN=123456789:ABCDEF...
cd KleinanzeigenCrawler
dotnet run
```

Redis is required even outside Docker. Point `REDIS_CONNECTION_STRING` at your instance (e.g., `localhost:6379`).

---

## 🐞 VS Code debugging

Launch the `Docker Compose: Bot (.NET)` profile from VS Code (requires the Docker extension) to debug the crawler inside containers, mirroring the workflow in the [official docs](https://code.visualstudio.com/docs/containers/debug-netcore):

1. Ensure Docker is running and `.env` contains your Telegram token.
2. Select `Docker Compose: Bot (.NET)` in the Run and Debug view and press `F5`.
3. The Docker extension executes the `docker-compose: debug` task, which installs `vsdbg` into `./.vsdbg`, composes `docker-compose.yml` + `docker-compose.debug.yml`, builds the bot with `BUILD_CONFIGURATION=Debug`, and starts the entire stack (the bot waits for the debugger via `tail -f /dev/null`).
4. VS Code mounts `./.vsdbg` at `/remote_debugger` and launches `vsdbg` inside the `bot` container via the `docker` debug type—no manual `pipeTransport` wiring needed.
5. Stopping the debug session triggers the `docker-compose: down` task to tear everything down cleanly.

Customize the behavior through `.vscode/launch.json`, `.vscode/tasks.json`, or `docker-compose.debug.yml` if you need to adjust debugger actions, compose parameters, or build arguments.

---

## ⚙️ Configuration

| Variable | Default | Purpose |
|----------|---------|---------|
| `TELEGRAM_BOT_TOKEN` | _required_ | Bot token from BotFather. |
| `ALLOW_NEW_USERS` | `true` | If `false`, new chats are rejected (existing users unaffected). |
| `ENABLE_PROMETHEUS` | `true` | Starts embedded Kestrel metrics server. |
| `PROMETHEUS_PORT` | `9090` | Metrics listener inside the bot container. |
| `CHECK_INTERVAL_SECONDS` | `60` | Delay between crawler passes. |
| `SESSION_TIMEOUT_MINUTES` | `15` | Telegram session inactivity timeout. |
| `DATA_PATH` | `data` | Directory inside container mapped to `./vol`. |
| `REDIS_CONNECTION_STRING` | `redis:6379,abortConnect=false` | Connection string for URL tracker. |
| `GRAFANA_PASSWORD` | _required_ | Grafana admin password in docker-compose. |
| `ALERT_TELEGRAM_CHAT_ID` | _required_ | Telegram chat that receives the alerts in docker-compose. The alerts are sent by the bot of `TELEGRAM_BOT_TOKEN`. |

All environment variables can be set via `.env`, shell exports, or your container orchestrator.

---

## 🏗️ Architecture Overview

1. **TelegramManager** (button UI) + **SessionManager** (per-user state) accept subscription commands.
2. **SubscriptionService** validates URLs, keywords, and ensures estimated results ≤ 50.
3. **SubscriptionPersistence** stores subscriber data in `./vol/Subscribers.json`.
4. **SubscriptionHandler** loops forever:
   - Groups subscriptions by parser type.
   - Uses `ParserBase` implementations to fetch + parse HTML.
   - Uses `RedisUrlTracker` to skip already processed URLs.
   - Applies keyword matching and sends notifications via Telegram.
5. **MetricsService** exposes counters/gauges/histograms for Prometheus.
6. **Docker Compose** bundles Redis, Prometheus, Grafana, Alertmanager, and the bot.

Key directories:
```
KleinanzeigenCrawler/
  Configuration/          # AppConfiguration, loader
  Interfaces/             # Contracts (IParser, IDataStorage, etc.)
  Manager/                # TelegramManager, SessionManager, SubscriptionService
  Models/                 # Domain objects (Subscription, Subscriber, TelegramSession, Result)
  Monitoring/             # MetricsService
  Parser/                 # ParserBase, ParserProvider, concrete parsers
  Persistence/            # JsonStorage, SubscriptionPersistence, RedisUrlTracker
  Query/                  # QueryExecutor, QueryCounter, UserAgentProvider
  Subscriptions/          # SubscriptionHandler

KleinanzeigenCrawler.Tests/  # xUnit tests
monitoring/                      # Prometheus/Grafana/Alertmanager configs
docker-compose.yml               # Full stack definition
```

---

## 📊 Observability

- **Metrics**: `/metrics` exposed inside the Docker network on port `PROMETHEUS_PORT`.
- **Grafana dashboards**: provisioned from `monitoring/grafana/dashboards/kleinanzeigen-crawler.json`.
- **Alerting**: `monitoring/alerts.yml` defines alerts such as:
  - High parse error rate
  - Crawler down
  - No subscriptions processed
  - HTTP error spikes / rate limit breaches
  - Notification failures
  - Aggregate error bursts (replacement for legacy Telegram admin notifications)
- **Alertmanager**: sends the alerts through the Telegram bot to `ALERT_TELEGRAM_CHAT_ID`.

---

## 💾 Data & Persistence

- `./data/Subscribers.json` – subscriber data (multi-user aware).
- `./data/logfile<yyyyMMdd>.txt` – daily rolling logs, 1 MB per file, kept for 90 days.
- Redis stores processed URL hashes (no JSON fallback).
- Remove/backup `./data` to reset subscribers and logs; Redis data persists in the `kleinanzeigen-bot-redis-data` volume.

---

## 🧪 Testing

```bash
dotnet test
```

Tests target SessionManager behavior, SubscriptionService validation, ParserBase helpers, etc. Extend the suite when touching business logic.

The alert rules have their own tests:

```bash
cd monitoring
docker run --rm -v "$PWD:/w" -w /w --entrypoint promtool prom/prometheus:latest test rules alerts_test.yml
```

---

## 🛠️ Troubleshooting

| Symptom | Check |
|---------|-------|
| Bot silent | `docker compose logs bot`, ensure `TELEGRAM_BOT_TOKEN` and Redis connection are valid. |
| Duplicate notifications | Confirm Redis is reachable; hashed URLs expire after 90 days. |
| No metrics | `ENABLE_PROMETHEUS` must be `true`; metrics exposed on internal port `9090`. |
| Alert spam | Inspect Grafana dashboards; adjust thresholds in `monitoring/alerts.yml`. |
| Rate limit warnings | Reduce number of subscriptions, or widen `CHECK_INTERVAL_SECONDS`. |

---

**Reminder:** Only desktop HTTPS URLs are supported. Respect the terms of service of the providers you crawl, and use sensible rate limits. Happy searching!
