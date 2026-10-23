# Builds the portable zip: self-contained win-x64 app plus
# portable.flag so settings travel with the folder. yt-dlp,
# ffmpeg, and the WebView2 runtime resolve on first run (small
# zip by design); user data is never included.
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $Root

$Configuration = "Release"
$Runtime = "win-x64"
$PublishDir = Join-Path $Root "publish" "portable"
$Version = "0.1.0"

$Csproj = Join-Path $Root "src/DeezNutzDownloader.App/DeezNutzDownloader.App.csproj"
[xml]$Xml = Get-Content -LiteralPath $Csproj
$Configured = $Xml.SelectSingleNode("//Version")
if ($Configured -and $Configured.InnerText.Trim()) {
    $Version = $Configured.InnerText.Trim()
}

Write-Host "Publishing portable $Version..."
if (Test-Path $PublishDir) {
    Remove-Item -Recurse -Force $PublishDir
}

dotnet publish $Csproj `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    --output $PublishDir

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Portable marker: settings/data live beside the exe.
New-Item -ItemType File -Path (Join-Path $PublishDir "portable.flag") -Force | Out-Null

@'
DeezNutzDownloader - portable
============================
1. Unzip anywhere and run DeezNutzDownloader.App.exe.
2. Settings, queue, history, and challenge sessions live in
   the "data" folder next to the exe (portable.flag).
3. yt-dlp, ffmpeg, and the WebView2 runtime resolve on first
   use; a proxy configured in Settings applies to downloads.
4. To update: Settings -> Check for updates now (needs a
   GitHub repository configured), or unzip a newer release
   over this folder while the app is closed.
'@ | Set-Content -LiteralPath (Join-Path $PublishDir "README.txt") -Encoding UTF8

$ZipName = "DeezNutzDownloader-portable-$Version.zip"
$ZipPath = Join-Path $Root "publish" $ZipName
if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath

Write-Host "Portable zip ready: $ZipPath"
