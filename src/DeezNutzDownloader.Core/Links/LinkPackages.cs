namespace DeezNutzDownloader.Core.Links;

public sealed record LinkPackage(
    string Name,
    IReadOnlyList<LinkAnalysisItem> Links)
{
    public int ValidCount =>
        Links.Count(
            item =>
                item.Status == LinkAnalysisStatus.Valid);

    public int TotalCount => Links.Count;
}

public static class LinkPackager
{
    public static IReadOnlyList<LinkPackage> GroupByHost(
        LinkAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var order = new List<string>();
        var groups = new Dictionary<string, List<LinkAnalysisItem>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (LinkAnalysisItem item in analysis.Items)
        {
            if (
                item.Status != LinkAnalysisStatus.Valid ||
                string.IsNullOrWhiteSpace(item.NormalizedUrl) ||
                !Uri.TryCreate(
                    item.NormalizedUrl,
                    UriKind.Absolute,
                    out Uri? uri) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                continue;
            }

            string host = uri.Host.ToLowerInvariant();

            if (!groups.TryGetValue(host, out List<LinkAnalysisItem>? group))
            {
                group = new List<LinkAnalysisItem>();
                groups.Add(host, group);
                order.Add(host);
            }

            group.Add(item);
        }

        return order
            .Select(
                host => new LinkPackage(
                    host,
                    Array.AsReadOnly(groups[host].ToArray())))
            .ToArray();
    }
}

public static class PackageFolderNames
{
    private const int MaximumLength = 100;
    private const char ForwardSlash = '/';
    private const char Backslash = (char)92;

    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9"
        };

    public static string Sanitize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "package";
        }

        string value = candidate.Trim();

        int separator = Math.Max(
            value.LastIndexOf(ForwardSlash),
            value.LastIndexOf(Backslash));

        if (separator >= 0)
        {
            value = value[(separator + 1)..];
        }

        char[] buffer = new char[value.Length];
        int length = 0;

        foreach (char character in value)
        {
            if (
                character == '.' && length == 0)
            {
                continue;
            }

            buffer[length++] =
                IsAllowed(character) ? character : '_';
        }

        string sanitized =
            new string(buffer, 0, length)
                .Trim()
                .TrimEnd(' ', '.');

        if (sanitized.Length == 0)
        {
            return "package";
        }

        if (sanitized.Length > MaximumLength)
        {
            sanitized = sanitized[..MaximumLength]
                .TrimEnd(' ', '.');
        }

        if (
            sanitized.Length == 0 ||
            ReservedNames.Contains(sanitized.Split('.', 2)[0]))
        {
            return "_" + sanitized;
        }

        return sanitized;
    }

    public static bool IsSafeSegment(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.Length > MaximumLength)
        {
            return false;
        }

        if (
            candidate.Any(
                character =>
                    !IsAllowed(character) ||
                    character == ForwardSlash ||
                    character == Backslash) ||
            candidate.Trim() != candidate ||
            candidate.EndsWith(" ", StringComparison.Ordinal) ||
            candidate.EndsWith(".", StringComparison.Ordinal) ||
            candidate.StartsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        return !ReservedNames.Contains(
            candidate.Split('.', 2)[0]);
    }

    private static bool IsAllowed(char character)
    {
        return
            !char.IsControl(character) &&
            character != '<' &&
            character != '>' &&
            character != ':' &&
            character != '"' &&
            character != ForwardSlash &&
            character != Backslash &&
            character != '|' &&
            character != '?' &&
            character != '*';
    }
}
