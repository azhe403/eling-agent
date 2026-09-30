#!/usr/bin/env sh
# Shared, change-scoped CI gate for .husky/pre-commit and .husky/pre-push.
# Runs only the checks relevant to what changed; both hooks call this SAME
# script, so their behaviour is identical.
#
# Usage:
#   sh scripts/ci-check.sh           # staged files  (pre-commit)
#   sh scripts/ci-check.sh --push    # files being pushed (pre-push)
set -e

MODE="${1:-}"

if [ "$MODE" = "--push" ]; then
  BRANCH="$(git rev-parse --abbrev-ref HEAD)"
  FILES="$(git diff --name-only '@{push}..HEAD' 2>/dev/null \
    || git diff --name-only "origin/$BRANCH..HEAD" 2>/dev/null \
    || git diff --name-only HEAD~1..HEAD 2>/dev/null \
    || true)"
else
  FILES="$(git diff --cached --name-only)"
fi

if [ -z "$FILES" ]; then
  echo "[eling] no changed files detected - skipping checks"
  exit 0
fi

HAS_FRONTEND=0
HAS_BACKEND=0

# Toolchain / hook changes: run everything.
if printf '%s\n' "$FILES" | grep -Eq '^(\.husky/|package\.json|pnpm-lock\.yaml)'; then
  echo "[eling] hook/toolchain changed - running full suite"
  HAS_FRONTEND=1
  HAS_BACKEND=1
else
  if printf '%s\n' "$FILES" | grep -Eq '^src/frontend/'; then HAS_FRONTEND=1; fi
  if printf '%s\n' "$FILES" | grep -Eq '^src/backend/|^src/desktop/|^tests/|Eling\.slnx$|Directory\.Build\.props$|\.csproj$'; then HAS_BACKEND=1; fi
fi

if [ "$HAS_FRONTEND" -eq 0 ] && [ "$HAS_BACKEND" -eq 0 ]; then
  echo "[eling] no backend/frontend changes - skipping checks"
  exit 0
fi

if [ "$HAS_BACKEND" -eq 1 ]; then
  echo "[eling] backend: restore + build (solution)"
  cross-env ELING_OUTPUT_ROOT=.bin-test dotnet restore Eling.slnx
  cross-env ELING_OUTPUT_ROOT=.bin-push dotnet build Eling.slnx
  echo "[eling] backend: tests (per project, excluding DashboardLifecycleTests)"
  # dotnet test driven through Eling.slnx starts the backend test host and then
  # never reports a result for it, while still exiting 0. Every backend assertion
  # was therefore dropped from this gate and the hook looked green. Verified by
  # A/B: the same project invoked directly reports all 413 tests, with and without
  # ELING_OUTPUT_ROOT set, so the solution file is the trigger and not the env var.
  cross-env ELING_OUTPUT_ROOT=.bin-test dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj
  cross-env ELING_OUTPUT_ROOT=.bin-test dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter FullyQualifiedName!~DashboardLifecycleTests
  cross-env ELING_OUTPUT_ROOT=.bin-test dotnet test tests/Eling.Desktop.Tests/Eling.Desktop.Tests.csproj
fi

if [ "$HAS_FRONTEND" -eq 1 ]; then
  echo "[eling] frontend: lint + typecheck + build"
  pnpm lint:frontend
  pnpm typecheck:frontend
  pnpm build:frontend
fi
