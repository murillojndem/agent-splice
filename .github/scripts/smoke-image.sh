#!/usr/bin/env bash
# Smoke test for a running AgentSplice container (ADR 0015).
#
# The integration tests prove the serving rules against a fixture build in an in-process server. This
# proves what they cannot: that the image as built carries the dashboard it was built with, that the
# shell names files the gateway actually serves, that real Kestrel path normalisation keeps the build
# directory closed, and that serving it changed nothing else.
#
# Usage: bash .github/scripts/smoke-image.sh <base-url> <admin-token>
#   e.g. bash .github/scripts/smoke-image.sh http://127.0.0.1:5280 "$AGENTSPLICE_ADMIN_API_KEY"
#
# Every check runs and reports; the script exits non-zero if any failed.

set -euo pipefail

base="${1:?usage: smoke-image.sh <base-url> <admin-token>}"
token="${2:?usage: smoke-image.sh <base-url> <admin-token>}"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0

pass() { echo "ok    $*"; }
fail() { echo "FAIL  $*" >&2; failures=$((failures + 1)); }

# request <method> <path> [curl options...]
# Prints the status code. Headers land in $work/headers and the body in $work/body. --path-as-is
# sends dot segments as written, so the traversal checks reach the gateway rather than being
# normalised away by curl.
request() {
  local method="$1" path="$2"
  shift 2
  curl --silent --show-error --path-as-is --request "$method" \
    --dump-header "$work/headers" --output "$work/body" --write-out '%{http_code}' \
    "$@" "$base$path"
}

# header <name>: the value of the last occurrence, without the trailing carriage return.
header() {
  { grep -i "^$1:" "$work/headers" || true; } | tail -n 1 | cut -d: -f2- | sed 's/^ *//' | tr -d '\r'
}

expect_status() { # <expected> <actual> <what>
  if [ "$2" = "$1" ]; then pass "$3 -> $2"; else fail "$3: expected $1, got $2"; fi
}

expect_header() { # <name> <expected substring> <what>
  local value
  value="$(header "$1")"
  case "$value" in
    *"$2"*) pass "$3: $1 contains \"$2\"" ;;
    *) fail "$3: $1 was \"$value\", expected it to contain \"$2\"" ;;
  esac
}

expect_body_without() { # <forbidden substring> <what>
  if grep -qF -- "$1" "$work/body"; then fail "$2: the body contains \"$1\""; else pass "$2: the body does not contain \"$1\""; fi
}

# A browser pointed at the gateway lands on the dashboard.
status="$(request GET /)"
expect_status 302 "$status" "GET /"
expect_header Location "/dashboard/" "GET /"

# The shell, with its policy and its caching.
status="$(request GET /dashboard/)"
expect_status 200 "$status" "GET /dashboard/"
expect_header Content-Type "text/html" "the shell"
expect_header Content-Security-Policy "default-src 'none'" "the shell"
expect_header Content-Security-Policy "script-src 'self'" "the shell"
expect_header Content-Security-Policy "frame-ancestors 'none'" "the shell"
expect_header X-Content-Type-Options "nosniff" "the shell"
expect_header X-Frame-Options "DENY" "the shell"
expect_header Cache-Control "no-cache" "the shell"
cp "$work/body" "$work/shell.html"

# Every file the shell names is served, typed, and cached as immutable. None at all means Vite's
# base and the gateway's mount disagree, which a browser shows as a blank page.
assets="$(grep -oE '/dashboard/assets/[^"]+\.(js|css)' "$work/shell.html" | sort -u || true)"

if [ -z "$assets" ]; then
  fail "the shell names no /dashboard/assets/ files; is Vite's base /dashboard/?"
fi

for asset in $assets; do
  status="$(request GET "$asset")"
  expect_status 200 "$status" "GET $asset"
  case "$asset" in
    *.js) expect_header Content-Type "text/javascript" "$asset" ;;
    *.css) expect_header Content-Type "text/css" "$asset" ;;
  esac
  expect_header Cache-Control "immutable" "$asset"
done

# A deep link survives a reload; a missing asset is a 404, never the shell.
status="$(request GET /dashboard/exchanges)"
expect_status 200 "$status" "GET /dashboard/exchanges"
expect_header Content-Type "text/html" "a deep link"

status="$(request GET /dashboard/assets/missing-0d1e2f.js)"
expect_status 404 "$status" "GET a missing asset"

# Nothing outside the build directory. appsettings.json sits in /app, one level above the build; its
# keys must never come back, whatever status the gateway chooses.
for path in /dashboard/../appsettings.json /dashboard/%2e%2e/appsettings.json /dashboard/..%2fappsettings.json; do
  status="$(request GET "$path")"
  echo "      GET $path -> $status"
  expect_body_without '"publicBaseUrl"' "GET $path"
done

# The APIs answer as they did before the dashboard existed.
status="$(request GET /v1/chat/completions)"
expect_status 405 "$status" "GET /v1/chat/completions"

status="$(request GET /api/v1/system)"
expect_status 401 "$status" "GET /api/v1/system without a token"

status="$(request GET /api/v1/system --header "Authorization: Bearer $token")"
expect_status 200 "$status" "GET /api/v1/system with the token"

status="$(request GET /health/live)"
expect_status 204 "$status" "GET /health/live"

status="$(request GET /health/ready)"
expect_status 200 "$status" "GET /health/ready"

if [ "$failures" -ne 0 ]; then
  echo "$failures check(s) failed." >&2
  exit 1
fi

echo "All checks passed."
