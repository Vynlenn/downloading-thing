using System.Net;
using System.Net.Http.Headers;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Adapters.Media;
using DeezNutzDownloader.Core.Challenges;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class QueueFeatureTests
{
    // ---------- Link checking ----------

    [Fact]
    public async Task LinkCheck_PlainFile_ReportsNameAndSize()
    {
        var checker = new LinkChecker(new HttpClient(new Respond(request =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(Array.Empty<byte>())
            };
            response.Content.Headers.ContentLength = 12_345;
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            response.Content.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("attachment") { FileName = "\"pack.zip\"" };
            return response;
        })));

        LinkCheckResult result =
            await checker.CheckAsync(new Uri("https://files.example.test/dl?id=7"));

        Assert.Equal(LinkCheckStatus.Online, result.Status);
        Assert.Equal("pack.zip", result.FileName);
        Assert.Equal(12_345, result.SizeBytes);
    }

    [Fact]
    public async Task LinkCheck_HeadRejected_FallsBackToOneByteGet()
    {
        var methods = new List<HttpMethod>();

        var checker = new LinkChecker(new HttpClient(new Respond(request =>
        {
            methods.Add(request.Method);

            if (request.Method == HttpMethod.Head)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            Assert.Equal(0, request.Headers.Range!.Ranges.First().From);
            Assert.Equal(0, request.Headers.Range!.Ranges.First().To);

            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(new byte[1])
            };
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 999);
            return partial;
        })));

        LinkCheckResult result =
            await checker.CheckAsync(new Uri("https://files.example.test/a.bin"));

        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Get }, methods);
        Assert.Equal(LinkCheckStatus.Online, result.Status);
        Assert.Equal(999, result.SizeBytes);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, LinkCheckStatus.Offline)]
    [InlineData(HttpStatusCode.Gone, LinkCheckStatus.Offline)]
    [InlineData(HttpStatusCode.TooManyRequests, LinkCheckStatus.Limited)]
    [InlineData(HttpStatusCode.Unauthorized, LinkCheckStatus.NeedsAttention)]
    [InlineData(HttpStatusCode.InternalServerError, LinkCheckStatus.Unknown)]
    public void LinkCheck_StatusCodes_Classify(HttpStatusCode code, LinkCheckStatus expected)
    {
        var response = new HttpResponseMessage(code)
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };

        Assert.Equal(
            expected,
            LinkChecker.FromResponse(response, new Uri("https://x.test/f")).Status);
    }

    [Fact]
    public void LinkCheck_HtmlPage_IsFlaggedNotOnline()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html></html>")
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");

        LinkCheckResult result =
            LinkChecker.FromResponse(response, new Uri("https://x.test/page"));

        Assert.Equal(LinkCheckStatus.NeedsAttention, result.Status);
        Assert.Contains("web page", result.Message);
    }

    [Fact]
    public void LinkCheck_ResolverErrors_Classify()
    {
        Assert.Equal(
            LinkCheckStatus.Limited,
            LinkChecker.Classify(new HostThrottledException("h", "quota")).Status);

        Assert.Equal(
            LinkCheckStatus.NeedsAttention,
            LinkChecker.Classify(
                new ChallengeDetectedException("h", ChallengeKind.Captcha, "https://h/", "captcha")).Status);

        Assert.Equal(
            LinkCheckStatus.Offline,
            LinkChecker.Classify(
                new InvalidOperationException("Pixeldrain has no file abc. It was removed.")).Status);

        Assert.Equal(
            LinkCheckStatus.Unknown,
            LinkChecker.Classify(new InvalidOperationException("Try again shortly.")).Status);
    }

    [Fact]
    public async Task LinkCheck_SingleLaneHostsAreCheckedOneAtATime()
    {
        int active = 0;
        int maximum = 0;

        var checker = new LinkChecker(new HttpClient(new Respond(async request =>
        {
            int now = Interlocked.Increment(ref active);
            InterlockedMax(ref maximum, now);
            await Task.Delay(40);
            Interlocked.Decrement(ref active);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        })), new IHostFileResolver[] { new NotFoundResolver() });

        var links = Enumerable.Range(0, 4)
            .Select(index => (Guid.NewGuid(), new Uri($"https://mediafire.com/file/{index}")))
            .ToList();

        var results = new List<LinkCheckResult>();
        await checker.CheckAllAsync(links, (_, result) => { lock (results) results.Add(result); });

        Assert.Equal(4, results.Count);
        Assert.Equal(1, maximum);
    }

    // ---------- 7-Zip ----------

    [Fact]
    public void SevenZipListing_ParsesEntriesAfterSeparator()
    {
        const string listing =
            "7-Zip 24.08\r\n\r\nListing archive: pack.rar\r\n\r\n--\r\n" +
            "Path = pack.rar\r\nType = Rar5\r\n\r\n----------\r\n" +
            "Path = docs\r\nFolder = +\r\nSize = 0\r\n\r\n" +
            "Path = docs\\readme.txt\r\nFolder = -\r\nSize = 120\r\nEncrypted = -\r\n\r\n" +
            "Path = secret.bin\r\nFolder = -\r\nSize = 4096\r\nEncrypted = +\r\n";

        IReadOnlyList<SevenZipEntry> entries = SevenZipArchives.ParseListing(listing);

        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].IsFolder);
        Assert.Equal("docs\\readme.txt", entries[1].Path);
        Assert.Equal(120, entries[1].Size);
        Assert.True(entries[2].Encrypted);
        Assert.DoesNotContain(entries, entry => entry.Path == "pack.rar");
    }

    [Theory]
    [InlineData("movie.rar", true)]
    [InlineData("movie.part1.rar", true)]
    [InlineData("movie.part01.rar", true)]
    [InlineData("movie.part2.rar", false)]
    [InlineData("movie.part10.rar", false)]
    [InlineData("backup.7z", true)]
    [InlineData("backup.7z.001", true)]
    [InlineData("backup.7z.002", false)]
    [InlineData("notes.zip", false)]
    public void SevenZip_OnlyFirstPartIsOffered(string name, bool expected)
    {
        Assert.Equal(expected, SevenZipArchives.IsSupported(name));
    }

    [Theory]
    [InlineData("C:\\dl\\movie.part1.rar", "C:\\dl\\movie.extracted")]
    [InlineData("C:\\dl\\movie.rar", "C:\\dl\\movie.extracted")]
    [InlineData("C:\\dl\\backup.7z.001", "C:\\dl\\backup.extracted")]
    public void SevenZip_OutputFolderDropsPartSuffix(string archive, string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, SevenZipArchives.DefaultOutputDirectory(archive));
    }

    // ---------- Queue views ----------

    [Fact]
    public void QueueFilter_MatchesStateTextAndHost()
    {
        var downloading = Item("https://cdn.example.test/a/movie.mkv") with
        {
            State = DownloadQueueItemState.Downloading
        };
        var paused = Item("https://pixeldrain.com/u/abc") with
        {
            State = DownloadQueueItemState.Paused,
            PackageName = "Holiday"
        };
        var program = Item("https://example.test/setup.exe");
        var offline = Item("https://example.test/gone.zip") with
        {
            LinkStatus = LinkCheckStatus.Offline
        };

        DownloadQueueItem[] all = { downloading, paused, program, offline };

        Assert.Equal(new[] { downloading }, QueueViews.Filter(all, null, QueueFilterKind.Active));
        Assert.Equal(new[] { paused }, QueueViews.Filter(all, "holiday", QueueFilterKind.All));
        Assert.Equal(new[] { paused }, QueueViews.Filter(all, "PIXELDRAIN", QueueFilterKind.All));
        Assert.Equal(new[] { program }, QueueViews.Filter(all, null, QueueFilterKind.Flagged));
        Assert.Equal(new[] { offline }, QueueViews.Filter(all, null, QueueFilterKind.LinkProblems));
        Assert.Same(all, QueueViews.Filter(all, "  ", QueueFilterKind.All));
    }

    [Fact]
    public void QueueSummary_TotalsSpeedAndHonestEta()
    {
        DownloadQueueItem[] items =
        {
            Item("https://x.test/1") with
            {
                State = DownloadQueueItemState.Downloading,
                BytesReceived = 0, TotalBytes = 1000, SpeedBytesPerSecond = 100
            },
            Item("https://x.test/2") with
            {
                State = DownloadQueueItemState.Downloading,
                BytesReceived = 500, TotalBytes = 1500, SpeedBytesPerSecond = 100
            },
            Item("https://x.test/3"),
            Item("https://x.test/4") with { State = DownloadQueueItemState.Paused },
            Item("https://x.test/5") with { State = DownloadQueueItemState.Completed },
            Item("https://x.test/6") with { State = DownloadQueueItemState.Failed }
        };

        QueueSummary summary = QueueViews.Summarize(items);

        Assert.Equal(2, summary.Active);
        Assert.Equal(1, summary.Waiting);
        Assert.Equal(1, summary.Paused);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(200, summary.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(10), summary.EstimatedRemaining);

        string text = QueueViews.FormatSummary(summary);
        Assert.Contains("2 downloading at 200 B/s", text);
        Assert.Contains("10s left", text);
    }

    [Fact]
    public void QueueSummary_NoEtaWhenAnActiveSizeIsUnknown()
    {
        DownloadQueueItem[] items =
        {
            Item("https://x.test/1") with
            {
                State = DownloadQueueItemState.Downloading,
                TotalBytes = null, SpeedBytesPerSecond = 100
            }
        };

        Assert.Null(QueueViews.Summarize(items).EstimatedRemaining);
    }

    // ---------- Host-limit retries ----------

    [Fact]
    public void RetryPlanner_HonorsRetryAfterAndGivesUp()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var withRetryAfter = new HostThrottledException(
            "h", "429", retryAfter: TimeSpan.FromMinutes(7));

        Assert.Equal(
            now + TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(5),
            ThrottleRetryPlanner.NextAttempt(withRetryAfter, 0, now));

        var quota = new HostThrottledException("h", "quota", isQuotaExhausted: true);

        Assert.Equal(now + TimeSpan.FromMinutes(15), ThrottleRetryPlanner.NextAttempt(quota, 0, now));
        Assert.Equal(now + TimeSpan.FromHours(4), ThrottleRetryPlanner.NextAttempt(quota, 5, now));
        Assert.Null(ThrottleRetryPlanner.NextAttempt(quota, ThrottleRetryPlanner.MaximumAutomaticRetries, now));

        var huge = new HostThrottledException("h", "429", retryAfter: TimeSpan.FromDays(3));
        Assert.Equal(now + TimeSpan.FromHours(12), ThrottleRetryPlanner.NextAttempt(huge, 0, now));
    }

    // ---------- helpers ----------

    private static DownloadQueueItem Item(string url) =>
        new(Guid.NewGuid(), url, Path.GetFileName(new Uri(url).AbsolutePath));

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

    private sealed class Respond : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public Respond(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = request => Task.FromResult(respond(request));
        }

        public Respond(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _respond(request);
    }

    /// <summary>Resolver that asks the (counting) server and reports not-found.</summary>
    private sealed class NotFoundResolver : IHostFileResolver
    {
        public bool CanResolve(Uri page) => page.Host == "mediafire.com";

        public async Task<ResolvedHostFile> ResolveAsync(
            Uri page,
            HttpClient httpClient,
            CancellationToken cancellationToken = default)
        {
            using HttpResponseMessage _ = await httpClient.GetAsync(page, cancellationToken);
            throw new InvalidOperationException("MediaFire share was not found. It may be removed.");
        }
    }
}
