#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [ -z "${Security__AccessKey:-}" ]; then
  read -rsp 'Workspace access key (at least 32 characters): ' Security__AccessKey
  echo
  export Security__AccessKey
fi
export ASPNETCORE_ENVIRONMENT=Development
dotnet run --project src/Loomi --no-launch-profile --urls http://localhost:5080
