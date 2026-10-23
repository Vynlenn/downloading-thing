using System.Net;
using System.Net.Http.Headers;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.Tests;

public sealed class HttpThrottleResumeTests
{
    [Fact]
    public async Task DownloadAsync_429ThenSuccess_RetriesAndCompletes()
    {
        byte[] payload = CreatePayload(10_000);
        string directory = CreateTemporaryDirectory();
        var observedRanges = new List<string?>();

        int requests = 0;

        var handler = new StubHandler((request, _) =>
        {
            requests++;
            observedRanges.Add(
                request.Headers.Range?.ToString());

            if (requests == 1)
            {
                var throttled =
                    new HttpResponseMessage(
                        HttpStatusCode.TooManyRequests)
                    {
                        Content = new StringContent("slow down")
                    };

                throttled.Headers.TryAddWithoutValidation(
                    "Retry-After",
                    "0");

                return Task.FromResult(throttled);
            }

            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = payload.LongLength;

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                });
        });

        using var client = new HttpClient(handler);
        var service = new HttpDownloadTransferService(client);

        try
        {
            DownloadTransferResult result =
                await service.DownloadAsync(
                    new DownloadTransferRequest(
                        new Uri("https://pixeldrain.com/u/abc123"),
                        directory));

            Assert.Equal(payload.LongLength, result.BytesWritten);
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(
                payload,
                await File.ReadAllBytesAsync(result.DestinationPath));
            Assert.All(
                observedRanges,
                range => Assert.Null(range));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DownloadAsync_Persistent429_ThrowsThrottled()
    {
        string directory = CreateTemporaryDirectory();

        var handler = new StubHandler((_, _) =>
            Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("slow down")
                }));

        using var client = new HttpClient(handler);
        var service = new HttpDownloadTransferService(client);

        try
        {
            HostThrottledException exception =
                await Assert.ThrowsAsync<HostThrottledException>(
                    () => service.DownloadAsync(
                        new DownloadTransferRequest(
                            new Uri("https://gofile.io/d/xyz"),
                            directory)));

            Assert.Contains("429", exception.Message);
            Assert.True(handler.RequestCount >= 2);
            Assert.Empty(
                Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DownloadAsync_Pixeldrain403_ThrowsQuotaError()
    {
        string directory = CreateTemporaryDirectory();

        var handler = new StubHandler((_, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("blocked")
                }));

        using var client = new HttpClient(handler);
        var service = new HttpDownloadTransferService(client);

        try
        {
            HostThrottledException exception =
                await Assert.ThrowsAsync<HostThrottledException>(
                    () => service.DownloadAsync(
                        new DownloadTransferRequest(
                            new Uri("https://pixeldrain.com/u/quota"),
                            directory)));

            Assert.True(exception.IsQuotaExhausted);
            Assert.Contains(
                "quota",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DownloadAsync_MidStreamAbort_ResumesWithRange()
    {
        byte[] payload = CreatePayload(100_000);
        string directory = CreateTemporaryDirectory();
        var observedRanges = new List<string?>();

        int attempts = 0;

        var handler = new StubHandler((request, _) =>
        {
            attempts++;
            observedRanges.Add(request.Headers.Range?.ToString());

            const int firstChunkBytes = 10_000;

            if (attempts == 1)
            {
                // First attempt dies mid-body after 10 KB.
                // StreamContent streams (no buffering), so the
                // failure surfaces inside the read loop.
                var content = new StreamContent(
                    new FirstChunkThenFailStream(
                        payload[..firstChunkBytes]));

                content.Headers.ContentLength =
                    payload.LongLength;

                content.Headers.ContentType =
                    new MediaTypeHeaderValue(
                        "application/octet-stream");

                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = content
                    });
            }

            const int offset = 10_000;
            byte[] remainder = payload[offset..];

            var resumed = new ByteArrayContent(remainder);
            resumed.Headers.ContentLength = remainder.LongLength;
            resumed.Headers.ContentRange =
                new ContentRangeHeaderValue(offset, payload.LongLength - 1, payload.LongLength);

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = resumed
                });
        });

        using var client = new HttpClient(handler);
        var service = new HttpDownloadTransferService(client);

        try
        {
            DownloadTransferResult result =
                await service.DownloadAsync(
                    new DownloadTransferRequest(
                        new Uri("https://pixeldrain.com/u/bigfile"),
                        directory));

            Assert.Equal(payload.LongLength, result.BytesWritten);
            Assert.Equal(
                payload,
                await File.ReadAllBytesAsync(result.DestinationPath));
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(
                "bytes=10000-",
                observedRanges[1]);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];

        for (int index = 0; index < length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        return payload;
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "DeezNutzDownloader.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best effort test cleanup.
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> _send;

        public StubHandler(
            Func<
                HttpRequestMessage,
                CancellationToken,
                Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return _send(request, cancellationToken);
        }
    }

    private sealed class FirstChunkThenFailStream : Stream
    {
        private readonly byte[] _firstChunk;
        private bool _firstChunkReturned;

        public FirstChunkThenFailStream(byte[] firstChunk)
        {
            _firstChunk = firstChunk;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length =>
            throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            if (_firstChunkReturned)
            {
                throw new IOException("Simulated connection drop.");
            }

            _firstChunkReturned = true;

            int bytesToCopy = Math.Min(count, _firstChunk.Length);
            Array.Copy(_firstChunk, 0, buffer, offset, bytesToCopy);
            return bytesToCopy;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_firstChunkReturned)
            {
                return ValueTask.FromException<int>(
                    new IOException("Simulated connection drop."));
            }

            _firstChunkReturned = true;

            int bytesToCopy = Math.Min(
                buffer.Length,
                _firstChunk.Length);

            _firstChunk
                .AsMemory(0, bytesToCopy)
                .CopyTo(buffer);

            return ValueTask.FromResult(bytesToCopy);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();
    }
}
