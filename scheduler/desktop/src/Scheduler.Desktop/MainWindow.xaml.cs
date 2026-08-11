using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Scheduler.Desktop;

public sealed partial class MainWindow : Window, IDesktopShellController, IDisposable
{
    private const string AppHostName = "scheduler.local";

    private readonly LocalDesktopActivityLogger activityLogger;
    private readonly DesktopBridgeSession bridgeSession;
    private readonly Stopwatch startupStopwatch = new();
    private readonly string webRoot;
    private CancellationTokenSource? startupWatchdog;
    private DesktopThemePreference themePreference;
    private string? themeBootstrapScriptId;
    private bool browserEventsAttached;
    private bool disposed;
    private bool frontendReady;
    private bool isStartingBrowser;

    public MainWindow(string webRoot, IDesktopCommandDispatcher dispatcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webRoot);
        ArgumentNullException.ThrowIfNull(dispatcher);

        this.webRoot = webRoot;
        themePreference = DesktopTheme.LoadPreference();
        activityLogger = LocalDesktopActivityLogger.CreateDefault();
        InitializeComponent();
        ApplyTheme(themePreference);
        bridgeSession = new DesktopBridgeSession(dispatcher, activityLogger, this);
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    public void ApplyTheme(DesktopThemePreference preference)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyTheme(preference));
            return;
        }

        themePreference = preference;
        DesktopTheme.SavePreference(preference);

        var palette = DesktopTheme.PaletteFor(preference);
        SetBrushColor("NativeCanvasBrush", palette.Canvas);
        SetBrushColor("NativeSurfaceBrush", palette.Surface);
        SetBrushColor("NativeTextBrush", palette.Text);
        SetBrushColor("NativeBorderBrush", palette.Border);
        SetBrushColor("NativeBrandBrush", palette.Brand);
        DesktopTheme.ApplyTitleBarTheme(this, preference);
    }

    public void MarkFrontendReady()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(MarkFrontendReady);
            return;
        }

        frontendReady = true;
        startupWatchdog?.Cancel();
        StartupOverlay.Visibility = Visibility.Collapsed;
        _ = RecordStartupActivityAsync("ready");
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs) => await StartBrowserAsync();

    private void OnSourceInitialized(object? sender, EventArgs eventArgs) =>
        DesktopTheme.ApplyTitleBarTheme(this, themePreference);

    private async Task StartBrowserAsync()
    {
        if (isStartingBrowser)
        {
            return;
        }

        isStartingBrowser = true;
        frontendReady = false;
        startupStopwatch.Restart();
        startupWatchdog?.Cancel();
        ShowStartupLoading("Đang tải giao diện…");

        try
        {
            await Browser.EnsureCoreWebView2Async();
            AttachBrowserEvents();
            await ConfigureThemeBootstrapAsync();
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AppHostName,
                webRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
            Browser.CoreWebView2.Navigate($"https://{AppHostName}/index.html");
            StartStartupWatchdog();
        }
        catch (Exception)
        {
            ShowStartupFailure("Không thể mở giao diện. Hãy thử lại hoặc kiểm tra cài đặt ứng dụng.");
        }
        finally
        {
            isStartingBrowser = false;
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        if (!IsTrustedAppUrl(eventArgs.Source))
        {
            return;
        }

        var response = await bridgeSession.HandleAsync(eventArgs.WebMessageAsJson);
        Browser.CoreWebView2.PostWebMessageAsJson(response);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (IsTrustedAppUrl(eventArgs.Uri))
        {
            return;
        }

        eventArgs.Cancel = true;
        bridgeSession.CancelActiveCommands();
        ShowStartupFailure("Liên kết không được phép mở trong ứng dụng.");
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (!eventArgs.IsSuccess)
        {
            ShowStartupFailure("Không thể mở giao diện. Hãy thử lại.");
            return;
        }

        StartupDetail.Text = "Đang hoàn tất…";
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs eventArgs)
    {
        bridgeSession.CancelActiveCommands();
        ShowStartupFailure("Giao diện đã dừng. Hãy thử lại.");
    }

    private void OnDiagnosticsClick(object sender, RoutedEventArgs eventArgs)
    {
        var runtimeVersion = Browser.CoreWebView2?.Environment.BrowserVersionString;
        MessageBox.Show(
            this,
            DesktopDiagnostics.Create(runtimeVersion).ToDisplayText(),
            "Thông tin kỹ thuật",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void OnExportSupportBundleClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".zip",
            FileName = "VNU-UET-Custom-Timetable-Scheduler-support.zip",
            Filter = "ZIP archive (*.zip)|*.zip",
            OverwritePrompt = true,
            Title = "Xuất gói hỗ trợ",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var runtimeVersion = Browser.CoreWebView2?.Environment.BrowserVersionString;
            await DesktopSupportBundle.CreateAsync(
                dialog.FileName,
                DesktopDiagnostics.Create(runtimeVersion),
                DesktopReleaseMetadata.DefaultLogDirectory);
            MessageBox.Show(
                this,
                "Đã tạo gói hỗ trợ.",
                "Xuất gói hỗ trợ",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            // The user did not receive an incomplete archive.
        }
        catch (Exception)
        {
            MessageBox.Show(
                this,
                "Không thể tạo gói hỗ trợ. Hãy thử chọn một vị trí khác.",
                "Xuất gói hỗ trợ",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OnRetryClick(object sender, RoutedEventArgs eventArgs) => await StartBrowserAsync();

    private void AttachBrowserEvents()
    {
        if (browserEventsAttached)
        {
            return;
        }

        Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
        Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        Browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        Browser.CoreWebView2.ProcessFailed += OnProcessFailed;
        browserEventsAttached = true;
    }

    public static bool IsTrustedAppUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, AppHostName, StringComparison.OrdinalIgnoreCase)
            && (uri.Port is -1 or 443)
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    private async Task ConfigureThemeBootstrapAsync()
    {
        if (themeBootstrapScriptId is not null)
        {
            Browser.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(themeBootstrapScriptId);
        }

        themeBootstrapScriptId = await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            DesktopTheme.CreateDocumentBootstrapScript(themePreference));
    }

    private void StartStartupWatchdog()
    {
        startupWatchdog?.Cancel();
        startupWatchdog?.Dispose();
        startupWatchdog = new CancellationTokenSource();
        _ = WatchForFrontendReadyAsync(startupWatchdog.Token);
    }

    private async Task WatchForFrontendReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            if (!frontendReady)
            {
                ShowStartupFailure("Mở ứng dụng mất quá nhiều thời gian. Hãy thử lại.");
            }
        }
        catch (OperationCanceledException)
        {
            // The frontend became ready or the window closed.
        }
    }

    private void ShowStartupLoading(string detail)
    {
        StartupOverlay.Visibility = Visibility.Visible;
        StartupTitle.Text = "Đang mở ứng dụng";
        StartupDetail.Text = detail;
        RetryButton.Visibility = Visibility.Collapsed;
    }

    private void ShowStartupFailure(string detail)
    {
        startupWatchdog?.Cancel();
        StartupOverlay.Visibility = Visibility.Visible;
        StartupTitle.Text = "Không thể mở giao diện";
        StartupDetail.Text = detail;
        RetryButton.Visibility = Visibility.Visible;
        _ = RecordStartupActivityAsync("failed");
    }

    private async Task RecordStartupActivityAsync(string outcome)
    {
        try
        {
            await activityLogger.RecordAsync(new DesktopActivity(
                DateTimeOffset.UtcNow,
                "startup",
                "frontend_startup",
                outcome,
                checked((long)Math.Ceiling(startupStopwatch.Elapsed.TotalMilliseconds)),
                DesktopBridgeSession.ProtocolVersion,
                null));
        }
        catch (Exception)
        {
            // Startup diagnostics are best effort and must not affect the UI.
        }
    }

    private void SetBrushColor(string resourceKey, Color color)
    {
        Resources[resourceKey] = new SolidColorBrush(color);
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        startupWatchdog?.Cancel();
        startupWatchdog?.Dispose();
        startupWatchdog = null;
        bridgeSession.Dispose();
        GC.SuppressFinalize(this);
    }
}
