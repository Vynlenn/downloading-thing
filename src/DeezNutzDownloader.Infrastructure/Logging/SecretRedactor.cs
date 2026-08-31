using System.Text.RegularExpressions;

namespace DeezNutzDownloader.Infrastructure.Logging;

public static partial class SecretRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string result = QuerySecretPattern().Replace(
            value,
            match => $"{match.Groups[1].Value}=[REDACTED]");

        result = UrlCredentialPattern().Replace(
            result,
            "://[REDACTED]@");

        result = AuthorizationPattern().Replace(
            result,
            "$1 [REDACTED]");

        return result;
    }

    [GeneratedRegex(
        @"(?i)\b(token|access_token|refresh_token|key|api_key|apikey|auth|password|passwd|secret|signature|sig|cookie)=([^&\s]+)")]
    private static partial Regex QuerySecretPattern();

    [GeneratedRegex(
        @"://[^/\s:@]+:[^/\s@]+@",
        RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentialPattern();

    [GeneratedRegex(
        @"(?i)\b(authorization|proxy-authorization):?\s+\S+")]
    private static partial Regex AuthorizationPattern();
}
