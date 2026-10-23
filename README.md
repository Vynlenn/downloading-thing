# DeezNutzDownloader

A Windows download manager: paste links, auto-crawl pages, queue downloads
across file hosts, and grab video — with resume, checksum checks, and
self-updates.

## Get it

Download the latest portable zip from
[Releases](https://github.com/Vynlenn/downloading-thing/releases),
unzip anywhere, and run `DeezNutzDownloader.App.exe`. Settings, queue,
history, and challenge sessions live in the `data` folder next to the exe
(portable mode). yt-dlp, FFmpeg, and the WebView2 runtime resolve on first
use. To update: Settings → Check for updates now, or unzip a newer release
over the folder while the app is closed.

Requires .NET 8 (self-contained in the portable zip).

## What it does

- **Link input** — paste links (auto-detected from the clipboard), group
  them by host, and see validity before queueing.
- **Page crawl** — fetch a page and pull out its file-host links
  (multi-page aware).
- **Hosts**
  - Pixeldrain files and lists — real names/sizes up front, per-file
    downloads, SHA-256 verified after each file.
  - Gofile shares — single files direct, folders expand to one queue row
    per file (subfolders kept).
  - MediaFire shares, MEGA files and folders (client-side decryption),
    generic HTTP(S) with resume and 429/503 backoff.
  - yt-dlp video — format picker, playlists, subtitle sidecars,
    cookies-from-browser, proxy support.
- **Honest errors** — CAPTCHA walls open an in-app browser to solve once;
  removed links say so plainly (no endless retry); quota/rate limits stay
  queued with backoff.
- **Queue** — concurrent downloads, per-host politeness, pause/cancel,
  persistence across restarts, duplicate detection against history.
- **Extras** — quiet-hours speed caps, ZIP extraction prompt, light/dark
  themes, HTTP/HTTPS proxy with credentials, self-updater.

## Settings worth knowing

- **Update repository** (`owner/name`) enables the in-app updater.
- **External tools** can be auto-managed or pointed at your own yt-dlp /
  FFmpeg binaries.
- Proxy is HTTP/HTTPS only — SOCKS is not supported. DRM-protected video
  and password-protected shares are not supported either.

## Develop

```powershell
scripts/build.ps1      # build the solution (Debug)
scripts/test.ps1       # full test suite
scripts/run-debug.ps1  # run the app
scripts/publish-portable.ps1  # self-contained portable zip -> publish/
```

- `src/DeezNutzDownloader.Core` — queue, orchestrator, link analysis,
  update logic (no I/O at the edges).
- `src/DeezNutzDownloader.Adapters` — host resolvers, HTTP transfer,
  MEGA/yt-dlp services.
- `src/DeezNutzDownloader.Infrastructure` — settings/queue/history stores,
  logging.
- `src/DeezNutzDownloader.App` — WPF UI.
- `tests/DeezNutzDownloader.Tests` — ~300 tests; resolvers and parsers are
  covered without network access.

CI builds and tests every push/PR to `main`. Pushing a `v*` tag builds the
portable zip and attaches it to a GitHub release.

## Status

v0.1.0 is the first release. The roadmap is the honest-limitations list
above plus whatever the queue UI teaches us next.
