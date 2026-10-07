using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace XPanel.Application;

public sealed record CopilotUsageSnapshot(long UsedCredits, long TotalCredits, string ResetText)
{
    public string Signature => $"{UsedCredits}:{TotalCredits}:{ResetText}";
    public byte UsedPercent => (byte)Math.Clamp(
        (int)Math.Round(UsedCredits * 100d / TotalCredits, MidpointRounding.AwayFromZero),
        0,
        100);
}

public partial class CopilotUsageWindow : Window
{
    private const string UsageUrl = "https://github.com/settings/copilot/features";
    private bool _browserReady;
    private bool _allowClose;
    private bool _hideAfterInitialization;
    private bool _waitingForUsageConfirmation;
    private bool _autoUsageReadActive;
    private readonly TaskCompletionSource<bool> _browserReadyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<CopilotUsageSnapshot>? UsageConfirmed;
    public event Action? EnableCancelled;

    public CopilotUsageWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XPanelClient",
                "CopilotUsage",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);

            var options = new CoreWebView2EnvironmentOptions
            {
                AllowSingleSignOnUsingOSPrimaryAccount = true,
            };
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: options);
            await Browser.EnsureCoreWebView2Async(environment);
            _browserReady = true;
            Browser.CoreWebView2.NavigationCompleted += Browser_NavigationCompleted;
            Browser.CoreWebView2.Navigate(UsageUrl);
            StatusText.Text = "Sign in with your company account, then enable Copilot Credits.";
            _browserReadyCompletion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"WebView2 could not start: {ex.Message}";
            _browserReadyCompletion.TrySetResult(false);
        }
    }

    public async Task<bool> EnsureBrowserReadyAsync(bool showForLogin)
    {
        if (showForLogin)
        {
            _waitingForUsageConfirmation = true;
        }

        if (!IsLoaded && !IsVisible)
        {
            ShowInTaskbar = showForLogin;
            ShowActivated = showForLogin;
            if (!showForLogin)
            {
                _hideAfterInitialization = true;
                Opacity = 0;
            }

            Show();
        }

        bool ready = await _browserReadyCompletion.Task;
        if (showForLogin)
        {
            ShowInTaskbar = true;
            Show();
            Activate();
        }
        else if (_hideAfterInitialization)
        {
            Hide();
            Opacity = 1;
            _hideAfterInitialization = false;
        }

        return ready;
    }

    public void ShowForReauthentication()
    {
        _waitingForUsageConfirmation = true;
        ShowInTaskbar = true;
        Show();
        Activate();
        StatusText.Text = "Refreshing GitHub usage. Sign in again if requested; this window closes when usage is detected.";
        if (_browserReady)
        {
            try
            {
                Browser.CoreWebView2.Navigate(UsageUrl);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not reopen GitHub usage page: {ex.Message}";
            }
        }
    }

    private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        StatusText.Text = e.IsSuccess
            ? "Sign in with your company account. This window closes when usage is detected."
            : $"Navigation failed: {e.WebErrorStatus}";
        if (e.IsSuccess && _waitingForUsageConfirmation)
        {
            _ = TryConfirmUsageAutomaticallyAsync();
        }
    }

    private void OpenUsagePage_Click(object sender, RoutedEventArgs e)
    {
        if (_browserReady)
        {
            Browser.CoreWebView2.Navigate(UsageUrl);
        }
    }

    private async Task TryConfirmUsageAutomaticallyAsync()
    {
        if (_autoUsageReadActive || !_waitingForUsageConfirmation)
        {
            return;
        }

        _autoUsageReadActive = true;
        try
        {
            for (int attempt = 0; attempt < 30 && _waitingForUsageConfirmation; attempt++)
            {
                CopilotUsageSnapshot? snapshot = await ReadUsageAsync(reload: false);
                if (snapshot != null)
                {
                    _waitingForUsageConfirmation = false;
                    UsageText.Text = FormatUsage(snapshot);
                    ResetText.Text = snapshot.ResetText;
                    StatusText.Text = "Usage detected. Copilot Credits enabled.";
                    UsageConfirmed?.Invoke(snapshot);
                    Hide();
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            if (_waitingForUsageConfirmation)
            {
                StatusText.Text = "Usage not detected yet. Complete sign-in and open the Copilot Usage section.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not read usage: {ex.Message}";
        }
        finally
        {
            _autoUsageReadActive = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelEnable();
    }

    public void CancelEnable()
    {
        _waitingForUsageConfirmation = false;
        EnableCancelled?.Invoke();
        Hide();
    }

    public async Task<CopilotUsageSnapshot?> ReadUsageAsync(bool reload)
    {
        if (!Dispatcher.CheckAccess())
        {
            return await Dispatcher.InvokeAsync(() => ReadUsageAsync(reload)).Task.Unwrap();
        }

        if (!_browserReady)
        {
            return null;
        }

        if (reload)
        {
            await ReloadUsagePageAsync();
            await Task.Delay(500);
        }

        const string script = "(() => {"
            + "const text = document.body?.innerText || '';"
            + "const credits = text.match(/[\\d,]+\\s*\\/\\s*[\\d,]+\\s*AI credits/i)?.[0] || null;"
            + "const reset = text.match(/Resets in[^\\r\\n]*/i)?.[0] || '';"
            + "return { url: location.href, credits, reset };"
            + "})()";

        string json = await Browser.ExecuteScriptAsync(script);
        UsagePageResult? result = JsonSerializer.Deserialize<UsagePageResult>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result?.Credits == null || !Uri.TryCreate(result.Url, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/settings/copilot/features", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Match match = Regex.Match(result.Credits, @"([\d,]+)\s*/\s*([\d,]+)");
        if (!match.Success
            || !long.TryParse(match.Groups[1].Value.Replace(",", string.Empty), out long used)
            || !long.TryParse(match.Groups[2].Value.Replace(",", string.Empty), out long total)
            || total <= 0)
        {
            return null;
        }

        return new CopilotUsageSnapshot(used, total, result.Reset ?? string.Empty);
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    private async Task ReloadUsagePageAsync()
    {
        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong targetNavigationId = 0;
        void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            targetNavigationId = args.NavigationId;
        }
        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (targetNavigationId != 0 && args.NavigationId == targetNavigationId)
            {
                navigation.TrySetResult(args.IsSuccess);
            }
        }

        Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
        Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        try
        {
            Browser.CoreWebView2.Navigate(UsageUrl);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            bool success = await navigation.Task.WaitAsync(timeout.Token);
            if (!success)
            {
                throw new InvalidOperationException("GitHub usage page navigation failed.");
            }
        }
        finally
        {
            Browser.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            Browser.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        CancelEnable();
    }

    private static string FormatUsage(CopilotUsageSnapshot snapshot)
    {
        double percentUsed = snapshot.UsedCredits * 100d / snapshot.TotalCredits;
        return $"{snapshot.UsedCredits.ToString("N0", CultureInfo.InvariantCulture)} / {snapshot.TotalCredits.ToString("N0", CultureInfo.InvariantCulture)} AI credits ({percentUsed.ToString("0.##", CultureInfo.InvariantCulture)}% used)";
    }

    private sealed class UsagePageResult
    {
        public string? Url { get; set; }
        public string? Credits { get; set; }
        public string? Reset { get; set; }
    }
}
