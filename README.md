# Grafana to Coralogix Dashboard Migration Tool

[![.NET 9](https://img.shields.io/badge/.NET-9-512BD4)](https://dotnet.microsoft.com/download/dotnet/9.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Open-source .NET 9 CLI to **migrate Grafana dashboards to Coralogix custom dashboards** — bulk or
single-file, with pre-migration assessment, checkpoint/resume, and round-trip validation.

**Use when:** moving from Grafana Cloud or self-hosted Grafana to Coralogix native dashboards, or to
Coralogix-hosted Grafana with `grafana-import`.

```bash
git clone https://github.com/RomaLoginovskiy/grafana-to-coralogix-migrator.git
cd grafana-to-coralogix-migrator
```

| Migration goal | Commands |
|---|---|
| Native Coralogix custom dashboards | `migrate`, `convert`, `import`, `push` |
| Keep Grafana UI on Coralogix | `grafana-import` |

Capabilities:
- [Pre-migration assessment](#pre-migration-assessment-assess) of a set of dashboards (`assess`)
- Single-file conversion (`convert`)
- Single-file conversion + upload (`push`)
- Bulk migration from live Grafana (`migrate`)
- Download-only backup of live Grafana dashboards (`backup`)
- Bulk import from local files (`import`)
- Conversion + round-trip validation (`verify`)
- Grafana-to-Grafana publishing (`grafana-import`) — push exported dashboards into a Coralogix-hosted
  Grafana unchanged apart from datasource re-pointing, with a dry run and idempotent re-runs

---

## Table of Contents

- [Pre-Migration Assessment (`assess`)](#pre-migration-assessment-assess)
- [Quick Start — Interactive Migration](#quick-start--interactive-migration)
- [Step-by-Step Walkthrough](#step-by-step-walkthrough)
  - [Step 1 — Prerequisites](#step-1--prerequisites)
  - [Step 2 — Clone and configure](#step-2--clone-and-configure)
  - [Step 3 — Build](#step-3--build)
  - [Step 4 — Configure migration-settings.json](#step-4--configure-migration-settingsjson)
  - [Step 5 — Run interactive migration](#step-5--run-interactive-migration)
  - [Step 6 — Follow the guided prompts](#step-6--follow-the-guided-prompts)
  - [Step 7 — Monitor progress and resume](#step-7--monitor-progress-and-resume)
- [Resuming a session](#resuming-a-session)
- [How It Works](#how-it-works)
- [Supported Panel Types](#supported-panel-types)
- [Supported Query Languages](#supported-query-languages)
- [Project Structure](#project-structure)
- [Migration Settings Reference](#migration-settings-reference)
- [Supported Regions](#supported-regions)
- [Environment Variables](#environment-variables)
- [Other Commands](#other-commands)
- [Integration Settings and Live Test](#integration-settings-and-live-test)
- [Playwright Migration Validation](#playwright-migration-validation-grafana-vs-coralogix)
- [FAQ](#faq)
- [Troubleshooting](#troubleshooting)
- [License and Related Links](#license-and-related-links)

---

## Pre-Migration Assessment (`assess`)

Scan a directory of Grafana JSON exports or a backup ZIP before migration. `assess` converts each
dashboard in memory, reports a per-dashboard `Clean`, `Degraded`, `Rejected`, or `Failed` verdict, and
never uploads a dashboard. Live API validation requires the
[`cx` CLI](https://github.com/coralogix/cx-cli) on `PATH` plus either `CX_API_KEY` or a configured
`--profile`. Without the `cx` CLI, `assess` still runs conversion-only assessment.

```bash
CX_API_KEY="${CX_API_KEY:?Set CX_API_KEY before running assess}" \
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- \
  assess ./grafana-backup.zip --format markdown --output report.md --region eu1
```

| Argument/Flag | Description |
|---|---|
| `<input>` | Directory containing dashboard JSON files, scanned recursively, or a backup `.zip` |
| `-f`, `--format` | Report format: `text` or `markdown` (default: `text`) |
| `-o`, `--output` | Write the report to a file as well as stdout |
| `-p`, `--profile` | Optional configured `cx` CLI profile used for API validation |
| `-r`, `--region` | Coralogix region used for API validation (default: `eu1`) |

---

## Quick Start — Interactive Migration

The fastest way to migrate dashboards from Grafana to Coralogix:

```bash
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- migrate --interactive
```

This launches a fully guided terminal session — no flags to memorise. The tool walks you through selecting your Coralogix region, API key, Grafana API key, folder selection, folder mapping, and starts the migration.

---

## Step-by-Step Walkthrough

### Step 1 — Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- A **Grafana API token** with read access to dashboards and folders
- A **Coralogix API key** with dashboard write permissions

### Step 2 — Clone and configure

Clone the migrator before working with a Grafana dashboard JSON export:

```bash
git clone https://github.com/RomaLoginovskiy/grafana-to-coralogix-migrator.git
cd grafana-to-coralogix-migrator
dotnet restore GrafanaToCx.sln
```

Export your credentials so the tool picks them up automatically (recommended):

```bash
export GRAFANA_API_KEY=glsa_xxxxxxxxxxxx
export CX_API_KEY=cxtp_xxxxxxxxxxxx
```

If you skip this step the prompts will ask for them during the session.

### Step 3 — Build

```bash
dotnet build GrafanaToCx.sln
```

### Step 4 — Configure migration-settings.json

The settings file controls which Grafana region and Coralogix region to connect to. Open `src/GrafanaToCx.Cli/migration-settings.json` and set at minimum:

```json
{
  "grafana": {
    "region": "eu1",
    "folders": []
  },
  "coralogix": {
    "region": "eu1"
  },
  "credentials": {
    "grafanaApiKey": "",
    "cxApiKey": ""
  },
  "migration": {
    "checkpointFile": "migration-checkpoint.json",
    "reportFile": "migration-report.txt",
    "maxRetries": 5,
    "initialRetryDelaySeconds": 2
  }
}
```

Set `grafana.region` and `coralogix.region` to the correct region codes (see [Supported Regions](#supported-regions)). Leave `folders` empty to migrate all folders, or list specific folder names to limit scope.

Leave `credentials` empty if you exported environment variables — those take priority.

### Step 5 — Run interactive migration

```bash
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- migrate --interactive
```

### Step 6 — Follow the guided prompts

The session walks you through a sequence of prompts. Here is exactly what to expect:

**a) Coralogix session setup**

```
Coralogix region
> eu1
  eu2
  us1
  us2
  ap1
  ap2
  ap3
  in1

Coralogix API key:
```

Pick a region with the arrow keys — the list is the set the tool can actually resolve, so a typo can no
longer end the session. The pre-selected entry comes from the region you last used in this session if there
is one (see [Resuming a session](#resuming-a-session)), otherwise `coralogix.region` in the settings file,
falling back to `eu2` when that is missing or unrecognised. If `CX_API_KEY` is already exported, the key
prompt is skipped.

The same picker is used by **Settings → Change Coralogix region / endpoint** (pre-selected with the region
the session is currently on) and by **Grafana Import**, which asks separately because the session endpoint
is the REST API base rather than a Grafana URL. That one is pre-selected from the region last used for a
Grafana import, then `grafanaImport.region`, then the session's region.

It also appears on the `--interactive` flag paths — `import -I`, `verify -d … -I` and `grafana-import -I` —
each pre-selected from the settings region for that command. Passing `--endpoint` or `--region` skips the
picker: you have already named the target, and there is no way to reverse a URL back into a region to
pre-select. A region named on the command line is never silently corrected — `--region eu9` fails.

**b) Main menu**

```
╔══════════════════════════════════════════╗
║  Grafana → Coralogix Dashboard Converter ║
╚══════════════════════════════════════════╝

  1. Convert – Grafana JSON → CX JSON (local)
  2. Push – Push single dashboard to Coralogix
  3. Import – Import folder of dashboards
> 4. Migrate – Bulk migrate from Grafana   ← select this
  5. Settings – Change connection settings
  6. Cleanup – Backup and delete dashboards by folder
  7. Backup – Download Grafana dashboards to a local ZIP
  0. Exit
```

Use arrow keys to highlight **Migrate** and press Enter. Migrate publishes to the region you picked at
startup, not to whatever `coralogix.region` says.

Option **7 (Backup)** downloads dashboards from Grafana without converting or uploading anything —
see [`backup`](#backup). Note that the interactive session asks for a Coralogix region and key up
front regardless of which option you pick; run `backup` as a direct command to skip that.

**c) Grafana API key**

```
Grafana API key: ****
```

Skipped automatically if `GRAFANA_API_KEY` is already exported.

**d) Settings file path**

```
Settings file [migration-settings.json]:
```

Press Enter to accept the default, or type a custom path.

**e) Grafana folder selection**

```
Fetching folders from Grafana...

Select folders to migrate  (use space to select, enter to confirm)
  ◉ General
  ◯ Observability
  ◯ Platform
```

Space-bar to toggle, Enter to confirm. Select all folders you want to migrate.

**f) Folder nesting strategy**

```
Folder nesting strategy
> Nest all under a parent CX folder (preserves structure)
  Map each Grafana folder individually
```

- **Nest all under a parent** — the tool creates (or reuses) a single root folder in Coralogix and places each Grafana folder as a sub-folder beneath it. Best for a clean, grouped import.
- **Map each individually** — you choose a specific Coralogix target folder (or create a new one, or none) for each Grafana folder. Best when you need precise placement.

**g) Parent folder selection (if nesting)**

```
Select or create parent CX folder
> + Create new folder
  Existing Folder A
  Existing Folder B
```

Choose an existing root folder or create a new one. If you create one, enter its name at the next prompt.

The tool then creates sub-folders in Coralogix matching each selected Grafana folder name, and prints a confirmation:

```
Creating sub-folders under 'Grafana Migration'...
  'General'... OK (id: abc123)
  'Observability'... OK (id: def456)
```

**h) Migration plan review**

```
Migration plan:
  Grafana 'General'        →  CX 'General'
  Grafana 'Observability'  →  CX 'Observability'

Overwrite dashboards that already exist in Coralogix? [y/N]:
Proceed with migration? [Y/n]:
```

Review the mapping, decide whether to overwrite existing dashboards, then confirm.

**i) Checkpoint prompt (on subsequent runs)**

If a previous migration checkpoint exists with completed dashboards, you are asked:

```
Checkpoint 'migration-checkpoint.json' already has 42 completed dashboard(s).
Keeping it means those dashboards will be SKIPPED (not re-migrated).
Reset checkpoint and re-migrate all dashboards? [y/N]:
```

- Answer **N** (default) to resume and skip already-completed dashboards.
- Answer **Y** to wipe the checkpoint and start fresh.

**j) Migration runs**

The orchestrator processes each dashboard with automatic retries and logs progress. When complete, a summary is printed:

```
Migration complete.
  Completed : 47
  Skipped   : 0
  Failed    : 1

See migration-report.txt for details.
```

### Step 7 — Monitor progress and resume

Progress is saved after each dashboard to `migration-checkpoint.json`. If the run is interrupted, re-run the same command — completed dashboards are skipped automatically.

A human-readable summary is written to `migration-report.txt` after every run.

---

## Resuming a session

The interactive console remembers the answers you gave it, so a second run offers them back instead of
asking from scratch. Each console run has a short session id, printed in the banner at startup and again on
exit:

```
╔══════════════════════════════════════════╗
║  Grafana → Coralogix Dashboard Converter ║
╚══════════════════════════════════════════╝
  Session 4f9c1a02

...

Session saved as 4f9c1a02.
  Resume it with:  grafana-to-cx --resume 4f9c1a02
  Or the most recent with:  grafana-to-cx --continue
```

| Flag | Effect |
|---|---|
| `--resume <id>` | Resume that session. Any unambiguous prefix of the id works, like a git short hash |
| `--resume` | List stored sessions newest-first and pick one |
| `-c`, `--continue` | Resume the most recently used session |

An id that matches nothing, or matches more than one session, is an error naming the candidates — it never
quietly starts a fresh session, because the hardcoded defaults would then look like remembered ones and the
first thing you accepted by pressing Enter could be a root directory or a dry-run flag you never chose.

Remembered answers appear as prompt **defaults**, so every prompt is still shown and one Enter per prompt
re-runs a command identically. Nothing is silently reused. The console remembers the Coralogix region, the
settings file, and the per-command answers of the top-level menus — Grafana Import's region, root directory,
recursive and dry-run; Import's root directory; Convert's input and output; Push's input file; and the
directory (not the filename, which is timestamped) of Cleanup's backup zip. The folder-grouping and
folder-mapping prompts inside an import are not remembered: they describe one specific directory tree and
would go stale against a different one.

Sessions are stored one file per session under `~/.grafana-to-cx/sessions/`, written after every completed
action rather than only at exit, so an interrupted run does not lose what you already answered. The 20 most
recent are kept and older ones are pruned. Deleting the directory is safe — it only discards remembered
answers.

**Session files hold no credentials.** The Coralogix and Grafana API keys live in memory for the life of the
process and are never written to disk, so resuming always re-asks for the key (or reads `CX_API_KEY` /
`GRAFANA_API_KEY` from the environment). Within a single process the Grafana key is asked once, not once per
visit to the Migrate menu.

A corrupt or unreadable session file is warned about and skipped rather than fatal: being unable to recall
your last root directory is never a reason the console cannot start. This is deliberately unlike
`migration-checkpoint.json`, where a bad file means the record of what was already published is
untrustworthy and continuing would republish or skip real dashboards.

---

## How It Works

This migration tool supports observability platform migration workflows from Grafana Cloud,
self-hosted Grafana, and Grafana dashboard JSON export backups.

```text
Grafana Dashboard JSON
        │
        ▼
┌─────────────────────────────┐
│   GrafanaToCxConverter      │
│                             │
│  • Groups panels into       │
│    sections (row panels)    │
│  • Maps variables           │
│  • Maps time ranges         │
│                             │
│  Panel Converters:          │
│  ┌──────────────────────┐   │
│  │ LineChartConverter   │   │  PromQL / LogQL -> Lucene / Elasticsearch
│  │ GaugeConverter       │   │  Thresholds, stat panels
│  │ LogsConverter        │   │  Log panel with Lucene queries
│  │ MarkdownConverter    │   │  Text / markdown panels
│  └──────────────────────┘   │
└─────────────────────────────┘
        │
        ▼
Coralogix Custom Dashboard JSON
        │
        ├─ save locally (convert)
        ├─ upload via API (push / migrate / import)
        └─ upload + verify round-trip (verify)
```

`migrate` and `import` share the same resilience machinery — checkpoint/resume, retry with exponential
backoff, and a written report — differing only in where dashboards come from:

```text
migrate:  Grafana API  ──┐
                         ├──▶  convert ──▶ validate ──▶ publish to Coralogix
import:   local folder ──┘                              (checkpoint after every dashboard)
```

The core conversion logic is in `src/GrafanaToCx.Core`, while `src/GrafanaToCx.Cli` provides CLI commands, API interaction, interactive prompts, and migration orchestration.

---

## Supported Panel Types

| Grafana Panel | Coralogix Widget |
|---|---|
| Time series / Graph | Line chart |
| Stat / Gauge | Gauge |
| Table | Line chart (aggregated) |
| Logs | Log viewer |
| Text / Markdown | Markdown |

---

## Supported Query Languages

| Source | Conversion |
|---|---|
| PromQL | Passed through as-is |
| Loki / LogQL | Converted to Lucene via `LogqlToLuceneConverter` |
| Elasticsearch | Passed through as-is |

Coralogix custom dashboards use destination-native query formats. This migrator preserves PromQL for
metrics and converts Loki LogQL to Lucene for logs; Coralogix also supports DataPrime for native log and
trace analysis, but the converter does not generate DataPrime expressions from source queries.

---

## Project Structure

```text
grafana_to_cx_custom_converter/
├── GrafanaToCx.sln
├── src/
│   ├── GrafanaToCx.Cli/
│   │   ├── Program.cs
│   │   ├── Cli/
│   │   │   ├── AppRunner.cs
│   │   │   ├── ArgumentParser.cs
│   │   │   ├── CommandHandlers.cs
│   │   │   ├── PromptInput.cs
│   │   │   ├── PromptMenus.cs
│   │   │   └── SessionConfig.cs
│   │   └── migration-settings.json
│   └── GrafanaToCx.Core/
│       ├── ApiClient/
│       ├── Converter/
│       │   └── PanelConverters/
│       └── Migration/
└── test_data/
    └── grafana_test_dashboards/
```

---

## Migration Settings Reference

Full settings file with all available fields:

```json
{
  "grafana": {
    "region": "eu1",
    "folders": ["General"]
  },
  "coralogix": {
    "region": "eu1",
    "folderId": "",
    "isLocked": false,
    "migrateFolderStructure": true,
    "parentFolderId": ""
  },
  "credentials": {
    "grafanaApiKey": "",
    "cxApiKey": ""
  },
  "migration": {
    "checkpointFile": "migration-checkpoint.json",
    "reportFile": "migration-report.txt",
    "backupFile": "grafana-backup.zip",
    "fanOutMultiQueryPanels": true,
    "maxRetries": 5,
    "initialRetryDelaySeconds": 2
  }
}
```

| Field | Description |
|---|---|
| `grafana.region` | Grafana Cloud region |
| `grafana.folders` | Grafana folders to migrate (empty = all) |
| `coralogix.region` | Coralogix region (used to resolve endpoint) |
| `coralogix.folderId` | Fallback CX folder ID when mapping is missing |
| `coralogix.isLocked` | Lock uploaded dashboards |
| `coralogix.migrateFolderStructure` | Recreate Grafana folder structure in Coralogix |
| `coralogix.parentFolderId` | Parent folder for newly created Coralogix folders |
| `credentials.grafanaApiKey` | Optional fallback when `GRAFANA_API_KEY` is not set |
| `credentials.cxApiKey` | Optional fallback when `CX_API_KEY` is not set |
| `migration.checkpointFile` | Checkpoint file path for resume |
| `migration.reportFile` | Human-readable migration report path |
| `migration.backupFile` | Grafana backup ZIP path, used by `backup` and by `migrate`'s pre-flight backup. Empty disables the pre-flight backup; `backup` then falls back to `grafana-backup.zip` |
| `migration.fanOutMultiQueryPanels` | Emit one widget per query for multi-query `stat` panels instead of keeping the first (default `true` — see below) |
| `migration.maxRetries` | Max retries per dashboard |
| `migration.initialRetryDelaySeconds` | Initial exponential backoff delay |

### `migration.fanOutMultiQueryPanels`

A Coralogix gauge carries a single query, so a Grafana `stat` panel with several queries would
keep the first and drop the rest. Grafana draws one tile per query on such a panel, so this emits
one widget per query — preserving the data, titled from each target's `alias`.

**On by default.** It changes layout — a five-query stat panel becomes five widgets, which on
dashboards built around the idiom (status breakdowns, wall displays) can multiply the widget
count several times over. That is still the better default, because the alternative loses data
outright, and extra widgets are visible and easy to delete while a missing query is neither.

Set it to `false` to keep the original single-widget layout and accept the loss. On `convert`,
which reads no settings file, pass `--no-fan-out` instead:

```bash
dotnet run --project src/GrafanaToCx.Cli -- convert dashboard.json --no-fan-out
```

Whenever a query *is* dropped — with fan-out off, or on a panel type that cannot fan out —
`convert` and `push` print which widget lost which targets, and `migrate` and `import` record it
in the run report.

Only `stat` and `singlestat` fan out. `table` panels join their queries via a transformation,
`piechart` queries are slices of one chart, and `bargauge` queries are buckets of one
distribution — for those, one widget per query would be wrong.

### Pre-upload validation with the `cx` CLI

If the [`cx` CLI](https://github.com/coralogix/cx-cli) is on `PATH`, every converted dashboard is
validated against the live Coralogix API **before** it is uploaded, using the read-only
`dashboards check` endpoint. A dashboard the API would reject is failed with the reason in the
migration report, instead of being sent and refused. Warnings are logged and do not block.

This is entirely optional — if `cx` is not installed the step is skipped and migration behaves
exactly as before.

Credentials come from the migration's own `CX_API_KEY` and region. If your account
authenticates via OAuth rather than an API key, set `migration.cxCliProfile` to a configured
`cx` profile name and that is used instead.

To validate converted files by hand:

```bash
CX_API_KEY=cxtp_xxx CX_REGION=EU1 cx dashboards check --from-file ./converted/dashboard.json
```

`migration.multiLuceneMerge.allowlistedWidgetTypes` optionally allowlists widget types for incremental multi-query Lucene merge rollout. Example widget types: `piechart`, `timeseries`, `barchart`.

---

## Supported Regions

| Region Code | Coverage |
|---|---|
| `eu1`, `eu2` | Europe |
| `us1`, `us2` | United States |
| `ap1`, `ap2`, `ap3` | Asia Pacific |
| `in1` | India |

Every command resolves its target through the same chain: `--endpoint`, `--region`, an explicit endpoint in
the settings file, the interactive picker, then the settings region. Nothing is guessed — a command with no
target fails, and each run prints the endpoint it resolved along with which of those named it.

> **Upgrade note.** `import` and `verify` no longer default to eu1. If you relied on that, pass
> `--region eu1` or set `coralogix.region` in the settings file you pass with `-s`. The old default was
> invisible: `import` creates folders and overwrites same-named dashboards, so a run against the wrong
> tenant was undone by hand.

---

## Environment Variables

| Variable | Used by | Notes |
|---|---|---|
| `GRAFANA_API_KEY` | `migrate`, `backup` | First priority for Grafana API key (falls back to `credentials.grafanaApiKey`) |
| `CX_API_KEY` | `migrate`, `verify` | First priority for Coralogix API key (falls back to `credentials.cxApiKey`) |
| `CX_API_KEY` | `assess` | Required for live validation unless `--profile` supplies credentials; conversion-only assessment needs no API key |

`backup` never contacts Coralogix, so it does not need `CX_API_KEY`.

`push` and `import` get the API key from the interactive session.

---

## Other Commands

All commands run from repository root:

```bash
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- <command> [options]
```

With no command, the interactive console starts. It takes no verb, only these flags:

| Option | Description |
|---|---|
| `--resume <id>` | Resume a stored session by id, or any unambiguous prefix of it |
| `--resume` | Pick from the list of stored sessions, newest first |
| `-c`, `--continue` | Resume the most recently used session |

### `convert`

Convert one Grafana dashboard JSON file locally (no API calls):

```bash
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- convert ./dashboard.json -o ./dashboard_cx.json
```

| Argument/Flag | Description |
|---|---|
| `<input>` | Input Grafana dashboard JSON file or directory |
| `-o`, `--output` | Output file or directory (default: `<input>_cx.json`) |

### `migrate` (non-interactive)

Bulk migration driven entirely by the settings file — no prompts:

```bash
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- migrate --settings migration-settings.json
```

| Flag | Description |
|---|---|
| `-s`, `--settings` | Path to migration settings JSON (default: `migration-settings.json`) |
| `-r`, `--region` | Coralogix destination region; overrides `coralogix.region` |
| `-I`, `--interactive` | Enable guided prompts for folder mapping and conflict handling |

`--region` overrides only the Coralogix destination. The source Grafana comes from `grafana.region`, which
is a different system — the destination region says nothing about where to read from.

API key precedence for non-interactive `migrate`:
1. `GRAFANA_API_KEY` / `CX_API_KEY` environment variables
2. `credentials.grafanaApiKey` / `credentials.cxApiKey` in the settings file

### `backup`

Download Grafana dashboards into a local ZIP and stop there — no conversion, no upload, no
Coralogix connection. This is the backup step that `migrate` performs first, available on its own:

```bash
export GRAFANA_API_KEY=glsa_xxxxxxxxxxxx
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- backup --settings migration-settings.json
```

| Flag | Description |
|---|---|
| `-s`, `--settings` | Path to migration settings JSON (default: `migration-settings.json`) |
| `-o`, `--output` | Output ZIP path (default: `migration.backupFile`, else `grafana-backup.zip`) |
| `-r`, `--region` | Grafana region override; also makes the settings file optional |
| `-I`, `--interactive` | Pick folders from a list instead of using `grafana.folders` |

Archive layout matches the `migrate` backup — one JSON per dashboard, grouped by Grafana folder:

```
Technical_Platform-Ops/Gateway Integra Cluster_jDN4i4T4zdsfr.json
Technical_Platform-Ops/Stephan_Monitoring_FE_e6e17d7d-....json
```

Folder scope comes from `grafana.folders` in the settings file (empty = all folders), or from the
picker when `--interactive` is passed. With `--region` you can skip the settings file entirely:

```bash
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- backup --region eu1 -o ./boards.zip -I
```

Exit code is `0` only when every discovered dashboard was written. If anything was skipped the
command exits `1` and the archive carries a `_manifest.json` listing what failed.

To unpack a backup for local work, then convert it without uploading:

```bash
unzip -d ./grafana-dashboards ./grafana-backup.zip
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- convert ./grafana-dashboards -o ./converted
```

### `push`

Available via **interactive mode** (menu option 2). Configure Coralogix region and API key, then choose Push to upload a single dashboard.

### `import`

Bulk-import a directory of exported Grafana dashboard JSON files, deriving Coralogix folders from the
filenames. Supports checkpoint/resume, retry with backoff, and a written report — the same machinery
`migrate` uses, but sourced from local files instead of the Grafana API.

**Interactive (recommended)** — menu option 3, or:

```bash
export CX_API_KEY=cxtp_xxxxxxxxxxxx
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- import ./exports --interactive
```

The flow enumerates the directory, groups files into folders, and shows a preview. **Nothing is uploaded
until you accept it**, so changing the separator or segment count is instant and free:

```text
Found 23 dashboard JSON file(s) in 'artifacts/dashboards'.
Grouping: split filename on " - ", first 2 segment(s) become the folder name.
Dashboard names from: JSON title

  DDE - Delivery Data Engineering    3 dashboard(s)
  DDM - Delivery Data Modelling      1 dashboard(s)
  YM - Yield Management                 3 dashboard(s)
  WSP - WebShop VSM                    4 dashboard(s)
  WSP - webShop Identity               1 dashboard(s)
  WSP - webShop Login & Registration   7 dashboard(s)
  WSP - webShop Platforms              2 dashboard(s)
  WIWO - Wire In Wire Out            2 dashboard(s)
------------------------------------------------------
  8 folder(s), 23 dashboard(s), 0 ungrouped

> Accept this grouping
  Change separator (currently " - ")
  Change segment count (currently 2)
  Pick which segment starts the folder name (currently #1)
  Show files per folder
  Rename a folder
  Group by subdirectories
  Put everything in one folder
  Dashboard names: currently "JSON title"
  Cancel
```

*Pick which segment starts the folder name* is for filenames where the folder-worthy part is not at the
front. It shows a real filename split into numbered segments and you choose one:

```text
Sample filename: DDE - Delivery Data Engineering - Primary CRM

> 1: DDE
  2: Delivery Data Engineering
  3: Primary CRM
```

Picking `2` with a segment count of `1` gives the folder `Delivery Data Engineering`. Skipped leading
segments are not discarded — they stay in the dashboard name (`DDE - Primary CRM`), so nothing from the
filename is lost. The default of `#1` is the previous behaviour: the leading segments become the folder.

After accepting the grouping you choose where the folders go:

```text
? Folder placement strategy
> Put dashboards into matching Coralogix folders that already exist
  Nest all under a parent Coralogix folder (preserves structure)
  Create each folder at the top level
```

- **Put dashboards into matching folders that already exist** — matches each derived group against the
  folders already in the destination, so an import does not create `DDE - Delivery Data Engineering`
  next to an existing `Delivery Data Engineering`. Matching is tried in order: exact name, then equal
  ignoring punctuation and spacing (`Wire In / Wire Out` ↔ `wire-in-wire-out`), then one name
  contained in the other. Folder names shorter than 4 characters after normalising are never matched by
  containment, so a folder called `ES` cannot swallow every group.
- **Nest all under a parent** — creates or reuses a single root folder and puts each group beneath it.
  Hidden on targets without nested folders.
- **Create each folder at the top level** — creates one top-level folder per group, reusing only a folder
  whose name matches exactly.

The matching strategy shows its proposal before writing anything, and every row is overridable:

```text
Folder mapping:
  DDE - Delivery Data Engineering  →  Engineering / Delivery Data Engineering   (matched on a contained name — check this one)
  YM - Yield Management               →  Yield Management   (exact name match)
  WSP - webShop Platforms            →  + create new folder with this name

> Accept this mapping
  Change one mapping
  Cancel
```

*Change one mapping* re-points a single group at any existing folder (listed by full path, so folders
sharing a leaf name stay distinguishable), at a new folder, or at no folder at all. Only the groups still
marked `+ create new folder` cause a folder to be created.

Then you choose whether to overwrite existing dashboards, and — if a previous run left a checkpoint —
whether to resume or start fresh.

**Dashboard names come from each file's JSON `title`, not the filename.** These often differ (a file named
`WSP - webShop Platforms - Primary.json` may contain the title `Lobby Platforms Dashboard V2`). Use *Show
files per folder* to see both, and *Dashboard names* to switch to the filename remainder instead.

**Non-interactive:**

```bash
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- import ./exports --region eu1
```

Uses the grouping defaults from the settings file, creates folders at the top level, and overwrites
existing dashboards.

| Argument/Flag | Description |
|---|---|
| `<input>` | Directory containing Grafana dashboard JSON files |
| `-e`, `--endpoint` | Coralogix API endpoint |
| `-r`, `--region` | Coralogix region; resolves to `https://<region-host>/mgmt/openapi/latest` |
| `-s`, `--settings` | Path to settings JSON (default: `migration-settings.json`) |
| `-I`, `--interactive` | Enable the region picker plus the guided grouping and folder-mapping prompts |

Target precedence: `--endpoint`, `--region`, the interactive picker, `coralogix.region`. There is no
built-in default — the command fails rather than guess a region. Note that the default settings path is
relative, so running from the repository root finds no settings file and you must pass `-r` or
`-s src/GrafanaToCx.Cli/migration-settings.json`. Every run prints the resolved target and where it came
from.

API key precedence: `CX_API_KEY` environment variable, then `credentials.cxApiKey` in the settings file.

Progress is written to `import-checkpoint.json` after every dashboard, so an interrupted run resumes where
it stopped. A summary — including per-dashboard panel conversion warnings — is written to
`import-report.txt`. These are deliberately separate from the migrate checkpoint and report; the tool
refuses to start if they are configured to the same path.

#### Import settings

```json
{
  "import": {
    "checkpointFile": "import-checkpoint.json",
    "reportFile": "import-report.txt",
    "maxRetries": 5,
    "initialRetryDelaySeconds": 2,
    "overwriteExisting": true,
    "isLocked": false,
    "grouping": {
      "separator": " - ",
      "segmentCount": 2,
      "segmentStart": 1,
      "recursive": false,
      "ungroupedFolderName": null
    }
  }
}
```

| Field | Description |
|---|---|
| `import.checkpointFile` | Checkpoint path for resume. Must differ from `migration.checkpointFile` |
| `import.reportFile` | Human-readable import report path |
| `import.overwriteExisting` | Replace dashboards matching on name + folder. Defaults to `true` |
| `import.isLocked` | Lock imported dashboards |
| `import.grouping.separator` | Filename separator used to derive folder names |
| `import.grouping.segmentCount` | How many consecutive segments form the folder name |
| `import.grouping.segmentStart` | 1-based index of the first segment used. `1` = leading segments |
| `import.grouping.recursive` | Scan subdirectories as well as the top level |
| `import.grouping.ungroupedFolderName` | Folder for files yielding no prefix. `null` = no folder |
| `grafanaImport.*` | Settings for the Grafana-to-Grafana flow — see [`grafana-import`](#grafana-import) |

Files whose names have too few segments are left **ungrouped** rather than being forced into a folder named
after a single dashboard.

### `grafana-import`

Publish a directory of exported Grafana dashboards into a **Coralogix-hosted Grafana**
(`https://<region-host>/grafana`) rather than converting them to Coralogix custom dashboards. Reuses the
same discovery, folder-grouping, checkpoint/resume, retry and report machinery as `import`; what differs is
the destination and a JSON transform that re-points datasources instead of rewriting panels.

Accepts both raw UI exports and the `{ "dashboard": …, "meta": … }` envelope returned by
`GET /api/dashboards/uid/:uid` — which is also what this tool's own `grafana-backup.zip` contains.

**Always dry-run first.** Nothing is created: no folders, no dashboards, no checkpoint.

```bash
export CX_API_KEY=cxtp_xxxxxxxxxxxx
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- \
  grafana-import ./grafana-backup -s src/GrafanaToCx.Cli/migration-settings.json \
  --region eu1 --recursive --dry-run
```

Then run it for real, or use `-I` for the guided grouping and folder prompts (also available as option 7
in the interactive menu):

```bash
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- \
  grafana-import ./grafana-backup -s src/GrafanaToCx.Cli/migration-settings.json \
  --region eu1 --recursive
```

| Argument/Flag | Description |
|---|---|
| `<input>` | Directory containing Grafana dashboard JSON files |
| `-e`, `--endpoint` | Grafana API base URL, e.g. `https://api.coralogix.com/grafana` |
| `-r`, `--region` | Coralogix region; resolves to `https://<region-host>/grafana` |
| `-s`, `--settings` | Path to settings JSON (default: `migration-settings.json`) |
| `-I`, `--interactive` | Guided grouping and folder-mapping prompts |
| `-n`, `--dry-run` | Print the plan and the datasource inventory; write nothing |
| `--overwrite` / `--no-overwrite` | Override `grafanaImport.overwriteExisting` |
| `-R`, `--recursive` / `--no-recursive` | Override `grafanaImport.grouping.recursive` |

`g2g` is accepted as a shorthand for `grafana-import`.

Target precedence: `--endpoint`, `--region`, `grafanaImport.endpoint`, `grafanaImport.region`,
`coralogix.region`. There is no built-in default — the command fails rather than guess a region, because a
wrong guess publishes dashboards into a tenant nobody asked for.

The **Coralogix** API key is used (`CX_API_KEY`, then `credentials.cxApiKey`); the hosted Grafana sits
behind the Coralogix gateway and accepts it as a bearer token. Both `cxtp_` (team) and `cxup_` (user) keys
work provided the resulting Grafana org role is Editor or Admin. Confirm before a large run:

```bash
curl -sS -H "Authorization: Bearer $CX_API_KEY" https://api.coralogix.com/grafana/api/user/orgs
# => [{"orgId":…,"role":"Editor"}]
```

Note the Coralogix **UI** host (e.g. `watcher.coralogix.com`) is not the API host — use the `api.` host from
the region table above.

#### Folder grouping

Source layout decides the grouping, so no configuration is needed for the common cases:

- **A directory tree** (one subdirectory per team, as `grafana-backup/` produces) mirrors those
  subdirectories into destination folders.
- **A flat directory** falls back to splitting each filename on `grafanaImport.grouping.separator`.

`--interactive` starts from that choice and lets you switch strategy, change the separator, rename folders,
or put everything in one folder.

#### What the transform changes

Re-running an import is a no-op because of four things together: a stable `uid`, `overwrite: true`, and the
removal of `id` and `version`. The uid is what Grafana matches on, `overwrite` waives its version check, a
foreign numeric `id` would match an unrelated dashboard on the destination, and a foreign `version` would be
written verbatim on create. Everything else is preserved.

| Field | Behaviour |
|---|---|
| `uid` | Preserved. Derived deterministically from the source path when absent, invalid, or claimed by more than one file in the run |
| `id`, `version`, `iteration` | Removed |
| `meta`, `folderId`, `folderUid`, `slug`, `url` | Removed at the top level only — nested `meta` (Elasticsearch extended-stats config) survives |
| `datasource` | Re-pointed; see below. Explicit `null`, `$variable` references and built-ins are left alone |
| `templating.list[].query` | **Never** rewritten — it means something different for every variable type |
| `templating.list[].current` | Re-pointed for `datasource` variables on schemaVersion ≥ 33 only |
| `annotations`, `links`, `refresh`, `timezone`, `weekStart`, `tags`, `time` | Preserved |
| `panels[].alert` | Dropped with a warning — pre-Grafana-9 rules cannot be recreated through this API |
| `panels[].libraryPanel` | Kept with a warning when the uid is missing on the destination |
| `__inputs`, `__requires` | Resolved and substituted, then removed |
| `schemaVersion` | Preserved; the destination migrates it on load |

Datasources are resolved in this order: explicit `datasourceUidMap` entry by source uid, then by source
name, then a destination datasource with the same uid, then the same name, then the only one of that type
(or the default among several). A reference that matches nothing is **left exactly as it was** and reported
as a warning, so the panel says it cannot query rather than silently querying the wrong backend. Setting
`allowTargetDefaultFallback` to `true` opts into pointing unmatched references at the default datasource.

#### Grafana import settings

```json
{
  "grafanaImport": {
    "region": "eu1",
    "endpoint": "",
    "checkpointFile": "grafana-import-checkpoint.json",
    "reportFile": "grafana-import-report.txt",
    "maxRetries": 5,
    "initialRetryDelaySeconds": 2,
    "overwriteExisting": true,
    "dryRun": false,
    "message": "Imported by grafana-to-cx grafana-import",
    "allowTargetDefaultFallback": false,
    "datasourceUidMap": { "source-uid-or-name": "target-uid" },
    "grouping": {
      "separator": " - ",
      "segmentCount": 2,
      "segmentStart": 1,
      "recursive": true,
      "ungroupedFolderName": null
    }
  }
}
```

| Field | Description |
|---|---|
| `grafanaImport.region` | Region used to derive the Grafana base URL |
| `grafanaImport.endpoint` | Explicit Grafana base URL; overrides `region` when non-empty |
| `grafanaImport.checkpointFile` | Checkpoint path. Must differ from **both** `migration.checkpointFile` and `import.checkpointFile` |
| `grafanaImport.reportFile` | Human-readable report path |
| `grafanaImport.maxRetries` | Retryable failures become permanent after this many attempts |
| `grafanaImport.overwriteExisting` | Whether to revisit dashboards already marked completed in the checkpoint. It does **not** control the `overwrite` flag on the save request, which is always `true` |
| `grafanaImport.dryRun` | Default for `--dry-run` |
| `grafanaImport.message` | Commit message recorded in the destination's dashboard version history |
| `grafanaImport.allowTargetDefaultFallback` | Point unmatched datasources at the destination default instead of reporting them |
| `grafanaImport.datasourceUidMap` | Source datasource uid (or legacy name) → destination uid. Wins over discovery |
| `grafanaImport.grouping.*` | Same rules as `import.grouping`, but `recursive` defaults to `true` |

`migrate`, `import` and `grafana-import` each keep their own checkpoint and report; the tool refuses to start
if any two resolve to the same path.

### `verify`

Convert, fetch from Coralogix, and compare conversion output:

```bash
dotnet run --project ./src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj -- verify ./dashboard.json --region eu1 -d DASHBOARD_ID
```

| Argument/Flag | Description |
|---|---|
| `<input>` | Input Grafana dashboard JSON file |
| `-e`, `--endpoint` | Coralogix API endpoint |
| `-r`, `--region` | Coralogix region; resolves to `https://<region-host>/mgmt/openapi/latest` |
| `-s`, `--settings` | Path to settings JSON (default: `migration-settings.json`) |
| `-d`, `--dashboard-id` | CX dashboard ID to verify against |
| `-I`, `--interactive` | Pick the region interactively |

Target precedence: `--endpoint`, `--region`, the interactive picker, `coralogix.region`. A target is only
required with `-d` — without it `verify` prints a local conversion report and never contacts Coralogix, so
it neither prompts nor fails on a missing region.

---

## Integration Settings and Live Test

- Commit-safe template: `src/GrafanaToCx.Cli/migration-settings.integration.example.json`
- Local secret-bearing file (git-ignored): `src/GrafanaToCx.Cli/migration-settings.integration.json`

```bash
export GRAFANA_TO_CX_INTEGRATION_SETTINGS=src/GrafanaToCx.Cli/migration-settings.integration.json
dotnet test --filter "FullyQualifiedName~MigrationFlowIntegrationTests"
```

---

## Playwright Migration Validation (Grafana vs Coralogix)

End-to-end migration checks under `tests/e2e` validate zero visible errors on both platforms, data presence, and tolerance-based numeric comparison for matched panel titles.

### 1) Install Playwright tooling

```bash
npm install
npm run e2e:install
```

### 2) Create one-time auth storage state

```bash
npm run e2e:auth
```

Complete login in the headed browser, then press Enter to save `tests/e2e/.auth/storage-state.json`.

### 3) Configure dashboards by name

Edit `tests/e2e/dashboard-selection.json`. Set `dashboards` to Grafana dashboard titles to validate — names are resolved against `migration-checkpoint.json` and must map to unique `Completed` entries.

### 4) Run migration comparison tests

```bash
npm run e2e:test
```

For interactive debugging:

```bash
npm run e2e:headed
```

Failure artifacts are written to `tests/e2e/artifacts/<dashboard-name>/`.

---

## FAQ

### What is the difference between Coralogix custom dashboards and Coralogix-hosted Grafana?

Custom dashboards use Coralogix-native widgets and APIs; use `migrate`, `convert`, `import`, or `push`.
Coralogix-hosted Grafana keeps the Grafana UI and dashboard model; use [`grafana-import`](#grafana-import).

### Can I migrate PromQL, LogQL, or Elasticsearch queries?

Yes. PromQL and Elasticsearch queries are preserved, while Loki LogQL is converted to Lucene. See
[Supported Query Languages](#supported-query-languages) for DataPrime scope.

### Do I need the Grafana API, or can I use exported JSON files?

Grafana API access is optional. Use [`convert`](#convert) for local conversion, [`import`](#import) for
bulk upload from JSON exports, or [`assess`](#pre-migration-assessment-assess) on an export directory or
backup ZIP.

### How do I resume a failed bulk migration?

Re-run the same command and retain its checkpoint file; completed dashboards are skipped. See
[Resuming a session](#resuming-a-session) and [`import`](#import) for checkpoint details.

### Is this open source?

Yes. The project is available under the [MIT License](LICENSE).

### How do I validate migration quality?

Run [`assess`](#pre-migration-assessment-assess) before migration, `verify` for conversion and round-trip
comparison, and [Playwright migration validation](#playwright-migration-validation-grafana-vs-coralogix)
for end-to-end UI and data checks.

---

## Troubleshooting

- `dotnet: command not found` — install .NET 9 SDK and restart terminal.
- `401/403` API errors — verify token/key, scopes/permissions, and endpoint/region alignment.
- Dashboard skipped during migration — check `migration-report.txt` and re-run; the checkpoint will resume from where it left off.
- Unexpected conversion output — run `verify` on the same input to compare round-trip behaviour.
- Rate limits/timeouts — increase `maxRetries` and `initialRetryDelaySeconds` in `migration-settings.json`.
- Migration stopped mid-run — re-run the same command; completed dashboards are skipped automatically via checkpoint.

---

## License and Related Links

Licensed under the [MIT License](LICENSE).

- [Coralogix Custom Dashboards documentation](https://coralogix.com/docs/user-guides/custom-dashboards/introduction/)
- [Grafana dashboard JSON export documentation](https://grafana.com/docs/grafana/latest/visualizations/dashboards/share-dashboards-panels/#export-dashboards)
- [Grafana Dashboard HTTP API](https://grafana.com/docs/grafana/latest/developers/http_api/dashboard/)
- [`cx` CLI](https://github.com/coralogix/cx-cli)
