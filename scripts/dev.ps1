param([int]$Port = 5080)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (-not $env:Security__AccessKey) {
    $credential = Get-Credential -UserName 'Loomi' -Message 'Enter your workspace access key (at least 32 characters). This is not your ChatGPT password.'
    $env:Security__AccessKey = $credential.GetNetworkCredential().Password
}
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/Loomi --no-launch-profile --urls "http://localhost:$Port"
