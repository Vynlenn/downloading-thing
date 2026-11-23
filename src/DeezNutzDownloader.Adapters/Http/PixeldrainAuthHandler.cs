using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DeezNutzDownloader.Adapters.Http;

/// <summary>
/// Signs Pixeldrain requests with the user's own API key, the way
/// Pixeldrain documents it: HTTP Basic auth, empty user name, key as
/// the password. With a Pro account that lifts the free per-IP quota
/// legitimately and opens the account's private files.
///
/// The key only ever goes to HTTPS Pixeldrain hosts. .NET drops the
/// Authorization header on redirects, so a redirect to a CDN or any
/// other host never carries it.
/// </summary>
public sealed class PixeldrainAuthHandler : DelegatingHandler
{
    private readonly AuthenticationHeaderValue _authorization;

    public PixeldrainAuthHandler(string apiKey, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(apiKey));
        }

        _authorization = BuildAuthorization(apiKey.Trim());
    }

    public static bool IsPixeldrainUri(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true } || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        string host = uri.IdnHost.TrimEnd('.');

        return
            host.Equals("pixeldrain.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("pixeldrain.nl", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".pixeldrain.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".pixeldrain.nl", StringComparison.OrdinalIgnoreCase);
    }

    internal static AuthenticationHeaderValue BuildAuthorization(string apiKey)
    {
        return new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + apiKey)));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (
            request.Headers.Authorization is null &&
            IsPixeldrainUri(request.RequestUri))
        {
            request.Headers.Authorization = _authorization;
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public sealed record PixeldrainKeyCheck(bool Valid, string Message);

/// <summary>Confirms an API key against Pixeldrain's account endpoint.</summary>
public static class PixeldrainAccount
{
    private static readonly Uri UserEndpoint = new("https://pixeldrain.com/api/user");

    public static async Task<PixeldrainKeyCheck> CheckKeyAsync(
        HttpClient httpClient,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
        request.Headers.Authorization = PixeldrainAuthHandler.BuildAuthorization(apiKey.Trim());

        try
        {
            using HttpResponseMessage response = await httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new PixeldrainKeyCheck(false, "Pixeldrain did not accept this key.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new PixeldrainKeyCheck(
                    false,
                    $"Pixeldrain answered HTTP {(int)response.StatusCode}; try again later.");
            }

            string json = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            return new PixeldrainKeyCheck(true, DescribeAccount(json));
        }
        catch (HttpRequestException exception)
        {
            return new PixeldrainKeyCheck(false, $"Could not reach Pixeldrain: {exception.Message}");
        }
    }

    internal static string DescribeAccount(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string? user =
                root.TryGetProperty("username", out JsonElement name) &&
                name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null;

            string? plan =
                root.TryGetProperty("subscription", out JsonElement subscription) &&
                subscription.ValueKind == JsonValueKind.Object &&
                subscription.TryGetProperty("name", out JsonElement planName) &&
                planName.ValueKind == JsonValueKind.String
                    ? planName.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(user))
            {
                return "Key accepted.";
            }

            return string.IsNullOrWhiteSpace(plan)
                ? $"Key accepted: signed in as {user}."
                : $"Key accepted: signed in as {user} ({plan}).";
        }
        catch (JsonException)
        {
            return "Key accepted.";
        }
    }
}
