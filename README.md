# Kleinanzeigen Crawler

Modern .NET 9 crawler that watches classified ads on **kleinanzeigen.de** and **wg-gesucht.de**, filters the results using customizable keywords, and notifies subscribers through Telegram (button-based UI) and Signal (text-based UI), which can run at the same time. Prometheus + Grafana dashboards and Alertmanager integrations provide full observability.

---

## ✨ Features

- **Instant notifications** via Telegram and/or Signal (usually within 0–5 minutes).
- **Include/exclude keyword filtering** applied to both title and description.
- **Multi-user, multi-subscription** support with per-user sessions.
- **Button-first Telegram UX** (no manual commands needed, though `/start` and friends still work).
- **Text-based Signal UX** with step-by-step prompts (`/add`, `/list`, `/edit`, ...).
- **Redis-backed URL deduplication** with SHA256 hashing and a 90-day TTL.
- **Prometheus metrics, Grafana dashboards, Alertmanager rules** bundled with Docker Compose.
- **Rate limiting respect**: QueryExecutor throttles requests (40 req / 5 min for Kleinanzeigen).
- **JSON persistence** for subscriber data (easy to back up with `./data`).

---

## 📦 Supported Providers

| Provider | Parser | Notes |
|----------|--------|-------|
| https://www.kleinanzeigen.de | `KleinanzeigenParser` | Desktop URLs only. URLs of the former domain ebay-kleinanzeigen.de are rejected, it answers with 410 Gone. |
| https://www.wg-gesucht.de | `WgGesuchtParser` | Desktop URLs only. |

---

## 🚀 Quick Start

### 1. Requirements
- Docker with Compose v2 (`docker compose`, version 2.30 or newer, the Compose secrets read environment variables) **or** .NET SDK 9.0+
- At least one messenger: a Telegram Bot token from [@BotFather](https://core.telegram.org/bots#3-how-do-i-create-a-bot) and/or a dedicated Signal number (see [Signal](#signal))

### 2. Prepare environment

```bash
cp .env.example .env
vi .env         # set TELEGRAM_BOT_TOKEN and/or SIGNAL_NUMBER and optional overrides
```

Minimum `.env` for Telegram (for Signal see [Signal](#signal)):
```
TELEGRAM_BOT_TOKEN=123456789:ABCDEF...
GRAFANA_PASSWORD=choose-a-strong-password
ALERT_TELEGRAM_CHAT_ID=123456789
```

`ALERT_TELEGRAM_CHAT_ID` is the Telegram chat that receives the monitoring alerts, sent by the same bot. That chat must have started a conversation with the bot.

### 3. Run with Docker

```bash
docker compose build
docker compose up -d
docker compose logs -f bot
```

- Grafana: `http://<docker-host>:3333` (user `admin`, password `GRAFANA_PASSWORD`)
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

1. Ensure Docker is running and `.env` contains your messenger settings (Telegram token and/or Signal number).
2. Select `Docker Compose: Bot (.NET)` in the Run and Debug view and press `F5`.
3. The Docker extension executes the `docker-compose: debug` task, which installs `vsdbg` into `./.vsdbg`, composes `docker-compose.yml` + `docker-compose.debug.yml`, builds the bot with `BUILD_CONFIGURATION=Debug`, and starts the entire stack (the bot waits for the debugger via `tail -f /dev/null`).
4. VS Code mounts `./.vsdbg` at `/remote_debugger` and launches `vsdbg` inside the `bot` container via the `docker` debug type—no manual `pipeTransport` wiring needed.
5. Stopping the debug session triggers the `docker-compose: down` task to tear everything down cleanly.

Customize the behavior through `.vscode/launch.json`, `.vscode/tasks.json`, or `docker-compose.debug.yml` if you need to adjust debugger actions, compose parameters, or build arguments.

---

## Signal

Signal has no bot API. The crawler talks to a [signal-cli-rest-api](https://github.com/bbernhard/signal-cli-rest-api) container (`signal-api`, json-rpc mode), which is part of `docker-compose.yml` behind the compose profile `signal`.

1. Get a **dedicated** Signal number for the bot. Without an [access password](#access-password) everyone who writes to that number can use the bot, so do not use your personal number.
2. Start the container: `COMPOSE_PROFILES=signal docker compose up -d signal-api`.
3. Register the number (or link it as a secondary device) through the API on `http://127.0.0.1:8085`, see the [API documentation](https://bbernhard.github.io/signal-cli-rest-api/) (`/v1/register`, `/v1/qrcodelink`). Restart `signal-api` if it does not pick up the new account.
4. Set `SIGNAL_NUMBER` (international format) and `COMPOSE_PROFILES=signal` in `.env`, then run `docker compose up -d`.

Users write to the number in a one-to-one chat. Commands: `/add`, `/list`, `/edit`, `/enable`, `/disable`, `/delete`, `/cancel`, `/help`. Group chats are ignored. Subscriptions belong to the messenger they were created in, and Telegram and Signal users do not see each other.

`docker-compose.yml` also uses `TELEGRAM_BOT_TOKEN` to deliver the monitoring alerts, so Telegram runs next to Signal in that setup. The bot only starts receiving once the number is registered; until then it logs the reason and retries.

---

## ⚙️ Configuration

| Variable | Default | Purpose |
|----------|---------|---------|
| `TELEGRAM_BOT_TOKEN` | _optional_ | Bot token from BotFather. Telegram is disabled when empty. |
| `SIGNAL_NUMBER` | _optional_ | Dedicated Signal number of the bot, e.g. `+491701234567`. Signal is disabled when empty. |
| `SIGNAL_API_URL` | `http://signal-api:8080` | Base URL of the signal-cli-rest-api container (json-rpc mode). |
| `ALLOW_NEW_USERS` | `true` | If `false`, new users are rejected on every messenger (existing users unaffected). |
| `ACCESS_PASSWORD` | _optional_ | Password that new users must send once, at least 8 characters, case-sensitive. See [Access password](#access-password). |
| `ENABLE_PROMETHEUS` | `true` | Starts embedded Kestrel metrics server. |
| `PROMETHEUS_PORT` | `9090` | Metrics listener inside the bot container. |
| `CHECK_INTERVAL_SECONDS` | `60` | Delay between crawler passes. |
| `SESSION_TIMEOUT_MINUTES` | `15` | Conversation session inactivity timeout. |
| `DATA_PATH` | `data` | Directory inside container mapped to `./data`. |
| `REDIS_CONNECTION_STRING` | `redis:6379,abortConnect=false` | Connection string for URL tracker. |
| `GRAFANA_PASSWORD` | _required_ | Grafana admin password in docker-compose. |
| `ALERT_TELEGRAM_CHAT_ID` | _required_ | Telegram chat that receives the alerts in docker-compose. The alerts are sent by the bot of `TELEGRAM_BOT_TOKEN`, which docker-compose therefore always requires. |

At least one of `TELEGRAM_BOT_TOKEN` and `SIGNAL_NUMBER` is required; with both set, both messengers run simultaneously. Unset or empty numeric and boolean variables use the default, an invalid value stops the start-up with a message. All environment variables can be set via `.env`, shell exports, or your container orchestrator.

### Access password

With `ACCESS_PASSWORD` set, a new user on any messenger is let in only after sending the password once:

- The first message of an unknown user is answered with a request for the password and is not counted. Existing subscribers are never asked.
- Whoever sends the password becomes a subscriber for good.
- A user who sends a wrong password 10 times is blocked for good: the bot does not answer its messages anymore.
- Failed attempts and blocks are stored per messenger in `./data/AccessAttempts.json`. To unblock a user, stop the bot, remove its entry from that file and start the bot again.
- `ALLOW_NEW_USERS=false` still rejects new users, whatever the password.

---

## 🏗️ Architecture Overview

1. **TelegramManager** (button UI) and **SignalManager** (text UI) + **SessionManager** (per-user state) accept subscription commands once the **AccessGate** admitted the sender (optional password). Both run simultaneously and only notify their own subscribers.
2. **SubscriptionService** validates URLs, keywords, and ensures estimated results ≤ 50.
3. **SubscriptionPersistence** stores subscriber data in `./data/Subscribers.json`.
4. **SubscriptionHandler** loops forever:
   - Groups subscriptions by parser type.
   - Uses `ParserBase` implementations to fetch + parse HTML.
   - Uses `RedisUrlTracker` to skip already processed URLs.
   - Applies keyword matching and sends notifications through all messengers (`CompositeNotifier`).
5. **MetricsService** exposes counters/gauges/histograms for Prometheus.
6. **Docker Compose** bundles Redis, Prometheus, Grafana, Alertmanager, and the bot.

Key directories:
```
KleinanzeigenCrawler/
  Configuration/          # AppConfiguration, loader
  Interfaces/             # Contracts (IParser, IMessenger, ISignalClient, etc.)
  Manager/                # TelegramManager, SignalManager, TextConversationHandler, AccessGate, SessionManager, SubscriptionService
  Models/                 # Domain objects (Subscription, Subscriber, ConversationSession, Result)
  Monitoring/             # MetricsService
  Parser/                 # ParserBase, ParserProvider, concrete parsers
  Persistence/            # JsonStorage, SubscriptionPersistence, AccessAttemptPersistence, RedisUrlTracker
  Query/                  # QueryExecutor, QueryCounter, UserAgentProvider
  Signal/                 # SignalRestClient (signal-cli-rest-api gateway)
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
  - Crawler down
  - High parse error rate
  - No subscriptions processed
  - HTTP error spikes / rate limit breaches
  - Notification failures
  - Aggregate error bursts
- **Alertmanager**: sends the alerts through the Telegram bot to `ALERT_TELEGRAM_CHAT_ID`.

---

## 💾 Data & Persistence

- `./data/Subscribers.json` – subscriber data (multi-user aware).
- `./data/AccessAttempts.json` - wrong password attempts and blocked users per messenger.
- `./data/logfile<yyyyMMdd>.txt` – daily rolling logs, 1 MB per file, kept for 90 days.
- Redis stores processed URL hashes (no JSON fallback).
- Remove/backup `./data` to reset subscribers and logs; Redis data persists in the `kleinanzeigen-bot-redis-data` volume.
- A `Subscribers.json` or `AccessAttempts.json` that exists but cannot be parsed stops the start-up instead of being overwritten. Repair or restore it first.

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
| Bot silent | `docker compose logs bot`, ensure the messenger settings (`TELEGRAM_BOT_TOKEN`, `SIGNAL_NUMBER`) and the Redis connection are valid. |
| Signal bot silent | `docker compose logs bot signal-api`. The number must be registered with `signal-api`, which must run with `MODE=json-rpc`; the bot logs the reason and retries. |
| Bot exits right after start | `docker compose logs bot`: invalid configuration values, an unreadable `Subscribers.json` or `AccessAttempts.json` or an unreachable Redis are reported before the exit. |
| Duplicate notifications | Confirm Redis is reachable; hashed URLs expire after 90 days. |
| No metrics | `ENABLE_PROMETHEUS` must be `true`; metrics exposed on internal port `9090`. |
| Alert spam | Inspect Grafana dashboards; adjust thresholds in `monitoring/alerts.yml`. |
| Rate limit warnings | Reduce number of subscriptions, or widen `CHECK_INTERVAL_SECONDS`. |

---

**Reminder:** Only desktop HTTPS URLs are supported. Respect the terms of service of the providers you crawl, and use sensible rate limits. Happy searching!
