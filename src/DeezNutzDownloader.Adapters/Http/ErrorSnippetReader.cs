using System.Text;

namespace DeezNutzDownloader.Adapters.Http;

/// <summary>
/// Reads just enough of an error page for challenge detection —
/// never a full body. Shared by the transfer service and crawler
/// so both surface typed challenges from 403/401 responses.
/// </summary>
internal static class ErrorSnippetReader
{
    internal const int MaximumSnippetChars = 32_768;

    internal static async Task<string> ReadSnippetAsync(
        HttpContent content,
        CancellationToken cancellationToken = default)
    {
        await using Stream stream =
            await content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        char[] buffer = new char[4_096];
        var builder = new StringBuilder();
        int total = 0;

        while (total < MaximumSnippetChars)
        {
            int read = await reader.ReadAsync(
                    buffer,
                    cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            int take = Math.Min(read, MaximumSnippetChars - total);
            builder.Append(buffer, 0, take);
            total += take;
        }

        return builder.ToString();
    }
}
