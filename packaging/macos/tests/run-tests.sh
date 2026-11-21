#!/bin/bash
#
# macOS CI: compile the privileged helper with sanitizers and run its typed
# contract tests. This script is not part of the shipped app.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TMP="$(mktemp -d /tmp/vpnrouter-helper-tests.XXXXXX)"
trap 'rm -rf "$TMP"' EXIT

clang -std=c11 -O1 -g -Wall -Wextra -Werror -Wno-unused-function \
    -fsanitize=address,undefined -fno-omit-frame-pointer \
    -I "$ROOT" \
    -o "$TMP/contract-tests" \
    "$ROOT/tests/contract-tests.c"

ASAN_OPTIONS=detect_leaks=0 "$TMP/contract-tests"
bash "$ROOT/tests/installer-transaction.sh"
