$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $Root
dotnet build ".\DeezNutzDownloader.sln" --configuration Debug
