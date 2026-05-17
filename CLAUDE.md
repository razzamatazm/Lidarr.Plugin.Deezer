# CLAUDE.md

Context for future Claude sessions on this fork. Read before making changes.

## What this is

A fork of [TrevTV/Lidarr.Plugin.Deezer](https://github.com/TrevTV/Lidarr.Plugin.Deezer)
with bug fixes to stop the plugin from invalidating its own Deezer session.

Lives at https://github.com/razzamatazm/Lidarr.Plugin.Deezer. Plugin
installs into Lidarr from that GitHub URL via System → Plugins. The
patched build is identifiable by its version number `10.1.99.X` (upstream
uses `10.1.0.X`); the gap leaves headroom so Lidarr's plugin updater
doesn't try to "update" the fork back to upstream.

DeezNET (the underlying Deezer library) is also forked and vendored
as a submodule at `ext/DeezNET`, branch `fix/ua-and-arl-cookie`. The
plugin csproj uses a `ProjectReference` to it instead of NuGet.

## The original problem and root cause

User had Lidarr's Deezer plugin and a separate deemix container running
against the same Deezer account, same ARL cookie. The deemix container
stayed logged in indefinitely. Lidarr's plugin re-authed daily and
eventually failed every download with
`DeezNET.Downloader.GetEncryptedTrackData` errors. After deep
investigation:

**The plugin was rotating its own session.** Two hot paths called
`DeezerClient.SetARL(activeARL)` on a wall-clock timer:

1. `DeezerAPI.TryUpdateToken()` — every 24h on each indexer search
   (deleted from this fork)
2. `DownloadItem.EnsureValidity()` — every 30 min during downloads,
   went through `ARLUtilities.IsValid()` which also called `SetARL`
   (replaced with a cached-state check)

`SetARL` internally calls `deezer.getUserData` with `Cookie: arl=<arl>`,
which Deezer treats as a fresh login — minting a new `sid` session and
invalidating any older session bound to that ARL. The deemix container's
parallel session was collateral damage; the plugin's own download path
401'd a day later when its now-orphaned session expired.

DeezNET's `GWApi.Call()` already retries via `SetToken()` on
`VALID_TOKEN_REQUIRED` from gw-light, so genuine token expiry is handled
on-demand. The wall-clock refresh was pure damage.

Issues confirming the symptom (upstream): #19, #25.

## Fixes in this fork

### Plugin-side
- **Delete `TryUpdateToken`** (`DeezerAPI.cs`) and its 24h call site in
  `DeezerRequestGenerator.GetRequests`.
- **`EnsureValidity` no longer re-auths** (`DownloadItem.cs`). It inspects
  the cached `ActiveUserData["USER"]["USER_ID"]` instead of calling
  `ARLUtilities.IsValid` (which would call `SetARL`).
- **Retry on `NoSourcesAvailableException`** (`DownloadItem.DoTrackDownload`).
  When `media.deezer.com/v1/get_url` returns empty Sources (silent stale
  `license_token`), call `GWApi.SetToken()` to refresh and retry once.
- **MP3 fallback for genuinely-FLAC-less tracks**. If the retry above still
  fails on a FLAC request, fall back to MP3_320 → MP3_128 under a `.mp3`
  extension so the album partially imports rather than failing wholesale.
- **Partial-album completion**. `DownloadItem.DoDownload` marks the item
  `Completed` if at least one track succeeded (`DownloadedSize > 0`). Was
  `Failed` on any track failure, which made Lidarr discard the whole
  download. Now Lidarr imports what worked and flags the rest as missing.
- **Configurable track-level parallelism** (`DeezerSettings.ParallelTracks`,
  default 3, range 1-10). Was hardcoded to 1 because upstream parallel calls
  tripped abuse detection — that root cause is now fixed.
- **Indexer: don't emit 0-byte release entries.** When `FILESIZE_X` sums to
  0 across all tracks, Deezer has no sources for that bitrate. Filtering at
  parser time stops the user from picking releases that would fail.
- **Indexer: "Only Best Available Quality" setting** (default on). Emits
  one row per album at the highest entitled bitrate. Reduces search-result
  noise; Lidarr's quality profile picks the best anyway.
- **Indexer: track count in title** (`[12tr]`). Distinguishes multi-edition
  variants (standard / deluxe / single / EP) that share the same album name.
- **Plugin identity**: `Owner = "razzamatazm"`, `GithubUrl` points at this
  fork. Was upstream — caused install-path collision with upstream and made
  Lidarr poll upstream for "updates."

### DeezNET-side (in the submodule)
- **User-Agent**. `HttpClient` had no UA, a textbook bot-detection tell.
  Set a stable Edge UA on the shared client in `Core.cs`.
- **Seed ARL into the CookieContainer** in `SetARL`. Previously only
  `deezer.getUserData` attached `Cookie: arl=...` manually; everything else
  relied on the cookie jar keeping it. If any response cleared the cookie,
  all subsequent calls silently went anonymous. Now `SetARL` writes the
  cookie into the jar on `.deezer.com`; the manual `Headers.Add` in
  `GWApi.Call` is dropped because the jar covers it.
- **Expose `GWApi.SetToken` as public** so the plugin's
  `NoSourcesAvailableException` retry path can call it.
- **Null-safe `get_url` response chain** in `GetEncryptedTrackData`. When
  Deezer returns no `data` array (stale license_token, geo block,
  entitlement mismatch), the prior `urls.Data.FirstOrDefault()` threw
  `ArgumentNullException` — which consumers don't expect or retry on. Now
  the chain returns null cleanly and falls into the existing
  `NoSourcesAvailableException` path.
- **Version**: `1.2.2-arl-fix.1` to make patched builds identifiable.

## Build + release

CI in `.github/workflows/build.yml` runs on push to main, pull_request, or
workflow_dispatch. It checks out submodules (so `ext/DeezNET` builds in
place), runs `dotnet build`, ILRepacks DeezNET + AngleSharp + SkiaSharp +
TagLibSharp + BouncyCastle into a single plugin DLL, zips it, and uploads
a **draft** GitHub release tagged `10.1.99.<github.run_number>`.

The draft has to be manually published for Lidarr's plugin updater to see
it. (Find it at https://github.com/razzamatazm/Lidarr.Plugin.Deezer/releases.)

GitHub Actions on forks need to be enabled via the UI once before push
events fire workflows. `workflow_dispatch` (via `gh workflow run`) works
without that one-time step.

## Lidarr plugin installation specifics

- Lidarr stores indexer + download-client settings in `/config/lidarr.db`
  in the `Indexers` and `DownloadClients` tables (serialized JSON). The
  plugin DLL only defines the schema; values persist across reinstall as
  long as the `Implementation` class name doesn't change.
- Install path is `/config/plugins/<Plugin.Owner>/<Plugin.Name>`. Changing
  `Plugin.Owner` moves the install directory.
- Settings rebind automatically when reinstalling a plugin whose class
  shape matches — that's why swapping from upstream to this fork (or
  reinstalling fresh) preserves the user's ARL.
- If a previous uninstall left a phantom DB row but the directory was
  removed, Lidarr's next uninstall errors with `Could not find a part of
  the path`. Workaround: `mkdir -p <missing path>` (chmod 777 because
  Lidarr runs as a non-root UID), then click Uninstall again. Or nuke
  the DB row directly:
  ```
  docker exec lidarr sqlite3 /config/lidarr.db \
    "DELETE FROM Plugins WHERE Name='Lidarr.Plugin.Deezer';"
  ```

## What NOT to do

- **Don't add wall-clock SetARL refreshes anywhere.** That was the bug. The
  session stays valid as long as nothing actively re-authenticates. DeezNET's
  `Call()` handles real expiry on-demand via `SetToken` on
  `VALID_TOKEN_REQUIRED`.
- **Don't add a User-Agent override** in the plugin's HttpClient on top of
  DeezNET's. One stable UA across all requests is the goal.
- **Don't switch back to the upstream DeezNET PackageReference** without
  first either upstreaming the patches or vendoring them somewhere stable.
  The `1.2.2-arl-fix.1` version is fictional; NuGet doesn't host it.
- **Don't fake `TorrentInfo`** to populate the "Peers" column in Lidarr's
  search UI. It breaks grab routing (sends Deezer URLs to torrent clients).
  Disambiguation info belongs in the `Title` string.
- **Don't bump the version base below `10.1.99`** — Lidarr's plugin updater
  compares numerically and would nag to "update" to upstream `10.1.0.18+`.

## Upstreaming

Both forks contain reasonable upstream candidates. If sending PRs:

- To `TrevTV/Lidarr.Plugin.Deezer`: the `TryUpdateToken` deletion, the
  `EnsureValidity` rewrite, the NoSourcesAvailable retry, partial-album
  completion, 0-byte filter, track count, OnlyBestQuality setting,
  parallelism setting.
- To `TrevTV/DeezNET`: User-Agent, ARL-in-cookie-jar, public SetToken,
  null-safe get_url chain.

Recommend waiting until the user has run the patched builds for ~a week
of normal usage to demonstrate stability before opening PRs — that's the
evidence the upstream maintainers will care about.

## Repo layout (this fork)

- `src/Lidarr.Plugin.Deezer/` — plugin source
  - `DeezerAPI.cs` — singleton client wrapper, holds the active ARL
  - `Plugin.cs` — Lidarr plugin manifest (Name, Owner, GithubUrl)
  - `ARLUtilities.cs` — rentry.org auto-ARL scraping (only reachable
    when user leaves the ARL field empty)
  - `Indexers/Deezer/`
    - `Deezer.cs` — indexer registration, calls CheckAndSetARL on
      settings refresh
    - `DeezerRequestGenerator.cs` — builds search.music GW requests
    - `DeezerParser.cs` — turns search responses into ReleaseInfo;
      contains the bitrate filtering + track count + best-quality logic
    - `DeezerSettings.cs` — indexer settings (Arl, HideAlbumsWithMissing,
      OnlyBestQuality, EarlyReleaseLimit)
  - `Download/Clients/Deezer/`
    - `Deezer.cs` — download client registration
    - `DeezerProxy.cs`, `DeezerSettings.cs`
    - `Queue/DownloadItem.cs` — per-album download orchestration,
      contains the retry + MP3 fallback + parallelism + partial-completion
    - `Queue/DownloadTaskQueue.cs` — album-level queue (serialized to 1
      via SemaphoreSlim)
- `ext/Lidarr` — Lidarr submodule (NzbDrone.Core / .Common dependencies)
- `ext/DeezNET` — patched DeezNET submodule

## Sister repo

A sidecar service `arlupdater` exists at the user's home that tried to
work around this problem externally (refreshing the ARL via stored
credentials and pushing to Lidarr's API). With this fork's fixes in
place, that service is obsolete. Its own CLAUDE.md notes this.
