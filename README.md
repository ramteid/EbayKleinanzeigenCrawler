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
| https://www.kleinanzeigen.de | `KleinanzeigenParser` | Formerly ebay-kleinanzeigen.de. Desktop URLs only. |
| https://www.wg-gesucht.de | `WgGesuchtParser` | Desktop URLs only. |

---

## 🚀 Quick Start

### 1. Requirements
- Docker & Docker Compose **or** .NET SDK 8.0+
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

- Grafana: http://localhost:3000 (default `admin` / `GRAFANA_PASSWORD`)
- Prometheus/Alertmanager/Redis stay inside the Docker network (no exposed ports).

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
| `GRAFANA_PASSWORD` | `admin` | Grafana admin password in docker-compose. |

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
  - No subscriptions processed
  - HTTP error spikes / rate limit breaches
  - Notification failures
  - Aggregate error bursts (replacement for legacy Telegram admin notifications)
- **Alertmanager**: sends webhooks to `http://alert-webhook:8080/alerts` by default. Update this to your Slack/Teams/e-mail gateway.

---

## 💾 Data & Persistence

- `./vol/Subscribers.json` – subscriber data (multi-user aware).
- `./vol/logfile.txt` – rolling structured logs.
- Redis stores processed URL hashes (no JSON fallback).
- Remove/backup `./vol` to reset subscribers and logs; Redis data persists in `redis-data` volume.

---

## 🧪 Testing

```bash
dotnet test
```

Tests target SessionManager behavior, SubscriptionService validation, ParserBase helpers, etc. Extend the suite when touching business logic.

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
