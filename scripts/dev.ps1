param([int]$Port = 5080)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet build --nologo
# Chromium ships per Playwright version; installing is a fast no-op once the matching build is present.
& './src/Loomi/bin/Debug/net10.0/playwright.ps1' install chromium
$env:ASPNETCORE_ENVIRONMENT = 'Development'
# Development falls back to the built-in access key and logs it. Set Security__AccessKey first to use your own.
dotnet run --project src/Loomi --no-build --no-launch-profile --urls "http://localhost:$Port"
