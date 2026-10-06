#!/usr/bin/env bash
# Smoke test of --profile in a built queuey: the self-contained single-file binary for one platform, or the dotnet tool.
# release.yml runs it before anything of that kind is published, so a build in which --profile cannot read the user's
# connections fails the release instead of the user. Nothing is sent: whoami and apply --dry-run never connect.
#
#   scripts/smoke-profile.sh <path to queuey>
#
# On Unix the connections file is checked the way ssh checks ~/.ssh, and its owner comes from the runtime's native shim
# (libSystem.Native), which a single-file binary carries inside itself. That is what this proves for each platform: that
# the check runs, and passes for a file only the user can reach. Windows has no such check.
set -euo pipefail

bin="${1:?usage: scripts/smoke-profile.sh <path to queuey>}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

windows=false
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) windows=true ;; esac

# A path the binary can open: on Windows a native path, not Git Bash's /tmp/….
native() { if $windows; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

fail() { echo "smoke-profile: $*" >&2; exit 1; }

mkdir -p "$work/home/.queuey"
chmod 700 "$work/home" "$work/home/.queuey"
config="$work/home/.queuey/config.json"
cat > "$config" <<'JSON'
{
  "profiles": {
    "smoke": {
      "apiKey": "qak_smoke.not-a-key", "license": "lic_smoke", "tenant": "ten_smoke",
      "apiBase": "https://api.smoke.invalid", "ingressBase": "https://ingress.smoke.invalid"
    }
  }
}
JSON
chmod 600 "$config"

deploy="$work/queuey.deploy.json"
cat > "$deploy" <<'JSON'
{
  "tenant": "${QUEUEY_TENANT}",
  "workspace": { "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}" },
  "queues": { "orders": { "delivery": { "url": "${ORDERS_HOOK_URL}" } } },
  "profiles": {
    "smoke": {
      "variables": {
        "QUEUEY_TENANT": "ten_smoke",
        "QUEUEY_WORKSPACE_ENVIRONMENT": "test",
        "ORDERS_HOOK_URL": "https://hooks.smoke.invalid/orders"
      }
    }
  }
}
JSON

QUEUEY_USER_CONFIG="$(native "$config")"
export QUEUEY_USER_CONFIG
unset QUEUEY_PROFILE QUEUEY_API_KEY QUEUEY_TENANT QUEUEY_LICENSE QUEUEY_API_BASE QUEUEY_INGRESS_BASE QUEUEY_SOURCE

# 1. whoami reads the profile's connection through the whole check: the link, the owner, the mode, every folder above it.
out="$("$bin" whoami --profile smoke --json 2>&1)" || fail "whoami --profile smoke failed: $out"
grep -q '"profile": "smoke"' <<<"$out" || fail "whoami --profile did not name the profile: $out"
grep -q '"tenant": "ten_smoke"' <<<"$out" || fail "whoami --profile did not take the profile's workspace: $out"

# 2. apply --dry-run expands the file with the profile's values, and has nothing to warn about with the connection there.
"$bin" apply --file "$(native "$deploy")" --dry-run --profile smoke > "$work/dry-run.out" 2> "$work/dry-run.err" \
  || fail "apply --dry-run --profile smoke failed: $(cat "$work/dry-run.out" "$work/dry-run.err")"
grep -q 'Nothing was sent.' "$work/dry-run.out" || fail "apply --dry-run did not finish: $(cat "$work/dry-run.out")"
if grep -q 'Warning' "$work/dry-run.err"; then
  fail "apply --dry-run could not use the connection: $(cat "$work/dry-run.err")"
fi

# 3. On Unix, a connections file others can read is refused: the check ran in this build rather than being skipped.
if ! $windows; then
  chmod 644 "$config"
  if refused="$("$bin" whoami --profile smoke --json 2>&1)"; then
    fail "whoami read a connections file others can read: $refused"
  fi
  grep -q 'other users can reach it (mode 0644)' <<<"$refused" || fail "the refusal was not the permission check: $refused"
fi

echo "smoke-profile: --profile works in $bin"
