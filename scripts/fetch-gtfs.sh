#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

DEST="src/CarrisRebuild/Resources/Raw/gtfs.db.gz"
URL="${GTFS_DB_URL:-https://github.com/DiAguiar310/BusLX/releases/download/v1.1/gtfs.db.gz}"
EXPECTED_SHA256="db56b540167d266f0089f564865a97c3caa0bee1b23713f64e65b01171ef4e90"

if [ -f "$DEST" ]; then
  echo "Já existe: $DEST"
  exit 0
fi

mkdir -p "$(dirname "$DEST")"
echo "A descarregar a base GTFS:"
echo "  $URL"
curl -L --fail --retry 3 --progress-bar -o "$DEST.tmp" "$URL"

ACTUAL_SHA256="$(sha256sum "$DEST.tmp" | awk '{print $1}')"
if [ "$ACTUAL_SHA256" != "$EXPECTED_SHA256" ]; then
  rm -f "$DEST.tmp"
  echo "Erro: SHA-256 não corresponde." >&2
  echo "  esperado: $EXPECTED_SHA256" >&2
  echo "  obtido:   $ACTUAL_SHA256" >&2
  exit 1
fi

mv "$DEST.tmp" "$DEST"
echo "Pronto: $DEST"
