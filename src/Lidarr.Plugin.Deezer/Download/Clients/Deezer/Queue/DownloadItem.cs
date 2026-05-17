using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezNET;
using DeezNET.Data;
using DeezNET.Exceptions;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Deezer;

namespace NzbDrone.Core.Download.Clients.Deezer.Queue
{
    public class DownloadItem
    {
        public static async Task<DownloadItem> From(RemoteAlbum remoteAlbum)
        {
            string url = remoteAlbum.Release.DownloadUrl.Trim();
            Bitrate bitrate;

            if (remoteAlbum.Release.Codec == "FLAC")
                bitrate = Bitrate.FLAC;
            else if (remoteAlbum.Release.Container == "320")
                bitrate = Bitrate.MP3_320;
            else
                bitrate = Bitrate.MP3_128;

            DownloadItem item = null;
            if (DeezerURL.TryParse(url, out var deezerUrl))
            {
                item = new()
                {
                    ID = Guid.NewGuid().ToString(),
                    Status = DownloadItemStatus.Queued,
                    Bitrate = bitrate,
                    RemoteAlbum = remoteAlbum,
                    _deezerUrl = deezerUrl,
                };

                await item.SetDeezerData();
            }

            return item;
        }

        public string ID { get; private set; }

        public string Title { get; private set; }
        public string Artist { get; private set; }
        public bool Explicit { get; private set; }

        public RemoteAlbum RemoteAlbum {  get; private set; }

        public string DownloadFolder { get; private set; }

        public Bitrate Bitrate { get; private set; }
        public DownloadItemStatus Status { get; set; }

        public float Progress { get => DownloadedSize / (float)Math.Max(TotalSize, 1); }
        public long DownloadedSize { get; private set; }
        public long TotalSize { get; private set; }

        public int FailedTracks { get; private set; }

        private (long id, long size)[] _tracks;
        private DeezerURL _deezerUrl;
        private JToken _deezerAlbum;

        public async Task DoDownload(DeezerSettings settings, Logger logger, CancellationToken cancellation = default)
        {
            List<Task> tasks = new();
            // Track-level parallelism within an album. User-configurable via
            // DeezerSettings.ParallelTracks (1-10, default 3 matching deemix).
            // Clamp defensively in case stored settings predate the field.
            var parallel = settings.ParallelTracks > 0 ? settings.ParallelTracks : 3;
            using SemaphoreSlim semaphore = new(parallel, parallel);
            foreach (var (trackId, trackSize) in _tracks)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellation);
                    try
                    {
                        await DoTrackDownload(trackId, settings, cancellation);
                        DownloadedSize += trackSize;
                    }
                    catch (TaskCanceledException) { }
                    catch (Exception ex)
                    {
                        logger.Error("Error while downloading Deezer track " + trackId);
                        logger.Error(ex.ToString());
                        FailedTracks++;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellation));
            }

            await Task.WhenAll(tasks);
            // Mark Completed unless EVERY track failed. Deezer often lacks
            // FLAC for one or two random tracks on an album (regional/licensing
            // quirks) and a partial download is far more useful to Lidarr than
            // a total failure: Lidarr imports what's there, flags the rest as
            // missing, and the user can manual-import or trigger a fresh search.
            // Only when nothing succeeded do we surface Failed so Lidarr can
            // try a different release.
            Status = DownloadedSize > 0 ? DownloadItemStatus.Completed : DownloadItemStatus.Failed;
        }

        private async Task DoTrackDownload(long track, DeezerSettings settings, CancellationToken cancellation = default)
        {
            var page = await DeezerAPI.Instance.Client.GWApi.GetTrackPage(track, cancellation);

            var songTitle = page["DATA"]!["SNG_TITLE"]!.ToString();
            var songVersion = page["DATA"]?["VERSION"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(songVersion))
                songTitle = $"{songTitle} {songVersion}";

            var albumTitle = page["DATA"]!["ALB_TITLE"]!.ToString();
            var albumVersion = _deezerAlbum["DATA"]?["VERSION"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(albumVersion))
                albumTitle = $"{albumTitle} {albumVersion}";

            var artistName = page["DATA"]!["ART_NAME"]!.ToString();
            var duration = page["DATA"]!["DURATION"]!.Value<int>();

            var ext = Bitrate == Bitrate.FLAC ? "flac" : "mp3";
            var outPath = Path.Combine(settings.DownloadPath, MetadataUtilities.GetFilledTemplate("%albumartist%/%album%/", ext, page, _deezerAlbum), MetadataUtilities.GetFilledTemplate("%track% - %title%.%ext%", ext, page, _deezerAlbum));
            var outDir = Path.GetDirectoryName(outPath)!;

            DownloadFolder = outDir;
            if (!Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            try
            {
                await DeezerAPI.Instance.Client.Downloader.WriteRawTrackToFile(track, outPath, Bitrate, null, cancellation);
            }
            catch (NoSourcesAvailableException)
            {
                // media.deezer.com returned no sources for this bitrate. First
                // try refreshing the license_token cached in ActiveUserData —
                // it goes stale faster than the sid cookie does and Deezer
                // silently 200s with an empty Sources array rather than
                // returning VALID_TOKEN_REQUIRED, so DeezNET's gw-light retry
                // path never triggers.
                await DeezerAPI.Instance.Client.GWApi.SetToken(cancellation);
                try
                {
                    await DeezerAPI.Instance.Client.Downloader.WriteRawTrackToFile(track, outPath, Bitrate, null, cancellation);
                }
                catch (NoSourcesAvailableException) when (Bitrate == Bitrate.FLAC)
                {
                    // Genuine FLAC unavailability after a fresh license_token.
                    // Some tracks on Deezer simply aren't offered in FLAC
                    // (regional/licensing). Fall back to MP3_320 (then MP3_128)
                    // so the rest of the album can still import. Rename the
                    // extension to .mp3 to keep the file honest — Lidarr will
                    // import it at the lower quality and the album will show
                    // mixed quality, which the user can re-search later if
                    // they want.
                    var mp3Path = Path.ChangeExtension(outPath, "mp3");
                    await DeezerAPI.Instance.Client.Downloader.WriteRawTrackToFile(track, mp3Path, Bitrate.MP3_320, Bitrate.MP3_128, cancellation);
                }
            }

            var plainLyrics = string.Empty;
            List<SyncLyrics> syncLyrics = null;

            var lyrics = await DeezerAPI.Instance.Client.Downloader.FetchLyricsFromDeezer(track, cancellation);
            if (lyrics.HasValue)
            {
                plainLyrics = lyrics.Value.plainLyrics;

                if (settings.SaveSyncedLyrics)
                    syncLyrics = lyrics.Value.syncLyrics;
            }

            if (settings.UseLRCLIB && (string.IsNullOrWhiteSpace(plainLyrics) || (settings.SaveSyncedLyrics && !(syncLyrics?.Any() ?? false))))
            {
                lyrics = await DeezerAPI.Instance.Client.Downloader.FetchLyricsFromLRCLIB("lrclib.net", songTitle, artistName, albumTitle, duration, cancellation);
                if (lyrics.HasValue)
                {
                    if (string.IsNullOrWhiteSpace(plainLyrics))
                        plainLyrics = lyrics.Value.plainLyrics;
                    if (settings.SaveSyncedLyrics && !(syncLyrics?.Any() ?? false))
                        syncLyrics = lyrics.Value.syncLyrics;
                }
            }

            await DeezerAPI.Instance.Client.Downloader.ApplyMetadataToFile(track, outPath, 512, plainLyrics, token: cancellation);

            if (syncLyrics != null)
                await CreateLrcFile(Path.Combine(outDir, MetadataUtilities.GetFilledTemplate("%track% - %title%.%ext%", "lrc", page, _deezerAlbum)), syncLyrics);

            // TODO: this is currently a waste of resources, if this pr ever gets merged, it can be reenabled
            // https://github.com/Lidarr/Lidarr/pull/4370
            /* try
            {
                string artOut = Path.Combine(outDir, "folder.jpg");
                if (!File.Exists(artOut))
                {
                    byte[] bigArt = await DeezerAPI.Instance.Client.Downloader.GetArtBytes(page["DATA"]!["ALB_PICTURE"]!.ToString(), 1024, cancellation);
                    await File.WriteAllBytesAsync(artOut, bigArt, cancellation);
                }
            }
            catch (UnavailableArtException) { } */
        }

        public void EnsureValidity()
        {
            // Inspect the user data captured at initial login. Do NOT call SetARL
            // here — it hits deezer.getUserData, which Deezer treats as a fresh
            // session and rotates the sid, invalidating any other client (e.g.
            // a parallel deemix container) using the same ARL. If the session
            // has actually expired, DeezNET's GWApi.Call will retry via
            // SetToken on VALID_TOKEN_REQUIRED.
            var userData = DeezerAPI.Instance?.Client?.GWApi?.ActiveUserData;
            var userId = userData?["USER"]?["USER_ID"]?.Value<long>() ?? 0;
            if (userId == 0)
                throw new InvalidARLException("The applied ARL is not valid for downloading, cannot continue.");
        }

        private async Task SetDeezerData(CancellationToken cancellation = default)
        {
            if (_deezerUrl.EntityType != EntityType.Album)
                throw new InvalidOperationException();

            var albumPage = await DeezerAPI.Instance.Client.GWApi.GetAlbumPage(_deezerUrl.Id, cancellation);

            var filesizeKey = Bitrate switch
            {
                Bitrate.MP3_128 => "FILESIZE_MP3_128",
                Bitrate.MP3_320 => "FILESIZE_MP3_320",
                Bitrate.FLAC => "FILESIZE_FLAC",
                _ => "FILESIZE"
            };

            _tracks ??= albumPage["SONGS"]!["data"]!.Select(t => (t["SNG_ID"]!.Value<long>(), t[filesizeKey]!.Value<long>())).ToArray();
            _deezerAlbum = albumPage;

            var album = albumPage["DATA"]!.ToObject<DeezerGwAlbum>();

            Title = album.AlbumTitle;
            Artist = album.ArtistName;
            Explicit = album.Explicit;
            TotalSize = _tracks.Sum(t => t.size);
        }

        private static async Task CreateLrcFile(string lrcFilePath, List<SyncLyrics> syncLyrics)
        {
            StringBuilder lrcContent = new();
            foreach (var lyric in syncLyrics)
            {
                if (!string.IsNullOrEmpty(lyric.LrcTimestamp) && !string.IsNullOrEmpty(lyric.Line))
                    lrcContent.AppendLine(CultureInfo.InvariantCulture, $"{lyric.LrcTimestamp} {lyric.Line}");
            }
            await File.WriteAllTextAsync(lrcFilePath, lrcContent.ToString());
        }
    }
}
