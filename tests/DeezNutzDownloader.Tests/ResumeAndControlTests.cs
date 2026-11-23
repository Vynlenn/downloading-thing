using System.Net;
using System.Net.Http.Headers;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Downloads;
using DeezNutzDownloader.Core.Links;

namespace DeezNutzDownloader.Tests;

/// <summary>
/// Partial downloads survive cancel/pause/restart and resume
/// safely; the live batch honors per-item pause, additions and
/// queue order.
/// </summary>
public sealed class ResumeAndControlTests : IDisposable
{
    private const int FourMegabytes = 4 * 1024 * 1024;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dnd-resume-tests",
        Guid.NewGuid().ToString("N"));

    public ResumeAndControlTests()
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

    // ---------- Transfer resume ----------

    [Fact]
    public async Task CanceledSingleStream_ResumesFromSavedBytes()
    {
        var server = new ResumeServer(FourMegabytes, advertiseRanges: false);
        string key = PartialDownloadFiles.KeyFor(Guid.NewGuid());

        await CancelPartwayAsync(server, key, connections: 1);

        PartialDownloadState? saved = PartialDownloadFiles.TryLoad(_directory, key);
        Assert.NotNull(saved);
        Assert.True(saved.ContiguousBytes > 0);
        Assert.True(saved.ContiguousBytes < FourMegabytes);

        server.SlowDelivery = false;
        DownloadTransferResult result = await DownloadAsync(server, key, connections: 1);

        AssertMatches(server.Payload, result);
        Assert.Equal(saved.ContiguousBytes, server.LastRangeStart);
        Assert.True(server.SawIfRange);
        Assert.Null(PartialDownloadFiles.TryLoad(_directory, key));
        Assert.False(File.Exists(PartialDownloadFiles.PartPath(_directory, key)));
    }

    [Fact]
    public async Task FileChangedBetweenSessions_RestartsInsteadOfSplicing()
    {
        var server = new ResumeServer(FourMegabytes, advertiseRanges: false);
        string key = PartialDownloadFiles.KeyFor(Guid.NewGuid());

        await CancelPartwayAsync(server, key, connections: 1);

        server.ReplaceContent(seed: 99);
        server.SlowDelivery = false;

        DownloadTransferResult result = await DownloadAsync(server, key, connections: 1);

        AssertMatches(server.Payload, result);
    }

    [Fact]
    public async Task CanceledSegmentedDownload_ResumesOnlyMissingRanges()
    {
        var server = new ResumeServer(FourMegabytes * 2, advertiseRanges: true);
        string key = PartialDownloadFiles.KeyFor(Guid.NewGuid());

        await CancelPartwayAsync(server, key, connections: 4);

        PartialDownloadState? saved = PartialDownloadFiles.TryLoad(_directory, key);
        Assert.NotNull(saved);
        Assert.NotNull(saved.Segments);
        long alreadySaved = saved.ConfirmedBytes;
        Assert.True(alreadySaved > 0);

        server.SlowDelivery = false;
        server.ResetCounters();

        DownloadTransferResult result = await DownloadAsync(server, key, connections: 4);

        AssertMatches(server.Payload, result);
        Assert.Equal(0, server.FullBodyRequests);

        // Only what was missing came over the wire again (plus the
        // tail of any range a worker was mid-way through).
        Assert.True(
            server.BytesServed <= server.Payload.Length - alreadySaved + 4 * 256 * 1024,
            $"Re-downloaded {server.BytesServed} bytes; {alreadySaved} were already saved.");
    }

    [Fact]
    public async Task DifferentLinkWithSameKey_NeverReusesBytes()
    {
        var first = new ResumeServer(FourMegabytes, advertiseRanges: false);
        string key = PartialDownloadFiles.KeyFor(Guid.NewGuid());

        await CancelPartwayAsync(first, key, connections: 1);

        var second = new ResumeServer(FourMegabytes, advertiseRanges: false, seed: 5);

        DownloadTransferResult result = await DownloadAsync(
            second,
            key,
            connections: 1,
            url: "https://cdn.example.test/other.bin");

        AssertMatches(second.Payload, result);
        Assert.Null(second.LastRangeStart);
    }

    [Fact]
    public void PartialFiles_RejectKeysThatCouldFormPaths()
    {
        Assert.False(PartialDownloadFiles.IsValidKey("..\\..\\evil"));
        Assert.False(PartialDownloadFiles.IsValidKey(null));
        Assert.True(PartialDownloadFiles.IsValidKey(PartialDownloadFiles.KeyFor(Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => PartialDownloadFiles.PartPath(_directory, "../x"));
    }

    // ---------- Live batch control ----------

    [Fact]
    public async Task PauseItem_StopsItKeepsItPausedAndResumeFinishesInSameBatch()
    {
        DownloadQueue queue = CreateQueue("https://example.test/big.bin");
        Guid id = queue.Items[0].Id;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;

        var service = new FuncService(async (request, cancellationToken) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Result(request);
        });

        // A second, slow item keeps the batch alive while paused.
        queue.Enqueue(LinkAnalyzer.Analyze("https://example.test/keepalive.bin"));
        Guid keepAlive = queue.Items[1].Id;
        var releaseKeepAlive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var gatedService = new FuncService(async (request, token) =>
        {
            if (request.Source.AbsolutePath.EndsWith("keepalive.bin", StringComparison.Ordinal))
            {
                await releaseKeepAlive.Task.WaitAsync(token);
                return Result(request);
            }

            return await service.DownloadAsync(request, null, token);
        });

        using var liveOrchestrator = new DownloadOrchestrator(queue, gatedService);

        Task<DownloadBatchResult> batch =
            liveOrchestrator.StartAllAsync(_directory, maximumConcurrency: 2);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(liveOrchestrator.PauseItem(id));
        await WaitForAsync(() => queue.Items.First(item => item.Id == id).State == DownloadQueueItemState.Paused);

        Assert.True(queue.TryResume(id));
        Assert.True(liveOrchestrator.TryAddToBatch(id));

        await WaitForAsync(() => queue.Items.First(item => item.Id == id).State == DownloadQueueItemState.Completed);

        releaseKeepAlive.TrySetResult();
        DownloadBatchResult result = await batch.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, attempts);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(0, result.CanceledCount);
    }

    [Fact]
    public async Task CancelItem_CancelsOnlyThatItem()
    {
        DownloadQueue queue = CreateQueue(
            "https://example.test/one.bin",
            "https://example.test/two.bin");

        Guid first = queue.Items[0].Id;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = new FuncService(async (request, token) =>
        {
            if (request.Source.AbsolutePath.EndsWith("one.bin", StringComparison.Ordinal))
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return Result(request);
        });

        using var orchestrator = new DownloadOrchestrator(queue, service);
        Task<DownloadBatchResult> batch = orchestrator.StartAllAsync(_directory, 2);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(orchestrator.CancelItem(first));

        DownloadBatchResult result = await batch.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, result.CompletedCount);
        Assert.Equal(1, result.CanceledCount);
        Assert.Equal(DownloadQueueItemState.Canceled, queue.Items[0].State);
        Assert.Equal(DownloadQueueItemState.Completed, queue.Items[1].State);
    }

    [Fact]
    public async Task ItemsAddedDuringBatch_DownloadInThatBatch()
    {
        DownloadQueue queue = CreateQueue("https://example.test/first.bin");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = new FuncService(async (request, token) =>
        {
            if (request.Source.AbsolutePath.EndsWith("first.bin", StringComparison.Ordinal))
            {
                await release.Task.WaitAsync(token);
            }

            return Result(request);
        });

        using var orchestrator = new DownloadOrchestrator(queue, service);
        Task<DownloadBatchResult> batch = orchestrator.StartAllAsync(_directory, 2);

        await WaitForAsync(() => queue.Items[0].State == DownloadQueueItemState.Downloading);

        queue.Enqueue(LinkAnalyzer.Analyze("https://example.test/late.bin"));
        Assert.True(orchestrator.TryAddToBatch(queue.Items[1].Id));

        await WaitForAsync(() => queue.Items[1].State == DownloadQueueItemState.Completed);
        release.TrySetResult();

        DownloadBatchResult result = await batch.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(2, result.SelectedCount);
    }

    [Fact]
    public async Task MoveToFront_ChangesWhatDownloadsNext()
    {
        DownloadQueue queue = CreateQueue(
            "https://example.test/a.bin",
            "https://example.test/b.bin",
            "https://example.test/c.bin");

        var order = new List<string>();

        var service = new FuncService((request, _) =>
        {
            lock (order)
            {
                order.Add(Path.GetFileName(request.Source.AbsolutePath));
            }

            return Task.FromResult(Result(request));
        });

        Assert.True(queue.MoveToFront(queue.Items[2].Id));

        using var orchestrator = new DownloadOrchestrator(queue, service);
        await orchestrator.StartAllAsync(_directory, maximumConcurrency: 1);

        Assert.Equal(new[] { "c.bin", "a.bin", "b.bin" }, order);
    }

    [Fact]
    public async Task OrchestratorPassesItemIdAsResumeKey()
    {
        DownloadQueue queue = CreateQueue("https://example.test/a.bin");
        string? key = null;

        var service = new FuncService((request, _) =>
        {
            key = request.ResumeKey;
            return Task.FromResult(Result(request));
        });

        using var orchestrator = new DownloadOrchestrator(queue, service);
        await orchestrator.StartAllAsync(_directory, 1);

        Assert.Equal(PartialDownloadFiles.KeyFor(queue.Items[0].Id), key);
    }

    // ---------- Queue model ----------

    [Fact]
    public void Queue_PauseMoveAndLinkStatus()
    {
        DownloadQueue queue = CreateQueue(
            "https://example.test/a.bin",
            "https://example.test/b.bin");

        Guid a = queue.Items[0].Id;
        Guid b = queue.Items[1].Id;

        Assert.True(queue.TryPause(a));
        Assert.Equal(DownloadQueueItemState.Paused, queue.Items[0].State);
        Assert.True(queue.Items[0].CanResume);
        Assert.Contains("Paused", queue.Items[0].ProgressText);

        Assert.True(queue.Move(b, -1));
        Assert.Equal(b, queue.Items[0].Id);
        Assert.False(queue.Move(b, -1));
        Assert.True(queue.MoveToBack(b));
        Assert.Equal(b, queue.Items[1].Id);

        Assert.True(queue.SetLinkStatus(b, LinkCheckStatus.Offline, "Removed by the host.", "real-name.bin", 42));
        DownloadQueueItem checkedItem = queue.Items[1];
        Assert.Equal("Offline", checkedItem.LinkStatusText);
        Assert.Equal("real-name.bin", checkedItem.DisplayName);
        Assert.Equal<long?>(42, checkedItem.TotalBytes);

        Assert.True(queue.TryResume(a));
        Assert.Equal(DownloadQueueItemState.Queued, queue.Items[0].State);
    }

    [Fact]
    public void ClearCompleted_KeepsPausedItems()
    {
        DownloadQueue queue = CreateQueue("https://example.test/a.bin");
        queue.TryPause(queue.Items[0].Id);

        Assert.Equal(0, queue.ClearCompleted());
        Assert.Single(queue.Items);
    }

    // ---------- helpers ----------

    private async Task CancelPartwayAsync(ResumeServer server, string key, int connections)
    {
        server.SlowDelivery = true;
        using var cancel = new CancellationTokenSource();

        var progress = new SyncProgress(report =>
        {
            if (report.BytesReceived >= server.Payload.Length / 3)
            {
                cancel.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DownloadAsync(server, key, connections, progress: progress, token: cancel.Token));

        Assert.True(File.Exists(PartialDownloadFiles.PartPath(_directory, key)));
    }

    private async Task<DownloadTransferResult> DownloadAsync(
        ResumeServer server,
        string key,
        int connections,
        string url = "https://cdn.example.test/big.bin",
        IProgress<DownloadTransferProgress>? progress = null,
        CancellationToken token = default)
    {
        using var client = new HttpClient(server, disposeHandler: false);
        var service = new HttpDownloadTransferService(
            client,
            resolvers: null,
            megaDownloader: null,
            stallTimeout: TimeSpan.FromSeconds(10));

        return await service.DownloadAsync(
            new DownloadTransferRequest(
                new Uri(url),
                _directory,
                null,
                connections,
                key),
            progress,
            token);
    }

    private static void AssertMatches(byte[] expected, DownloadTransferResult result)
    {
        byte[] actual = File.ReadAllBytes(result.DestinationPath);
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(expected.AsSpan().SequenceEqual(actual), "Downloaded bytes differ.");
    }

    private static DownloadQueue CreateQueue(params string[] urls)
    {
        var queue = new DownloadQueue();
        queue.Enqueue(LinkAnalyzer.Analyze(string.Join(Environment.NewLine, urls)));
        Assert.Equal(urls.Length, queue.Count);
        return queue;
    }

    private static DownloadTransferResult Result(DownloadTransferRequest request)
    {
        string name = Path.GetFileName(request.Source.AbsolutePath);
        return new DownloadTransferResult(
            request.Source,
            Path.Combine(request.DestinationDirectory, name),
            name,
            1,
            1);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    private sealed class FuncService : IDownloadTransferService
    {
        private readonly Func<DownloadTransferRequest, CancellationToken, Task<DownloadTransferResult>> _run;

        public FuncService(Func<DownloadTransferRequest, CancellationToken, Task<DownloadTransferResult>> run)
        {
            _run = run;
        }

        public Task<DownloadTransferResult> DownloadAsync(
            DownloadTransferRequest request,
            IProgress<DownloadTransferProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            _run(request, cancellationToken);
    }

    private sealed class SyncProgress : IProgress<DownloadTransferProgress>
    {
        private readonly Action<DownloadTransferProgress> _report;

        public SyncProgress(Action<DownloadTransferProgress> report)
        {
            _report = report;
        }

        public void Report(DownloadTransferProgress value) => _report(value);
    }

    /// <summary>In-memory server with Range, If-Range and ETag support.</summary>
    private sealed class ResumeServer : HttpMessageHandler
    {
        private byte[] _payload;
        private string _etag;
        private long _bytesServed;
        private int _fullBodyRequests;

        public ResumeServer(int size, bool advertiseRanges, int seed = 1)
        {
            _payload = new byte[size];
            new Random(seed).NextBytes(_payload);
            _etag = $"\"v{seed}\"";
            AdvertiseRanges = advertiseRanges;
        }

        public byte[] Payload => Volatile.Read(ref _payload);

        public bool AdvertiseRanges { get; }

        public bool SlowDelivery { get; set; }

        public bool SawIfRange { get; private set; }

        public long? LastRangeStart { get; private set; }

        public long BytesServed => Interlocked.Read(ref _bytesServed);

        public int FullBodyRequests => Volatile.Read(ref _fullBodyRequests);

        public void ReplaceContent(int seed)
        {
            byte[] changed = new byte[_payload.Length];
            new Random(seed).NextBytes(changed);
            Volatile.Write(ref _payload, changed);
            _etag = $"\"v{seed}\"";
        }

        public void ResetCounters()
        {
            Interlocked.Exchange(ref _bytesServed, 0);
            Interlocked.Exchange(ref _fullBodyRequests, 0);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[] payload = Payload;
            RangeItemHeaderValue? range = request.Headers.Range?.Ranges.FirstOrDefault();

            if (request.Headers.IfRange is not null)
            {
                SawIfRange = true;

                if (request.Headers.IfRange.EntityTag?.Tag != _etag)
                {
                    range = null;
                }
            }

            if (range?.From is long from)
            {
                LastRangeStart = from;

                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    RequestMessage = request,
                    Content = new StreamContent(Body(payload, (int)from))
                };

                partial.Headers.ETag = new EntityTagHeaderValue(_etag);
                partial.Content.Headers.ContentLength = payload.Length - from;
                partial.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(from, payload.Length - 1, payload.Length);
                return Task.FromResult(partial);
            }

            Interlocked.Increment(ref _fullBodyRequests);

            var full = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(Body(payload, 0))
            };

            full.Headers.ETag = new EntityTagHeaderValue(_etag);
            full.Content.Headers.ContentLength = payload.Length;

            if (AdvertiseRanges)
            {
                full.Headers.AcceptRanges.Add("bytes");
            }

            return Task.FromResult(full);
        }

        private Stream Body(byte[] payload, int from) =>
            new CountingStream(payload, from, this);

        private sealed class CountingStream : Stream
        {
            private readonly byte[] _data;
            private readonly ResumeServer _owner;
            private int _position;

            public CountingStream(byte[] data, int from, ResumeServer owner)
            {
                _data = data;
                _position = from;
                _owner = owner;
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
                if (_owner.SlowDelivery)
                {
                    await Task.Delay(5, cancellationToken);
                }

                int count = Math.Min(
                    Math.Min(buffer.Length, 32 * 1024),
                    _data.Length - _position);

                if (count <= 0)
                {
                    return 0;
                }

                _data.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                Interlocked.Add(ref _owner._bytesServed, count);
                return count;
            }

            public override Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
