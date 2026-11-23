using System.Net;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

public sealed class FilesterAndBrowserRoutingTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dnd-browser-routing-tests",
        Guid.NewGuid().ToString("N"));

    public FilesterAndBrowserRoutingTests()
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

    [Theory]
    [InlineData("https://filester.me/d/AbCd123", true, false, false)]
    [InlineData("https://filester.gg/d/AbCd123", true, false, false)]
    [InlineData("https://filester.me/h/AbCd123", false, true, false)]
    [InlineData("https://filester.me/t/89d50f21-094d-4c7f-acaf-5c7cdeddbe60", false, false, true)]
    [InlineData("https://filester.me/", false, false, false)]
    [InlineData("https://filester.me.evil.test/d/x", false, false, false)]
    public void FilesterLinks_Classify(string url, bool page, bool hotlink, bool thumbnail)
    {
        var uri = new Uri(url);
        Assert.Equal(page, FilesterLinks.IsFilePage(uri));
        Assert.Equal(hotlink, FilesterLinks.IsHotlink(uri));
        Assert.Equal(thumbnail, FilesterLinks.IsThumbnail(uri));
    }

    [Fact]
    public async Task FilesterThumbnail_IsRefusedWithoutAnyRequest()
    {
        int requests = 0;
        HttpDownloadTransferService service = Service(() => requests++, out _);

        NotAFileException failure = await Assert.ThrowsAsync<NotAFileException>(
            () => service.DownloadAsync(new DownloadTransferRequest(
                new Uri("https://filester.me/t/89d50f21-094d-4c7f-acaf-5c7cdeddbe60"), _directory)));

        Assert.Equal(0, requests);
        Assert.Contains("thumbnail", failure.Message);
    }

    [Fact]
    public async Task FilesterFilePage_GoesToBrowserInGenericMode()
    {
        HttpDownloadTransferService service = Service(() => { }, out RecordingBrowser browser);

        DownloadTransferResult result = await service.DownloadAsync(
            new DownloadTransferRequest(new Uri("https://filester.me/d/AbCd123"), _directory));

        Assert.Equal(BrowserDownloadMode.Generic, browser.Mode);
        Assert.Equal("clip.mp4", result.FileName);
    }

    [Fact]
    public async Task BunkrFilePage_UsesBunkrMode()
    {
        HttpDownloadTransferService service = Service(() => { }, out RecordingBrowser browser);

        await service.DownloadAsync(
            new DownloadTransferRequest(new Uri("https://bunkr.cr/f/AbC123xyz"), _directory));

        Assert.Equal(BrowserDownloadMode.Bunkr, browser.Mode);
    }

    [Fact]
    public async Task ViaBrowserFlag_SendsAnyLinkToTheBrowser()
    {
        int requests = 0;
        HttpDownloadTransferService service = Service(() => requests++, out RecordingBrowser browser);

        await service.DownloadAsync(new DownloadTransferRequest(
            new Uri("https://anyhost.example.test/share/123"),
            _directory,
            ViaBrowser: true));

        Assert.Equal(0, requests);
        Assert.Equal(BrowserDownloadMode.Generic, browser.Mode);
    }

    [Fact]
    public async Task FilesterHotlink_DownloadsOverPlainHttp()
    {
        int requests = 0;
        HttpDownloadTransferService service = Service(() => requests++, out RecordingBrowser browser);

        await service.DownloadAsync(new DownloadTransferRequest(
            new Uri("https://filester.me/h/AbCd123"), _directory));

        Assert.Equal(1, requests);
        Assert.Null(browser.Mode);
    }

    [Fact]
    public void FilesterIsOneFileAtATime()
    {
        Assert.True(HostThrottlePolicy.IsSingleLaneHost("filester.me"));
    }

    [Fact]
    public void Queue_RequeueViaBrowser_SetsFlagResetsAndPersists()
    {
        var queue = new DownloadQueue();
        var completedPage = new DownloadQueueItem(
            Guid.NewGuid(),
            "https://anyhost.example.test/share/1",
            "share",
            DownloadQueueItemState.Completed,
            500,
            500,
            "C:\\old\\share",
            "share");

        queue.RestoreSnapshot(new[] { completedPage });
        Guid id = queue.Items[0].Id;

        Assert.True(queue.TryRequeueViaBrowser(id));

        DownloadQueueItem item = queue.Items[0];
        Assert.Equal(DownloadQueueItemState.Queued, item.State);
        Assert.True(item.PreferBrowser);
        Assert.Null(item.DestinationPath);
        Assert.Contains("browser", item.ProgressText);

        var restored = new DownloadQueue();
        restored.RestoreSnapshot(queue.Items);
        Assert.True(restored.Items[0].PreferBrowser);
    }

    [Fact]
    public async Task Orchestrator_PassesTheBrowserFlag()
    {
        var queue = new DownloadQueue();
        queue.Enqueue(LinkAnalyzer.Analyze("https://anyhost.example.test/share/1"));
        queue.TryRequeueViaBrowser(queue.Items[0].Id);

        bool? viaBrowser = null;
        var service = new Capture(request => viaBrowser = request.ViaBrowser);

        using var orchestrator = new DownloadOrchestrator(queue, service);
        await orchestrator.StartAllAsync(_directory, 1);

        Assert.True(viaBrowser);
    }

    private HttpDownloadTransferService Service(Action onRequest, out RecordingBrowser browser)
    {
        browser = new RecordingBrowser();

        return new HttpDownloadTransferService(
                new HttpClient(new Respond(request =>
                {
                    onRequest();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = request,
                        Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
                    };
                })))
            .WithBrowserDownloader(browser);
    }

    private sealed class RecordingBrowser : IBrowserDownloader
    {
        public BrowserDownloadMode? Mode { get; private set; }

        public async Task<BrowserDownloadResult> DownloadAsync(
            Uri page,
            string destinationDirectory,
            BrowserDownloadMode mode,
            IProgress<DownloadTransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            Mode = mode;
            string temp = Path.Combine(destinationDirectory, $".download-{Guid.NewGuid():N}.browser");
            byte[] mp4 = { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D };
            await File.WriteAllBytesAsync(temp, mp4, cancellationToken);
            return new BrowserDownloadResult(temp, "clip", mp4.Length, "video/mp4");
        }
    }

    private sealed class Capture : IDownloadTransferService
    {
        private readonly Action<DownloadTransferRequest> _seen;

        public Capture(Action<DownloadTransferRequest> seen)
        {
            _seen = seen;
        }

        public Task<DownloadTransferResult> DownloadAsync(
            DownloadTransferRequest request,
            IProgress<DownloadTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _seen(request);
            return Task.FromResult(new DownloadTransferResult(
                request.Source, Path.Combine(request.DestinationDirectory, "f"), "f", 1, 1));
        }
    }

    private sealed class Respond : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public Respond(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_respond(request));
    }
}
