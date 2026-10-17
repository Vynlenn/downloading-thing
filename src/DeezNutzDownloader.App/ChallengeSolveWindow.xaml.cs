using System.Windows;
using DeezNutzDownloader.Core.Challenges;
using Microsoft.Web.WebView2.Core;

namespace DeezNutzDownloader.App;

/// <summary>
/// Hosts the site's own challenge page in a real embedded browser.
/// The user solves it by hand; Done captures the site's session
/// cookies into the encrypted session store. The app never sees
/// keystrokes or page contents — only cookies afterwards.
/// </summary>
public partial class ChallengeSolveWindow : Window
{
    private readonly string _pageUrl;
    private readonly string _host;
    private readonly IChallengeSessionStore _sessionStore;

    public bool SessionSaved { get; private set; }

    public int SavedCookieCount { get; private set; }

    public ChallengeSolveWindow(
        string pageUrl,
        string host,
        IChallengeSessionStore sessionStore)
    {
        _pageUrl = pageUrl;
        _host = host;
        _sessionStore =
            sessionStore ??
            throw new ArgumentNullException(nameof(sessionStore));

        InitializeComponent();

        Title = $"Solve challenge — {host}";

        InstructionText.Text =
            $"{host} is showing a check. Complete it below, " +
            "then choose Done — the queue resumes on its own.";

        Loaded += ChallengeSolveWindow_Loaded;
    }

    private async void ChallengeSolveWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.Source = new Uri(_pageUrl);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "The embedded browser could not start. " +
                "Install the WebView2 runtime and try again. " +
                exception.Message,
                "Browser unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Close();
        }
    }

    private async void DoneButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        DoneButton.IsEnabled = false;

        try
        {
            if (Browser.CoreWebView2 is null)
            {
                throw new InvalidOperationException(
                    "The browser is not ready yet.");
            }

            List<CoreWebView2Cookie> browserCookies =
                await Browser.CoreWebView2.CookieManager
                    .GetCookiesAsync(_pageUrl);

            var cookies = new List<ChallengeCookie>();

            foreach (CoreWebView2Cookie browserCookie in browserCookies)
            {
                if (
                    string.IsNullOrWhiteSpace(browserCookie.Name) ||
                    string.IsNullOrEmpty(browserCookie.Value))
                {
                    continue;
                }

                cookies.Add(
                    new ChallengeCookie(
                        browserCookie.Name,
                        browserCookie.Value,
                        string.IsNullOrWhiteSpace(browserCookie.Path)
                            ? "/"
                            : browserCookie.Path,
                        ToExpiresUtc(browserCookie)));
            }

            if (cookies.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "The site has not set any session cookies yet. " +
                    "Finish the check in the page first, then choose Done.",
                    "No session yet",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DoneButton.IsEnabled = true;
                return;
            }

            await _sessionStore.SaveAsync(
                new ChallengeSession(
                    _host,
                    cookies.AsReadOnly(),
                    DateTimeOffset.UtcNow,
                    ChallengeSessionSource.BrowserHandoff));

            SessionSaved = true;
            SavedCookieCount = cookies.Count;
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "The session could not be saved: " +
                exception.Message,
                "Session not saved",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            DoneButton.IsEnabled = true;
        }
    }

    private async void ForgetButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            await _sessionStore.RemoveAsync(_host);

            MessageBox.Show(
                this,
                $"The saved session for {_host} was forgotten. " +
                "Solve the check again to download from this host.",
                "Session forgotten",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "The session could not be forgotten: " +
                exception.Message,
                "Still saved",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static DateTimeOffset? ToExpiresUtc(
        CoreWebView2Cookie cookie)
    {
        try
        {
            if (cookie.IsSession)
            {
                return null;
            }

            DateTime expires = cookie.Expires;

            if (expires == default)
            {
                return null;
            }

            return expires.Kind == DateTimeKind.Unspecified
                ? new DateTimeOffset(
                    DateTime.SpecifyKind(expires, DateTimeKind.Utc))
                : new DateTimeOffset(expires);
        }
        catch
        {
            // An unreadable expiry degrades to session-cookie
            // semantics: usable until it stops working.
            return null;
        }
    }
}
