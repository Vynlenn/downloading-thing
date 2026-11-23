using System.IO.Compression;
using System.Net;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;
using DeezNutzDownloader.Core.Updates;

namespace DeezNutzDownloader.Tests;

/// <summary>
/// Pins the scheduling, stall-recovery, progress-coalescing and
/// download-safety behavior (Mark of the Web, filename spoofing,
/// archive and updater hardening).
/// </summary>
public sealed class PerformanceAndSafetyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dnd-perf-safety-tests",
        Guid.NewGuid().ToString("N"));

    public PerformanceAndSafetyTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Best effort test cleanup.
        }
    }

    // ---------- Orchestrator scheduling ----------

    [Fact]
    public async Task BusySingleLaneHost_DoesNotStallOtherHosts()
    {
        // Two Pixeldrain items first, then a generic one. With two
        // workers, the second worker used to park on the Pixeldrain
        // lane; it must skip ahead and finish the generic item while
        // the first Pixeldrain item is still running.
        var queue = new DownloadQueue();
        queue.Enqueue(
            LinkAnalyzer.Analyze(
                "https://pixeldrain.com/u/aaaa1111\n" +
                "https://pixeldrain.com/u/bbbb2222\n" +
                "https://example.test/other.bin"));

        var releasePixeldrain =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var otherFinished =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var service = new FuncTransferService(
            async (request, cancellationToken) =>
            {
                if (request.Source.Host == "example.test")
                {
                    otherFinished.TrySetResult();
                }
                else
                {
                    await releasePixeldrain.Task
                        .WaitAsync(cancellationToken);
                }

                return new DownloadTransferResult(
                    request.Source,
                    Path.Combine(request.DestinationDirectory, "f"),
                    "f",
                    1,
                    1);
            });

        using var orchestrator = new DownloadOrchestrator(queue, service);

        Task<DownloadBatchResult> batch =
            orchestrator.StartAllAsync(_directory, maximumConcurrency: 2);

        Task winner = await Task.WhenAny(
            otherFinished.Task,
            Task.Delay(TimeSpan.FromSeconds(5)));

        releasePixeldrain.TrySetResult();
        DownloadBatchResult result = await batch;

        Assert.Same(otherFinished.Task, winner);
        Assert.Equal(3, result.CompletedCount);
    }

    [Fact]
    public async Task SingleLaneHost_StillRunsOneAtATime()
    {
        var queue = new DownloadQueue();
        queue.Enqueue(
            LinkAnalyzer.Analyze(
                "https://pixeldrain.com/u/aaaa1111\n" +
                "https://pixeldrain.com/u/bbbb2222\n" +
                "https://pixeldrain.com/u/cccc3333"));

        int active = 0;
        int maximum = 0;

        var service = new FuncTransferService(
            async (request, cancellationToken) =>
            {
                int now = Interlocked.Increment(ref active);
                InterlockedMax(ref maximum, now);
                await Task.Delay(30, cancellationToken);
                Interlocked.Decrement(ref active);

                return new DownloadTransferResult(
                    request.Source,
                    Path.Combine(request.DestinationDirectory, "f"),
                    "f",
                    1,
                    1);
            });

        using var orchestrator = new DownloadOrchestrator(queue, service);

        DownloadBatchResult result =
            await orchestrator.StartAllAsync(_directory, maximumConcurrency: 3);

        Assert.Equal(3, result.CompletedCount);
        Assert.Equal(1, maximum);
    }

    // ---------- Transfer: stall recovery + progress coalescing ----------

    [Fact]
    public async Task StalledConnection_ResumesWithRangeInsteadOfHanging()
    {
        byte[] payload = Enumerable.Range(0, 4096)
            .Select(index => (byte)index)
            .ToArray();

        int requests = 0;
        long? resumedFrom = null;

        var handler = new LambdaHandler(request =>
        {
            int call = Interlocked.Increment(ref requests);

            if (call == 1)
            {
                // First connection: half the body, then silence.
                var stalled = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(
                        new StallingStream(payload[..2048]))
                };
                stalled.Content.Headers.ContentLength = payload.Length;
                return stalled;
            }

            resumedFrom = request.Headers.Range?.Ranges.First().From;

            var rest = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[2048..])
            };
            rest.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(
                    2048,
                    payload.Length - 1,
                    payload.Length);
            return rest;
        });

        using var client = new HttpClient(handler);
        var service = new HttpDownloadTransferService(
            client,
            resolvers: null,
            megaDownloader: null,
            stallTimeout: TimeSpan.FromMilliseconds(300));

        DownloadTransferResult result = await service
            .DownloadAsync(
                new DownloadTransferRequest(
                    new Uri("https://example.test/file.bin"),
                    _directory))
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(2, requests);
        Assert.Equal(2048, resumedFrom);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.DestinationPath));
    }

    [Fact]
    public async Task Progress_IsCoalescedButFinalReportIsExact()
    {
        // 4 MB in 1 KB reads = 4,096 chunks; with coalescing the
        // report count must be a small fraction of that.
        byte[] payload = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        var handler = new LambdaHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new TrickleStream(payload, 1024))
            };
            response.Content.Headers.ContentLength = payload.Length;
            return response;
        });

        using var client = new HttpClient(handler);
        var service = new HttpDownloadTransferService(client);
        var reports = new List<DownloadTransferProgress>();

        await service.DownloadAsync(
            new DownloadTransferRequest(
                new Uri("https://example.test/big.bin"),
                _directory),
            new SyncProgress<DownloadTransferProgress>(reports.Add));

        Assert.True(
            reports.Count < 200,
            $"Expected coalesced progress, got {reports.Count} reports.");
        Assert.Equal(payload.Length, reports[^1].BytesReceived);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InlineChecksum_VerifiesAcrossResume(bool hashMatches)
    {
        byte[] payload = Enumerable.Range(0, 8192)
            .Select(index => (byte)(index * 7))
            .ToArray();

        string expected = hashMatches
            ? Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(payload))
            : new string('a', 64);

        int requests = 0;

        var handler = new LambdaHandler(request =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                // Drop the connection halfway through.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(
                        new FailAfterStream(payload, 3000))
                };
            }

            long from = request.Headers.Range!.Ranges.First().From!.Value;
            var rest = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[(int)from..])
            };
            rest.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(
                    from,
                    payload.Length - 1,
                    payload.Length);
            return rest;
        });

        using var client = new HttpClient(handler);
        var service = new HttpDownloadTransferService(
            client,
            new IHostFileResolver[] { new HashResolver(expected) });

        Task<DownloadTransferResult> download = service.DownloadAsync(
            new DownloadTransferRequest(
                new Uri("https://hashhost.test/file"),
                _directory));

        if (hashMatches)
        {
            DownloadTransferResult result = await download;
            Assert.Equal(2, requests);
            Assert.Equal(payload, await File.ReadAllBytesAsync(result.DestinationPath));
        }
        else
        {
            ChecksumMismatchException mismatch =
                await Assert.ThrowsAsync<ChecksumMismatchException>(() => download);
            Assert.True(File.Exists(mismatch.KeptPath));
        }
    }

    // ---------- Mark of the Web ----------

    [Fact]
    public async Task CompletedDownload_GetsMarkOfTheWeb()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handler = new LambdaHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            });

        using var client = new HttpClient(handler);
        var service = new HttpDownloadTransferService(client);

        DownloadTransferResult result = await service.DownloadAsync(
            new DownloadTransferRequest(
                new Uri("https://example.test/setup.exe?token=secret"),
                _directory));

        Assert.True(MarkOfTheWeb.IsMarked(result.DestinationPath));

        string zone = await File.ReadAllTextAsync(
            result.DestinationPath + ":Zone.Identifier");

        Assert.Contains("HostUrl=https://example.test/setup.exe", zone);
        Assert.DoesNotContain("secret", zone);
    }

    [Fact]
    public void MarkOfTheWeb_StripsUserInfoQueryAndFragment()
    {
        Assert.Equal(
            "https://host.test/a/b.zip",
            MarkOfTheWeb.StripForPrivacy(
                new Uri("https://user:pw@host.test/a/b.zip?sig=1#frag")));

        Assert.Null(MarkOfTheWeb.StripForPrivacy(new Uri("file:///c:/x")));
    }

    // ---------- Filename spoofing ----------

    [Theory]
    [InlineData("invoice\u202Efdp.exe", "invoicefdp.exe")]
    [InlineData("photo\u200B.jpg", "photo.jpg")]
    [InlineData("a\u2066b\u2069.txt", "ab.txt")]
    [InlineData("report.pdf                    .exe", "report.pdf .exe")]
    [InlineData("tab\tand\u00A0nbsp.txt", "tab and nbsp.txt")]
    public void SanitizeFileName_RemovesSpoofingTricks(
        string input,
        string expected)
    {
        Assert.Equal(
            expected,
            HttpDownloadTransferService.SanitizeFileName(input));
    }

    // ---------- Archive extraction ----------

    [Theory]
    [InlineData("notes.txt:payload.exe")]
    [InlineData("CON.txt")]
    [InlineData("dir/aux")]
    [InlineData("evil.exe.")]
    [InlineData("dir /evil.exe")]
    [InlineData("a/../b.txt")]
    [InlineData("./x.txt")]
    public void SafeRelativePath_RejectsWindowsAliasTricks(string entry)
    {
        Assert.Null(ArchiveExtraction.SafeRelativePath(entry));
    }

    [Theory]
    [InlineData("release..notes.txt")]
    [InlineData("folder/sub/file.tar.gz")]
    public void SafeRelativePath_AllowsDotsInsideNames(string entry)
    {
        Assert.Equal(entry, ArchiveExtraction.SafeRelativePath(entry));
    }

    [Fact]
    public void ExtractZip_PropagatesMarkOfTheWebToExtractedFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string zip = Path.Combine(_directory, "pack.zip");

        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using StreamWriter writer =
                new(archive.CreateEntry("inner/run.bat").Open());
            writer.Write("echo hi");
        }

        Assert.True(
            MarkOfTheWeb.TryApply(zip, new Uri("https://host.test/pack.zip")));

        ArchiveExtractionResult result = ArchiveExtraction.ExtractZip(zip);

        Assert.Equal(1, result.ExtractedCount);
        Assert.True(
            MarkOfTheWeb.IsMarked(
                Path.Combine(result.OutputDirectory, "inner", "run.bat")));
    }

    // ---------- Updater ----------

    [Theory]
    [InlineData("https://github.com/o/r/releases/download/v1/a.zip", true)]
    [InlineData("https://objects.githubusercontent.com/x/a.zip", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("http://github.com/o/r/releases/download/v1/a.zip", false)]
    [InlineData("https://github.com.evil.test/a.zip", false)]
    [InlineData("https://evilgithubusercontent.com/a.zip", false)]
    [InlineData("https://user@github.com/a.zip", false)]
    [InlineData("not a url", false)]
    public void UpdateAssetUrl_OnlyHttpsGitHubHosts(string url, bool trusted)
    {
        Assert.Equal(trusted, UpdateCheck.IsTrustedAssetUrl(url));
    }

    [Fact]
    public void ParseRelease_ReadsDigestAndDropsUntrustedAssets()
    {
        string hex = new('a', 64);

        string json =
            "{\"tag_name\":\"v2.0.0\",\"assets\":[" +
            "{\"name\":\"app-portable.zip\"," +
            "\"browser_download_url\":\"https://github.com/o/r/releases/download/v2.0.0/app-portable.zip\"," +
            "\"size\":10,\"digest\":\"sha256:" + hex + "\"}," +
            "{\"name\":\"mirror.zip\"," +
            "\"browser_download_url\":\"http://mirror.test/app.zip\"}]}";

        GitHubReleaseInfo? release = UpdateCheck.ParseRelease(json);

        Assert.NotNull(release);
        ReleaseAsset asset = Assert.Single(release.Assets);
        Assert.Equal(hex.ToUpperInvariant(), asset.Sha256);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("md5:abc", null)]
    [InlineData("sha256:xyz", null)]
    public void ParseSha256Digest_RejectsMalformed(string? digest, string? expected)
    {
        Assert.Equal(expected, UpdateCheck.ParseSha256Digest(digest));
    }

    // ---------- helpers ----------

    private static void InterlockedMax(ref int target, int value)
    {
        int current;

        do
        {
            current = Volatile.Read(ref target);

            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private sealed class FuncTransferService : IDownloadTransferService
    {
        private readonly Func<
            DownloadTransferRequest,
            CancellationToken,
            Task<DownloadTransferResult>> _download;

        public FuncTransferService(
            Func<
                DownloadTransferRequest,
                CancellationToken,
                Task<DownloadTransferResult>> download)
        {
            _download = download;
        }

        public Task<DownloadTransferResult> DownloadAsync(
            DownloadTransferRequest request,
            IProgress<DownloadTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return _download(request, cancellationToken);
        }
    }

    private sealed class LambdaHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = _respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public SyncProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value)
        {
            _report(value);
        }
    }

    private sealed class HashResolver : IHostFileResolver
    {
        private readonly string _sha256;

        public HashResolver(string sha256)
        {
            _sha256 = sha256;
        }

        public bool CanResolve(Uri page) => page.Host == "hashhost.test";

        public Task<ResolvedHostFile> ResolveAsync(
            Uri page,
            HttpClient httpClient,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new ResolvedHostFile(
                    new Uri("https://hashhost.test/raw/file.bin"),
                    "file.bin",
                    null,
                    _sha256));
        }
    }

    /// <summary>Serves the first N bytes, then fails like a dropped socket.</summary>
    private sealed class FailAfterStream : MemoryStream
    {
        private readonly int _failAt;

        public FailAfterStream(byte[] data, int failAt)
            : base(data, writable: false)
        {
            _failAt = failAt;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Position >= _failAt)
            {
                throw new IOException("Connection reset.");
            }

            int allowed = (int)Math.Min(buffer.Length, _failAt - Position);
            return base.ReadAsync(buffer[..allowed], cancellationToken);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>Serves its bytes, then never returns another one.</summary>
    private sealed class StallingStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public StallingStream(byte[] data)
        {
            _data = data;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < _data.Length)
            {
                int count = Math.Min(buffer.Length, _data.Length - _position);
                _data.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return ReadAsync(
                buffer.AsMemory(offset, count),
                cancellationToken).AsTask();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>Returns at most chunkSize bytes per read.</summary>
    private sealed class TrickleStream : MemoryStream
    {
        private readonly int _chunkSize;

        public TrickleStream(byte[] data, int chunkSize)
            : base(data, writable: false)
        {
            _chunkSize = chunkSize;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, _chunkSize));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(
                buffer[..Math.Min(buffer.Length, _chunkSize)],
                cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            base.ReadAsync(
                buffer,
                offset,
                Math.Min(count, _chunkSize),
                cancellationToken);
    }
}
