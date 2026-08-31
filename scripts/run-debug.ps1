$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $Root
dotnet run --project ".\src\DeezNutzDownloader.App\DeezNutzDownloader.App.csproj"
