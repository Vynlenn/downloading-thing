namespace DeezNutzDownloader.Core.Links;

public enum LinkAnalysisStatus
{
    Valid,
    Duplicate,
    Invalid
}

public sealed record LinkAnalysisItem(
    int LineNumber,
    string OriginalText,
    string? NormalizedUrl,
    LinkAnalysisStatus Status,
    string Detail)
{
    public string LineLabel => $"Line {LineNumber}";
}

public sealed class LinkAnalysisResult
{
    internal LinkAnalysisResult(
        IReadOnlyList<LinkAnalysisItem> items)
    {
        Items = Array.AsReadOnly(items.ToArray());

        ValidCount = Items.Count(
            item => item.Status == LinkAnalysisStatus.Valid);

        DuplicateCount = Items.Count(
            item => item.Status == LinkAnalysisStatus.Duplicate);

        InvalidCount = Items.Count(
            item => item.Status == LinkAnalysisStatus.Invalid);
    }

    public IReadOnlyList<LinkAnalysisItem> Items { get; }

    public int TotalCount => Items.Count;

    public int ValidCount { get; }

    public int DuplicateCount { get; }

    public int InvalidCount { get; }
}

public static class LinkAnalyzer
{
    public static LinkAnalysisResult Analyze(string? input)
    {
        var items = new List<LinkAnalysisItem>();

        if (string.IsNullOrEmpty(input))
        {
            return new LinkAnalysisResult(items);
        }

        string normalizedInput = input
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        string[] lines = normalizedInput.Split('\n');

        var firstLineByUrl = new Dictionary<string, int>(
            StringComparer.Ordinal);

        for (int index = 0; index < lines.Length; index++)
        {
            int lineNumber = index + 1;
            string candidate = lines[index].Trim();

            if (candidate.Length == 0)
            {
                continue;
            }

            if (!TryNormalizeHttpUrl(
                    candidate,
                    out string normalizedUrl,
                    out string validationMessage))
            {
                items.Add(
                    new LinkAnalysisItem(
                        lineNumber,
                        candidate,
                        null,
                        LinkAnalysisStatus.Invalid,
                        validationMessage));

                continue;
            }

            if (firstLineByUrl.TryGetValue(
                    normalizedUrl,
                    out int firstLineNumber))
            {
                items.Add(
                    new LinkAnalysisItem(
                        lineNumber,
                        candidate,
                        normalizedUrl,
                        LinkAnalysisStatus.Duplicate,
                        $"Duplicate of line {firstLineNumber}. " +
                        $"Normalized: {normalizedUrl}"));

                continue;
            }

            firstLineByUrl.Add(normalizedUrl, lineNumber);

            string detail = string.Equals(
                candidate,
                normalizedUrl,
                StringComparison.Ordinal)
                    ? "Valid HTTP(S) URL. Ready to queue."
                    : $"Normalized: {normalizedUrl}";

            items.Add(
                new LinkAnalysisItem(
                    lineNumber,
                    candidate,
                    normalizedUrl,
                    LinkAnalysisStatus.Valid,
                    detail));
        }

        return new LinkAnalysisResult(items);
    }

    private static bool TryNormalizeHttpUrl(
        string candidate,
        out string normalizedUrl,
        out string validationMessage)
    {
        normalizedUrl = string.Empty;

        if (candidate.Any(character => char.IsWhiteSpace(character)))
        {
            validationMessage =
                "The URL contains unescaped whitespace.";

            return false;
        }

        if (!Uri.TryCreate(
                candidate,
                UriKind.Absolute,
                out Uri? uri))
        {
            validationMessage =
                "The value is not a valid absolute URL.";

            return false;
        }

        bool isHttp = string.Equals(
            uri.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase);

        bool isHttps = string.Equals(
            uri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);

        if (!isHttp && !isHttps)
        {
            validationMessage =
                "Only HTTP and HTTPS URLs are supported.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            validationMessage =
                "The URL does not contain a valid host.";

            return false;
        }

        try
        {
            var builder = new UriBuilder(uri)
            {
                Scheme = uri.Scheme.ToLowerInvariant(),
                Host = uri.IdnHost.ToLowerInvariant(),

                // URL fragments are not part of an HTTP request and
                // therefore do not identify a different download.
                Fragment = string.Empty
            };

            if (uri.IsDefaultPort)
            {
                builder.Port = -1;
            }

            normalizedUrl = builder.Uri.AbsoluteUri;
            validationMessage = string.Empty;

            return true;
        }
        catch (Exception exception)
            when (exception is UriFormatException or ArgumentException)
        {
            validationMessage =
                "The URL could not be normalized safely.";

            return false;
        }
    }
}
