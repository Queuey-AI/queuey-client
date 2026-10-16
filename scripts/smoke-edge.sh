#!/usr/bin/env bash
# Smoke test of the Edge spool in a built queuey: the self-contained single-file binary for one platform, or the dotnet
# tool. release.yml runs it beside smoke-profile.sh, so a build that cannot open a spool fails the release instead of the
# device it was copied to. Nothing is sent: edge publish and edge status only touch the local spool.
#
#   scripts/smoke-edge.sh <path to queuey>
#
# The spool is SQLite, and its native library (e_sqlite3) is not part of the .NET runtime: a single-file binary carries
# it inside itself and extracts it on every start, under DOTNET_BUNDLE_EXTRACT_BASE_DIR or else ~/.net (%TEMP%\.net on
# Windows). The release archive holds the binary alone, so this is what proves for each platform that the binary needs
# nothing the publish left beside it. The publish runs as on a laptop, extracting under the home folder; the status runs
# as under the systemd unit in docs/edge-operations.md, with no usable home and DOTNET_BUNDLE_EXTRACT_BASE_DIR set.
set -euo pipefail

bin="${1:?usage: scripts/smoke-edge.sh <path to queuey>}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

windows=false
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) windows=true ;; esac

# A path the binary can open: on Windows a native path, not Git Bash's /tmp/….
native() { if $windows; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

fail() { echo "smoke-edge: $*" >&2; exit 1; }

mkdir -p "$work/spool"
spool="$(native "$work/spool/spool.db")"
unset QUEUEY_TENANT QUEUEY_SPOOL_KEY DOTNET_BUNDLE_EXTRACT_BASE_DIR

# 1. edge publish creates the spool and accepts one event into it: the first thing that loads SQLite.
out="$("$bin" edge publish smoke-readings --spool "$spool" --tenant ten_smoke \
  --data '{"reading":1}' --group-key unit-1 --json 2>&1)" || fail "edge publish failed: $out"
grep -q '"transferId": "' <<<"$out" || fail "edge publish did not accept the event: $out"
grep -q '"pending": 1' <<<"$out" || fail "edge publish did not leave one event pending: $out"

# 2. edge status reads the spool back. On Unix the home folder does not exist, so the binary starts only if it extracts
#    where DOTNET_BUNDLE_EXTRACT_BASE_DIR says — as a service user without a home has to.
DOTNET_BUNDLE_EXTRACT_BASE_DIR="$(native "$work/extract")"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR
if $windows; then
  out="$("$bin" edge status --spool "$spool" --json 2>&1)" || fail "edge status failed: $out"
else
  out="$(HOME="$work/no-home" "$bin" edge status --spool "$spool" --json 2>&1)" \
    || fail "edge status failed without a home folder, with DOTNET_BUNDLE_EXTRACT_BASE_DIR set: $out"
fi
grep -q '"pending": 1' <<<"$out" || fail "edge status did not read the pending event back: $out"
grep -q '"quarantined": 0' <<<"$out" || fail "edge status reported a quarantined event: $out"

echo "smoke-edge: the Edge spool works in $bin"
