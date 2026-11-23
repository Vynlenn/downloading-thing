using System.Net;
using System.Net.Http.Headers;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.Tests;

/// <summary>
/// Multi-connection downloads against an in-memory range server:
/// byte-exact output, real parallelism, and every fallback path.
/// </summary>
public sealed class SegmentedDownloadTests : IDisposable
{
    private const int TenMegabytes = 10 * 1024 * 1024;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dnd-segmented-tests",
        Guid.NewGuid().ToString("N"));

    public SegmentedDownloadTests()
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
    [InlineData(1L * 1024 * 1024, 8, 1)]       // below 4 MB: single
    [InlineData(10L * 1024 * 1024, 1, 1)]      // feature off
    [InlineData(10L * 1024 * 1024, 4, 4)]
    [InlineData(5L * 1024 * 1024, 16, 5)]      // >= 1 MB per segment
    [InlineData(10L * 1024 * 1024 * 1024, 64, 16)] // hard cap
    public void PlanConnectionCount_RespectsSizeAndCaps(
        long total,
        int requested,
        int expected)
    {
        Assert.Equal(
            expected,
            SegmentedDownloader.PlanConnectionCount(total, requested));
    }

    [Fact]
    public async Task LargeFile_DownloadsOverParallelConnections()
    {
        var server = new RangeServer(TenMegabytes)
        {
            // Hold every range body until 4 range requests are open
            // at once: this only completes if they run in parallel.
            ParallelGate = 4
        };

        DownloadTransferResult result =
            await DownloadAsync(server, connections: 4);

        await AssertFileMatchesAsync(server, result);
        Assert.True(server.ParallelGateReached);
        Assert.Equal(1, server.FullRequests);
        Assert.Equal(TenMegabytes, result.BytesWritten);
        Assert.True(server.RangeRequests >= 4);
    }

    [Fact]
    public async Task UnevenConnections_FinishByStealingWork()
    {
        // The first range connection crawls; the others must split
        // its range instead of waiting on it.
        var server = new RangeServer(TenMegabytes)
        {
            SlowFirstRange = true
        };

        DownloadTransferResult result =
            await DownloadAsync(server, connections: 4)
                .WaitAsync(TimeSpan.FromSeconds(20));

        await AssertFileMatchesAsync(server, result);
        Assert.Equal(1, server.FullRequests);
        Assert.True(
            server.RangeRequests > 4,
            $"Expected work stealing to open extra ranges; saw {server.RangeRequests}.");
    }

    [Fact]
    public async Task NoAcceptRanges_UsesOneStream()
    {
        var server = new RangeServer(TenMegabytes)
        {
            AdvertiseRanges = false
        };

        DownloadTransferResult result =
            await DownloadAsync(server, connections: 8);

        await AssertFileMatchesAsync(server, result);
        Assert.Equal(1, server.TotalRequests);
    }

    [Fact]
    public async Task ServerIgnoringRanges_FallsBackToSingleStream()
    {
        var server = new RangeServer(TenMegabytes)
        {
            HonorRanges = false
        };

        DownloadTransferResult result =
            await DownloadAsync(server, connections: 4);

        await AssertFileMatchesAsync(server, result);
    }

    [Fact]
    public async Task FileChangedMidDownload_IsNeverSpliced()
    {
        // After the probe the server swaps content and ETag; If-Range
        // makes it answer 200 with the new file, which must win whole.
        var server = new RangeServer(TenMegabytes)
        {
            ChangeAfterProbe = true
        };

        DownloadTransferResult result =
            await DownloadAsync(server, connections: 4);

        await AssertFileMatchesAsync(server, result);
        Assert.True(server.SawIfRange);
    }

    [Fact]
    public async Task RefusedExtraConnections_OthersAbsorbTheirRanges()
    {
        var server = new RangeServer(TenMegabytes)
        {
            // Like a host that allows two connections per client:
            // a third concurrent range gets 503.
            MaxConcurrentRanges = 2
        };

        DownloadTransferResult result =
            await DownloadAsync(server, connections: 4)
                .WaitAsync(TimeSpan.FromSeconds(20));

        await AssertFileMatchesAsync(server, result);
        Assert.Equal(1, server.FullRequests);
        Assert.True(server.RefusedRanges > 0);
    }

    [Fact]
    public async Task DroppedRangeConnection_ResumesThatRange()
    {
        var server = new RangeServer(TenMegabytes)
        {
            DropFirstRangeAfterBytes = 300_000
        };

        DownloadTransferResult result =
            await DownloadAsync(server, connections: 4)
                .WaitAsync(TimeSpan.FromSeconds(20));

        await AssertFileMatchesAsync(server, result);
        Assert.Equal(1, server.FullRequests);
    }

    [Fact]
    public async Task SingleLaneHost_NeverSegments()
    {
        var server = new RangeServer(TenMegabytes);

        DownloadTransferResult result = await DownloadAsync(
            server,
            connections: 8,
            url: "https://pixeldrain.com/api/file/abcd1234");

        await AssertFileMatchesAsync(server, result);
        Assert.Equal(0, server.RangeRequests);
    }

    [Fact]
    public async Task SpeedCap_DisablesSegmenting()
    {
        var server = new RangeServer(5 * 1024 * 1024);

        DownloadTransferResult result = await DownloadAsync(
            server,
            connections: 8,
            maxBytesPerSecond: 1_000_000_000);

        await AssertFileMatchesAsync(server, result);
        Assert.Equal(0, server.RangeRequests);
    }

    [Fact]
    public async Task Progress_EndsAtTotalAndNeverGoesBackwards()
    {
        var server = new RangeServer(TenMegabytes);
        var reports = new List<long>();

        await DownloadAsync(
            server,
            connections: 4,
            progress: new SyncProgress(report =>
            {
                lock (reports)
                {
                    reports.Add(report.BytesReceived);
                }
            }));

        Assert.Equal(TenMegabytes, reports[^1]);

        for (int index = 1; index < reports.Count; index++)
        {
            Assert.True(reports[index] >= reports[index - 1]);
        }
    }

    /// <summary>
    /// Span comparison: xUnit's element-wise Assert.Equal takes
    /// seconds on multi-megabyte arrays.
    /// </summary>
    private static async Task AssertFileMatchesAsync(
        RangeServer server,
        DownloadTransferResult result)
    {
        byte[] actual = await File.ReadAllBytesAsync(result.DestinationPath);

        Assert.Equal(server.Payload.Length, actual.Length);
        Assert.True(
            server.Payload.AsSpan().SequenceEqual(actual),
            "Downloaded bytes differ from the served file.");
    }

    private async Task<DownloadTransferResult> DownloadAsync(
        RangeServer server,
        int connections,
        string url = "https://cdn.example.test/files/big.bin",
        long? maxBytesPerSecond = null,
        IProgress<DownloadTransferProgress>? progress = null)
    {
        using var client = new HttpClient(server);
        var service = new HttpDownloadTransferService(
            client,
            resolvers: null,
            megaDownloader: null,
            stallTimeout: TimeSpan.FromSeconds(5));

        return await service.DownloadAsync(
            new DownloadTransferRequest(
                new Uri(url),
                _directory,
                maxBytesPerSecond,
                connections),
            progress);
    }

    private sealed class SyncProgress : IProgress<DownloadTransferProgress>
    {
        private readonly Action<DownloadTransferProgress> _report;

        public SyncProgress(Action<DownloadTransferProgress> report)
        {
            _report = report;
        }

        public void Report(DownloadTransferProgress value)
        {
            _report(value);
        }
    }

    /// <summary>In-memory HTTP server with RFC 7233 range support.</summary>
    private sealed class RangeServer : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private byte[] _payload;
        private string _etag = "\"v1\"";
        private int _totalRequests;
        private int _rangeRequests;
        private int _openRanges;
        private int _dropped;
        private int _activeRanges;
        private int _fullRequests;
        private int _refusedRanges;

        public RangeServer(int size)
        {
            _payload = new byte[size];
            new Random(size).NextBytes(_payload);
        }

        public byte[] Payload => Volatile.Read(ref _payload);

        public bool AdvertiseRanges { get; init; } = true;

        public bool HonorRanges { get; init; } = true;

        public bool ChangeAfterProbe { get; init; }

        public int ParallelGate { get; init; }

        public bool SlowFirstRange { get; init; }

        public int MaxConcurrentRanges { get; init; } = int.MaxValue;

        public int DropFirstRangeAfterBytes { get; init; }

        public bool ParallelGateReached => _gate.Task.IsCompleted;

        public bool SawIfRange { get; private set; }

        public int TotalRequests => Volatile.Read(ref _totalRequests);

        public int RangeRequests => Volatile.Read(ref _rangeRequests);

        public int FullRequests => Volatile.Read(ref _fullRequests);

        public int RefusedRanges => Volatile.Read(ref _refusedRanges);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int requestNumber = Interlocked.Increment(ref _totalRequests);
            RangeItemHeaderValue? range =
                request.Headers.Range?.Ranges.FirstOrDefault();

            if (range is null)
            {
                HttpResponseMessage full = Full(request);

                if (requestNumber == 1 && ChangeAfterProbe)
                {
                    byte[] changed = new byte[_payload.Length];
                    new Random(7).NextBytes(changed);
                    Volatile.Write(ref _payload, changed);
                    _etag = "\"v2\"";
                }

                return Task.FromResult(full);
            }

            int rangeNumber = Interlocked.Increment(ref _rangeRequests);

            if (request.Headers.IfRange is not null)
            {
                SawIfRange = true;

                if (request.Headers.IfRange.EntityTag?.Tag != _etag)
                {
                    return Task.FromResult(Full(request));
                }
            }

            if (!HonorRanges)
            {
                return Task.FromResult(Full(request));
            }


            if (Interlocked.Increment(ref _activeRanges) > MaxConcurrentRanges)
            {
                Interlocked.Decrement(ref _activeRanges);
                Interlocked.Increment(ref _refusedRanges);

                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        RequestMessage = request,
                        Content = new ByteArrayContent(Array.Empty<byte>())
                    });
            }

            byte[] payload = Payload;
            long from = range.From!.Value;
            byte[] slice = payload[(int)from..];

            Stream body = new ReleasingStream(
                new MemoryStream(slice, writable: false),
                () => Interlocked.Decrement(ref _activeRanges));

            if (ParallelGate > 0)
            {
                if (Interlocked.Increment(ref _openRanges) >= ParallelGate)
                {
                    _gate.TrySetResult();
                }

                body = new GatedStream(body, _gate.Task);
            }

            if (SlowFirstRange && rangeNumber == 1)
            {
                body = new SlowStream(body);
            }

            if (
                DropFirstRangeAfterBytes > 0 &&
                Interlocked.CompareExchange(ref _dropped, 1, 0) == 0)
            {
                body = new DroppingStream(body, DropFirstRangeAfterBytes);
            }

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new StreamContent(body)
            };

            response.Headers.ETag = new EntityTagHeaderValue(_etag);
            response.Content.Headers.ContentLength = slice.Length;
            response.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(
                    from,
                    payload.Length - 1,
                    payload.Length);

            return Task.FromResult(response);
        }

        private HttpResponseMessage Full(HttpRequestMessage request)
        {
            Interlocked.Increment(ref _fullRequests);

            byte[] payload = Payload;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload)
            };

            response.Content.Headers.ContentLength = payload.Length;
            response.Headers.ETag = new EntityTagHeaderValue(_etag);

            if (AdvertiseRanges)
            {
                response.Headers.AcceptRanges.Add("bytes");
            }

            return response;
        }
    }

    private abstract class WrappingStream : Stream
    {
        protected WrappingStream(Stream inner)
        {
            Inner = inner;
        }

        protected Stream Inner { get; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Runs a callback once when the response body closes.</summary>
    private sealed class ReleasingStream : WrappingStream
    {
        private Action? _onClose;

        public ReleasingStream(Stream inner, Action onClose)
            : base(inner)
        {
            _onClose = onClose;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Inner.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            Interlocked.Exchange(ref _onClose, null)?.Invoke();
            base.Dispose(disposing);
        }
    }

    private sealed class GatedStream : WrappingStream
    {
        private readonly Task _gate;

        public GatedStream(Stream inner, Task gate)
            : base(inner)
        {
            _gate = gate;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return await Inner.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class SlowStream : WrappingStream
    {
        public SlowStream(Stream inner)
            : base(inner)
        {
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            return await Inner.ReadAsync(buffer[..Math.Min(buffer.Length, 16 * 1024)], cancellationToken);
        }
    }

    private sealed class DroppingStream : WrappingStream
    {
        private int _remaining;

        public DroppingStream(Stream inner, int dropAfter)
            : base(inner)
        {
            _remaining = dropAfter;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                throw new IOException("Connection reset by peer.");
            }

            int read = await Inner.ReadAsync(
                buffer[..Math.Min(buffer.Length, _remaining)],
                cancellationToken);

            _remaining -= read;
            return read;
        }
    }
}
