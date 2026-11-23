# Aiursoft DotNetWatchDog

<a name="dotnetwatchdog"></a>
[![MIT licensed](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/AiursoftWeb/dotnetwatchdog/blob/master/LICENSE)
[![Pipeline stat](https://badge-proxy.aiursoft.com/aiursoft/dotnetwatchdog/badges/master/pipeline.svg)](https://gitlab.aiursoft.com/aiursoft/dotnetwatchdog/-/pipelines)
[![Test Coverage](https://badge-proxy.aiursoft.com/aiursoft/dotnetwatchdog/badges/master/coverage.svg)](https://gitlab.aiursoft.com/aiursoft/dotnetwatchdog/-/pipelines)
[![S360 compliance](https://s360.aiursoft.com/badge.svg?project=aiursoft%2Fdotnetwatchdog)](https://s360.aiursoft.com/SupplyChain?tab=projects&project=aiursoft%2Fdotnetwatchdog&scope=baseline&ecosystem=all)
[![NuGet version (Aiursoft.DotNetWatchDog)](https://img.shields.io/nuget/v/Aiursoft.DotNetWatchDog.svg)](https://www.nuget.org/packages/Aiursoft.DotNetWatchDog/)
[![Man hours](https://manhours.aiursoft.com/r/github.com/aiursoftweb/dotnetwatchdog.svg)](https://manhours.aiursoft.com/r/github.com/aiursoftweb/dotnetwatchdog.html)

Monitors ASP.NET Core runtime errors and application exceptions in ClickHouse, reporting incidents and investigation links to AlertCenter.

## Overview

### What is DotNetWatchDog?

DotNetWatchDog is a long-running watchdog service written in .NET 10 that periodically inspects application runtime logs in ClickHouse (`AspNetCoreLogs` database) and reports active error bursts to [AlertCenter](https://alertcenter.aiursoft.com).

It succeeds and replaces the legacy `ClickHouseAppLogErrorMonitor` service with full architectural symmetry to `PipelineWatchDog` and `LogWatchDog`.

### Key Capabilities

- **Automatic Service Discovery**: Discovers all active service log tables in ClickHouse dynamically every cycle.
- **Error Counting**: Aggregates `Error` (LogLevel 4) and `Critical` (LogLevel 5) events across sliding 15-minute windows every 5 minutes.
- **Escalation Rules**:
  - $\ge$ 3 errors: triggers or maintains **Sev4**
  - $\ge$ 5 errors: escalates to **Sev3**
  - $\ge$ 15 errors: escalates to **Sev2**
  - Severity is never downgraded; only upgraded upon increased error count.
- **Automated Resolution**: Automatically resolves the AlertCenter incident when the 15-minute error count drops back to zero.
- **Direct Investigation Links**: Provides pre-filled ClickHouse Play query URLs and detailed diagnostic SQL queries directly in incident notifications.
- **Masked Sample Snippets**: Extracts up to 3 recent error call stacks and messages, automatically redacting authorization tokens, credentials, and sensitive parameters.
- **Durable Local State**: Persists known table identities and pending incident receipts in local SQLite (`state.sqlite3`) with file-level process locking (`monitor.lock`) to ensure exactly-once alerting.

## Installation

Install the CLI globally with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```bash
dotnet tool install --global Aiursoft.DotNetWatchDog
```

Update an existing installation:

```bash
dotnet tool update --global Aiursoft.DotNetWatchDog
```

Remove the CLI:

```bash
dotnet tool uninstall --global Aiursoft.DotNetWatchDog
```

## Deployment with Docker

Run the container as a managed background service:

```bash
docker run -d --name dotnet-watchdog \
  --env-file dotnet-watchdog.env \
  -v dotnet-watchdog-data:/data \
  hub.aiursoft.com/aiursoft/dotnetwatchdog:latest
```

The container runs as UID `1654:1654`. Mount persistent storage to `/data` so pending reports and known table discovery state persist across restarts.

## Configuration

Configure the service using environment variables:

| Variable | Description | Default |
|---|---|---|
| `NODE_NAME` | Stable node/server identifier (e.g. `proart`, `global`). | *Required* |
| `CLICKHOUSE_DATABASE` | Target ClickHouse database containing application logs. | `AspNetCoreLogs` |
| `CLICKHOUSE_URL` | ClickHouse HTTP endpoint (e.g. `http://clickhouse_clickhouse:8123`). | `http://clickhouse_clickhouse:8123` |
| `CLICKHOUSE_USER` | ClickHouse database username. | `default` |
| `CLICKHOUSE_PASSWORD` | ClickHouse database password. | `""` |
| `CLICKHOUSE_CONNECTION_STRING` | Optional custom connection string overriding the URL/credentials above. | `""` |
| `CLICKHOUSE_PUBLIC_URL` | Optional public URL to ClickHouse Play (e.g. `https://clickhouse-proart.aiursoft.com`). | `""` |
| `ALERTCENTER_URL` | AlertCenter API base URL (e.g. `https://alertcenter.aiursoft.com`). | *Required* |
| `ALERTCENTER_API_KEY` | AlertCenter API token with incident query, create, modify, and resolve permissions. | *Required* |
| `STATE_DIR` | Directory for persistent state and file locks. | `/data` |

## Health Check & CLI Commands

- `dotnet-watchdog`: Starts the 5-minute background watchdog loop.
- `dotnet-watchdog --once`: Executes a single scan cycle and exits with status 0 (success) or 1 (failure).
- `dotnet-watchdog --healthcheck`: Exits with 0 if the last successful scan occurred within the past 15 minutes, otherwise 1.

## Contributing

1. Clone the repository: `git clone https://gitlab.aiursoft.com/aiursoft/DotNetWatchDog.git`
2. Restore and test: `dotnet test`
3. Run code inspection: `./lint.sh`
