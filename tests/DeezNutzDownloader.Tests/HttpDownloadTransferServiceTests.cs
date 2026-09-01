using System.Net;
using System.Net.Http.Headers;
using DeezNutzDownloader.Adapters.Http;
using DeezNutzDownloader.Core.Downloads;

namespace DeezNutzDownloader.Tests;

public sealed class HttpDownloadTransferServiceTests
{
    [Fact]
    public async Task DownloadAsync_StreamsBodyAndReportsProgress()
    {
        byte[] payload = CreatePayload(200_000);
        string directory = CreateTemporaryDirectory();

        var handler = new StubHttpMessageHandler(
            (request, cancellationToken) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(
                    "https://example.test/files/archive.bin",
                    request.RequestUri?.AbsoluteUri);

                var content = new ByteArrayContent(payload);
                content.Headers.ContentLength =
                    payload.LongLength;

                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = content
                    });
            });

        using var client = new HttpClient(handler);
        var service =
            new HttpDownloadTransferService(client);

        var reports =
            new List<DownloadTransferProgress>();

        var progress =
            new InlineProgress<DownloadTransferProgress>(
                reports.Add);

        try
        {
            DownloadTransferResult result =
                await service.DownloadAsync(
                    new DownloadTransferRequest(
                        new Uri(
                            "https://example.test/files/archive.bin"),
                        directory),
                    progress);

            Assert.Equal("archive.bin", result.FileName);
            Assert.Equal(
                payload.LongLength,
                result.BytesWritten);

            Assert.Equal(
                payload,
                await File.ReadAllBytesAsync(
                    result.DestinationPath));

            Assert.NotEmpty(reports);
            Assert.Equal(
                payload.LongLength,
                reports[^1].BytesReceived);

            Assert.Equal<long?>(
                payload.LongLength,
                reports[^1].TotalBytes);

            Assert.Equal(
                100d,
                reports[^1].Percentage!.Value);

            Assert.Empty(
                Directory
                    .EnumerateFiles(directory)
                    .Where(
                        path => path.EndsWith(
                            ".part",
                            StringComparison.OrdinalIgnoreCase)));

            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DownloadAsync_SanitizesTraversalInDispositionName()
    {
        byte[] payload = CreatePayload(64);
        string directory = CreateTemporaryDirectory();

        var handler = new StubHttpMessageHandler(
            (request, cancellationToken) =>
            {
                var content = new ByteArrayContent(payload);

                content.Headers.ContentDisposition =
                    new ContentDispositionHeaderValue(
                        "attachment")
                    {
                        FileName =
                            "\"../../outside.txt\""
                    };

                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = content
                    });
            });

        using var client = new HttpClient(handler);
        var service =
            new HttpDownloadTransferService(client);

        try
        {
            DownloadTransferResult result =
                await service.DownloadAsync(
                    new DownloadTransferRequest(
                        new Uri(
                            "https://example.test/original.bin"),
                        directory));

            Assert.Equal("outside.txt", result.FileName);

            Assert.Equal(
                Path.GetFullPath(directory),
                Path.GetDirectoryName(
                    result.DestinationPath),
                ignoreCase: true);

            Assert.True(
                File.Exists(result.DestinationPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DownloadAsync_DecodesFilenameFromUrlPath()
    {
        string directory = CreateTemporaryDirectory();

        var handler = new StubHttpMessageHandler(
            (request, cancellationToken) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content =
                            new ByteArrayContent(
                                CreatePayload(32))
                    }));

        using var client = new HttpClient(handler);
        var service =
            new HttpDownloadTransferService(client);

        try
        {
            DownloadTransferResult result =
                await service.DownloadAsync(
                    new DownloadTransferRequest(
                        new Uri(
                            "https://example.test/files/" +
                            "second%20file.bin?token=secret"),
                        directory));

            Assert.Equal(
                "second file.bin",
                result.FileName);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DownloadAsync_DoesNotOverwriteExistingFiles()
    {
        byte[] payload = CreatePayload(48);
        string directory = CreateTemporaryDirectory();

        string existingPath = Path.Combine(
            directory,
            "archive.zip");

        await File.WriteAllTextAsync(
            existingPath,
            "existing content");

        var handler = new StubHttpMessageHandler(
            (request, cancellationToken) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content =
                            new ByteArrayContent(payload)
                    }));

        using var client = new HttpClient(handler);
        var service =
            new HttpDownloadTransferService(client);

        try
        {
            DownloadTransferRequest request = new(
                new Uri(
                    "https://example.test/files/archive.zip"),
                directory);

            DownloadTransferResult first =
                await service.DownloadAsync(request);

            DownloadTransferResult second =
                await service.DownloadAsync(request);

            Assert.Equal(
                "archive (1).zip",
                first.FileName);

            Assert.Equal(
                "archive (2).zip",
                second.FileName);

            Assert.Equal(
                "existing content",
                await File.ReadAllTextAsync(existingPath));

            Assert.Equal(
                payload,
                await File.ReadAllBytesAsync(
                    first.DestinationPath));

            Assert.Equal(
                payload,
                await File.ReadAllBytesAsync(
                    second.DestinationPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData("ftp://example.test/file.zip")]
    [InlineData("relative/file.zip")]
    public async Task DownloadAsync_RejectsNonHttpSourcesBeforeSending(
        string source)
    {
        string directory = CreateTemporaryDirectory();

        var handler = new StubHttpMessageHandler(
            (request, cancellationToken) =>
                throw new InvalidOperationException(
                    "The HTTP handler must not be called."));

        using var client = new HttpClient(handler);
        var service =
            new HttpDownloadTransferService(client);

        try
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => service.DownloadAsync(
                    new DownloadTransferRequest(
                        new Uri(
                            source,
                            UriKind.RelativeOrAbsolute),
                        directory)));

            Assert.Equal(0, handler.RequestCount);
            Assert.Empty(
                Directory.EnumerateFileSystemEntries(
                    directory));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DownloadAsync_HttpFailureLeavesNoFiles()
    {
        string directory = CreateTemporaryDirectory();

        var handler = new StubHttpMessageHandler(
            (request, cancellationToken) =>
                Task.FromResult(
                    new HttpResponseMessage(
                        HttpStatusCode.NotFound)
                    {
                        Content =
                            new StringContent("not found")
                    }));

        using var client = new HttpClient(handler);
        var service =
            new HttpDownloadTransferService(client);

        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(
                () => service.DownloadAsync(
                    new DownloadTransferRequest(
                        new Uri(
                            "https://example.test/missing.zip"),
                        directory)));

            Assert.Empty(
                Directory.EnumerateFileSystemEntries(
                    directory));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DownloadAsync_CancellationDeletesPartialFile()
    {
        byte[] payload = CreatePayload(300_000);
        string directory = CreateTemporaryDirectory();

        var handler = new StubHttpMessageHandler(
            (request, cancellationToken) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content =
                            new ByteArrayContent(payload)
                    }));

        using var client = new HttpClient(handler);
        var service =
            new HttpDownloadTransferService(client);

        using var cancellationSource =
            new CancellationTokenSource();

        bool sawTemporaryPartFile = false;

        var progress =
            new InlineProgress<DownloadTransferProgress>(
                report =>
                {
                    if (report.BytesReceived == 0)
                    {
                        return;
                    }

                    sawTemporaryPartFile =
                        Directory
                            .EnumerateFiles(directory)
                            .Any(
                                path => path.EndsWith(
                                    ".part",
                                    StringComparison.OrdinalIgnoreCase));

                    cancellationSource.Cancel();
                });

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.DownloadAsync(
                    new DownloadTransferRequest(
                        new Uri(
                            "https://example.test/large.bin"),
                        directory),
                    progress,
                    cancellationSource.Token));

            Assert.True(sawTemporaryPartFile);

            Assert.Empty(
                Directory.EnumerateFileSystemEntries(
                    directory));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DownloadAsync_BodyFailureDeletesPartialFile()
    {
        string directory = CreateTemporaryDirectory();

        var handler = new StubHttpMessageHandler(
            (request, cancellationToken) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content =
                            new StreamContent(
                                new FailingReadStream(
                                    CreatePayload(64)))
                    }));

        using var client = new HttpClient(handler);
        var service =
            new HttpDownloadTransferService(client);

        try
        {
            await Assert.ThrowsAsync<IOException>(
                () => service.DownloadAsync(
                    new DownloadTransferRequest(
                        new Uri(
                            "https://example.test/broken.bin"),
                        directory)));

            Assert.Empty(
                Directory.EnumerateFileSystemEntries(
                    directory));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];

        for (int index = 0; index < payload.Length; index++)
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

    private static void DeleteTemporaryDirectory(
        string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(
                directory,
                recursive: true);
        }
    }

    private sealed class StubHttpMessageHandler
        : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> _send;

        public StubHttpMessageHandler(
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

            return _send(
                request,
                cancellationToken);
        }
    }

    private sealed class InlineProgress<T>
        : IProgress<T>
    {
        private readonly Action<T> _report;

        public InlineProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value)
        {
            _report(value);
        }
    }

    private sealed class FailingReadStream
        : Stream
    {
        private readonly byte[] _firstChunk;
        private bool _firstChunkReturned;

        public FailingReadStream(byte[] firstChunk)
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
                throw new IOException(
                    "Simulated response-stream failure.");
            }

            _firstChunkReturned = true;

            int bytesToCopy = Math.Min(
                count,
                _firstChunk.Length);

            Array.Copy(
                _firstChunk,
                0,
                buffer,
                offset,
                bytesToCopy);

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
                    new IOException(
                        "Simulated response-stream failure."));
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

        public override long Seek(
            long offset,
            SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
        {
            throw new NotSupportedException();
        }
    }
}
