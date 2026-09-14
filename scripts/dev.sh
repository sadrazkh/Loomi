#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build --nologo
# Chromium ships per Playwright version; installing is a fast no-op once the matching build is present.
if command -v pwsh >/dev/null 2>&1; then
  pwsh src/Loomi/bin/Debug/net10.0/playwright.ps1 install chromium
else
  echo "pwsh not found. Run 'pwsh src/Loomi/bin/Debug/net10.0/playwright.ps1 install chromium' before connecting the browser." >&2
fi
export ASPNETCORE_ENVIRONMENT=Development
# Development falls back to the built-in access key and logs it. Set Security__AccessKey first to use your own.
dotnet run --project src/Loomi --no-build --no-launch-profile --urls http://localhost:5080
