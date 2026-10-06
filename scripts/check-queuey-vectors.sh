#!/usr/bin/env bash
# Compares the test vectors the CLI shares with Queuey: the addresses Queuey's egress guard blocks and lets through
# (SsrfEgressPolicy, mirrored by DeploymentDestinations) and the rules for a credential name (CredentialNameRules, the
# same class on both sides). Each side tests its own code against its own vectors; this script fails when the two sets
# differ, so a range or a rule added on one side fails here until the other side has it too.
#
# Run it before a v* tag (CLAUDE.md, Release). It reads Queuey's files from a git ref, without touching that checkout:
#   scripts/check-queuey-vectors.sh [--ref <git ref>] [<path to the Queuey repository>]
# The path defaults to $QUEUEY_REPO, else ../Queuey beside this repository. The ref defaults to origin/main, what
# production runs: a tag waits for Queuey's integration branch to reach production, and until it has, this fails too.
# Fetch the Queuey repository first (git -C <path> fetch) so the ref is current.
set -euo pipefail

ref="origin/main"
repo="${QUEUEY_REPO:-}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --ref) ref="${2:?--ref takes a git ref, such as origin/main}"; shift 2 ;;
    -h|--help) sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) repo="$1"; shift ;;
  esac
done

client="$(cd "$(dirname "$0")/.." && pwd)"
repo="${repo:-$client/../Queuey}"
if ! commit="$(git -C "$repo" rev-parse --verify --quiet "$ref^{commit}")"; then
  echo "No Queuey repository with $ref at $repo. Pass its path, or set QUEUEY_REPO." >&2
  exit 2
fi
echo "Queuey at $ref ($commit), the CLI at $(git -C "$client" rev-parse HEAD 2>/dev/null || echo 'an unknown commit')."

# The arguments of each [InlineData(...)] line, as written, prefixed with the test method that follows them:
#   <method><TAB><arguments>
# A test that changes its vectors' wording changes them here too; the comparison is of the source text.
inline_data() {
  awk '
    /^[ \t]*\[InlineData\(/ {
      line = $0
      start = index(line, "[InlineData(") + 12
      rest = substr(line, start)
      # The attribute ends at the ")]" that only whitespace or a // comment follows.
      pos = 0; found = 0
      while ((i = index(substr(rest, pos + 1), ")]")) > 0) {
        pos += i
        tail = substr(rest, pos + 2)
        if (tail ~ /^[ \t]*(\/\/.*)?$/) { found = pos; break }
      }
      if (found > 0) pending[++n] = substr(rest, 1, found - 1)
      next
    }
    /^[ \t]*(public|private|internal)[^(]*\(/ {
      head = substr($0, 1, index($0, "(") - 1)
      count = split(head, words, /[ \t]+/)
      method = words[count]
      for (k = 1; k <= n; k++) print method "\t" pending[k]
      n = 0
    }
  '
}

# The vectors of the methods whose names match a pattern, sorted: one argument list per line.
vectors() { # <text> <method regex>
  printf '%s\n' "$1" | inline_data | awk -F '\t' -v pattern="$2" '$1 ~ pattern { print $2 }' | LC_ALL=C sort -u
}

# A file that is not there gives no vectors, and the comparison says so.
queuey_file() { git -C "$repo" show "$ref:$1" 2>/dev/null || true; }
client_file() { cat "$client/$1" 2>/dev/null || true; }

failed=0

# Every [InlineData( line has to be read, or a vector the parser misses (one spread over lines, say) would pass unseen.
check_parsed() { # <what> <file text>
  local lines parsed
  lines="$(printf '%s\n' "$2" | grep -c '^[[:space:]]*\[InlineData(' || true)"
  parsed="$(printf '%s\n' "$2" | inline_data | wc -l | tr -d ' ')"
  if [[ "$lines" != "$parsed" ]]; then
    echo "✗ $1: $lines [InlineData( lines, but $parsed read. Keep each vector on one line, with its test method below it."
    failed=1
  fi
}

compare() { # <what> <Queuey's vectors> <the CLI's vectors>
  local only_queuey only_client
  only_queuey="$(LC_ALL=C comm -23 <(printf '%s\n' "$2") <(printf '%s\n' "$3") | sed '/^$/d')"
  only_client="$(LC_ALL=C comm -13 <(printf '%s\n' "$2") <(printf '%s\n' "$3") | sed '/^$/d')"
  if [[ -z "$2" || -z "$3" ]]; then
    echo "✗ $1: no vectors on $([[ -z "$2" ]] && echo "Queuey's side" || echo "the CLI's side"). The test is not there: an older ref, or a test that was renamed or moved, which this script must follow."
    failed=1
  elif [[ -z "$only_queuey" && -z "$only_client" ]]; then
    echo "✓ $1: $(printf '%s\n' "$2" | wc -l | tr -d ' ') vectors, the same on both sides."
  else
    echo "✗ $1 differ:"
    if [[ -n "$only_queuey" ]]; then echo "    only in Queuey:"; sed 's/^/      /' <<< "$only_queuey"; fi
    if [[ -n "$only_client" ]]; then echo "    only in the CLI:"; sed 's/^/      /' <<< "$only_client"; fi
    failed=1
  fi
}

egress_queuey="$(queuey_file tests/Api/Queuey.Api.Tests/Tests/Security/SsrfEgressPolicyTests.cs)"
egress_client="$(client_file tests/Queuey.Client.Waas.Tests/Deployment/DeploymentDestinationsTests.cs)"
check_parsed "Queuey's egress tests" "$egress_queuey"
check_parsed "The CLI's egress tests" "$egress_client"

# An IsBlockedIp_ theory that is neither a blocked nor an allowed list would be left out of the comparison silently.
unsorted="$(printf '%s\n' "$egress_queuey" | inline_data | cut -f1 | LC_ALL=C sort -u \
  | grep '^IsBlockedIp_' | grep -Ev '^IsBlockedIp_(blocks|allows|lets)_' || true)"
if [[ -n "$unsorted" ]]; then
  echo "✗ Queuey has IsBlockedIp_ tests that are neither blocks_ nor allows_ or lets_, so they are not compared:"
  sed 's/^/      /' <<< "$unsorted"
  failed=1
fi

compare "Blocked addresses" \
  "$(vectors "$egress_queuey" '^IsBlockedIp_blocks_')" \
  "$(vectors "$egress_client" '^An_address_queueys_egress_guard_blocks_is_refused_before_anything_is_sent$')"
compare "Addresses let through" \
  "$(vectors "$egress_queuey" '^IsBlockedIp_(allows|lets)_')" \
  "$(vectors "$egress_client" '^An_address_queueys_egress_guard_lets_through_is_left_to_the_server$')"

# The credential name tests are twins: the same method names on both sides, each with the same vectors.
names_queuey="$(queuey_file tests/Api/Queuey.Api.Tests/Tests/Security/CredentialNameRulesTests.cs)"
names_client="$(client_file tests/Queuey.Client.Waas.Tests/Deployment/CredentialNameRulesTests.cs)"
check_parsed "Queuey's credential name tests" "$names_queuey"
check_parsed "The CLI's credential name tests" "$names_client"
methods="$( (printf '%s\n' "$names_queuey"; printf '%s\n' "$names_client") | inline_data | cut -f1 | LC_ALL=C sort -u)"
for method in $methods; do
  compare "Credential names, $method" "$(vectors "$names_queuey" "^$method\$")" "$(vectors "$names_client" "^$method\$")"
done

if [[ $failed -ne 0 ]]; then
  echo "The CLI and Queuey ($ref) test different vectors. Bring the side that lacks one up to the other, code and tests." >&2
  exit 1
fi
