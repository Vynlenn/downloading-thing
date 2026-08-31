$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $Root
dotnet test ".\DeezNutzDownloader.sln" --configuration Debug
