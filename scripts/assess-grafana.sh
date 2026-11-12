#!/usr/bin/env bash
# Back up every dashboard from a Grafana and assess how it would migrate to Coralogix.
#
# Writes into one directory:
#   boards.zip             the dashboards as downloaded
#   assessment.md          what converts today, untranslatable queries included
#   assessment-assumed.md  dashboard mechanics only, assuming the data and its naming are sorted
#
# Uses GRAFANA_API_KEY when set, otherwise asks Grafana anonymously.

set -euo pipefail

usage() {
    cat <<EOF
Usage: $(basename "$0") <grafana-url> [options]

Options:
  -o, --out <dir>             Output directory (default: ./assessment-<host>-<date>)
  -k, --tls-skip-verify       Accept a self-signed TLS certificate from Grafana
  -h, --help                  Show this help

Set GRAFANA_API_KEY to use a token; without it the backup is anonymous.
EOF
}

url=""
out=""
insecure=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        -o|--out) out="${2:?--out needs a directory}"; shift 2 ;;
        -k|--tls-skip-verify|--insecure) insecure=true; shift ;;
        -h|--help) usage; exit 0 ;;
        -*) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
        *)
            if [[ -n "$url" ]]; then echo "Only one Grafana URL, please." >&2; exit 2; fi
            url="$1"; shift ;;
    esac
done

if [[ -z "$url" ]]; then usage >&2; exit 2; fi

repo="$(cd "$(dirname "$0")/.." && pwd)"
host="$(echo "$url" | sed -E 's#^[a-zA-Z]+://##; s#[/:].*$##')"
out="${out:-assessment-${host}-$(date +%Y%m%d)}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"

# Build once rather than letting each `dotnet run` check, and keep its noise out of the way.
echo "Building..."
dotnet build "$repo/src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj" -c Release -v quiet -nologo >/dev/null

# The tool reads ./migration-settings.json for both its settings and its logging. Running from
# the output directory with this minimal one means a settings file elsewhere (with its own region
# or placeholder key) is never picked up, logs land in $out/logs, and nothing is shipped over OTLP.
if [[ ! -e "$out/migration-settings.json" ]]; then
    cat > "$out/migration-settings.json" <<'JSON'
{
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": {
      "File": { "Enabled": true, "Path": "logs/grafana-to-cx-.json", "Formatter": "Json" },
      "Otlp": { "Enabled": false }
    }
  }
}
JSON
fi

cli() {
    (cd "$out" && dotnet run --project "$repo/src/GrafanaToCx.Cli/GrafanaToCx.Cli.csproj" \
        -c Release --no-build -- "$@")
}

backup_args=(backup --grafana-url "$url" -o "$out/boards.zip")
$insecure && backup_args+=(--tls-skip-verify)
if [[ -z "${GRAFANA_API_KEY:-}" ]]; then
    echo "GRAFANA_API_KEY not set: requesting dashboards anonymously."
    backup_args+=(--anonymous)
fi

# A partial backup still exits non-zero; assess whatever arrived and say so.
backup_status=0
cli "${backup_args[@]}" || backup_status=$?
if [[ ! -s "$out/boards.zip" ]]; then
    echo "Backup produced nothing; stopping." >&2
    exit 1
fi
if [[ $backup_status -ne 0 ]]; then
    echo "Warning: backup was incomplete; see _manifest.json in boards.zip. Assessing what arrived." >&2
fi

# assess exits non-zero when a dashboard would be rejected, which is a finding, not a failure.
echo "Assessing..."
cli assess "$out/boards.zip" -f markdown -o "$out/assessment.md" >/dev/null || true
cli assess "$out/boards.zip" -f markdown -o "$out/assessment-assumed.md" --assume-queries-translated >/dev/null || true

summary() { sed -n '3p' "$1"; grep -E '^\| (Clean|Degraded|Rejected|Failed) \|' "$1"; }

echo
echo "As things stand ($out/assessment.md):"
summary "$out/assessment.md"
echo
echo "Assuming the data and its naming are sorted ($out/assessment-assumed.md):"
summary "$out/assessment-assumed.md"
