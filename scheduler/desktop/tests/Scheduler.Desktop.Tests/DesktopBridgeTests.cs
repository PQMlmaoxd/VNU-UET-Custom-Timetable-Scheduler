using System.Text.Json;
using Scheduler.Desktop;
using Xunit;

namespace Scheduler.Desktop.Tests;

public sealed class DesktopBridgeTests
{
    [Theory]
    [InlineData("https://scheduler.local/index.html", true)]
    [InlineData("https://scheduler.local/assets/app.js", true)]
    [InlineData("http://scheduler.local/index.html", false)]
    [InlineData("https://scheduler.local.evil/index.html", false)]
    [InlineData("https://scheduler.local:444/index.html", false)]
    [InlineData("https://user@scheduler.local/index.html", false)]
    [InlineData("https://example.com/index.html", false)]
    public void IsTrustedAppUrlAcceptsOnlyTheLocalHttpsOrigin(string url, bool expected)
    {
        Assert.Equal(expected, MainWindow.IsTrustedAppUrl(url));
    }

    [Fact]
    public async Task HandleAsyncReturnsDispatcherResultForValidRequest()
    {
        var bridge = new DesktopBridgeSession(new RecordingDispatcher());
        var response = await bridge.HandleAsync(
            """{"protocol_version":1,"id":"request-1","method":"validate_workbook","payload":{"name":"schedule.xlsx"}}""");

        using var document = JsonDocument.Parse(response);
        Assert.Equal(1, document.RootElement.GetProperty("protocol_version").GetInt32());
        Assert.Equal("request-1", document.RootElement.GetProperty("id").GetString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("validate_workbook", document.RootElement.GetProperty("result").GetProperty("method").GetString());
    }

    [Fact]
    public async Task HandleAsyncRejectsUnsupportedProtocolVersion()
    {
        var bridge = new DesktopBridgeSession(new RecordingDispatcher());
        var response = await bridge.HandleAsync(
            """{"protocol_version":2,"id":"request-1","method":"validate_workbook","payload":{}}""");

        using var document = JsonDocument.Parse(response);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Unsupported desktop bridge protocol version", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HandleAsyncRejectsOversizedMessagesBeforeDispatch()
    {
        var dispatcher = new RecordingDispatcher();
        var bridge = new DesktopBridgeSession(dispatcher);
        var response = await bridge.HandleAsync(new string('x', DesktopBridgeSession.MaximumMessageCharacters + 1));

        using var document = JsonDocument.Parse(response);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("request is too large", document.RootElement.GetProperty("error").GetString());
        Assert.Null(dispatcher.LastMethod);
    }

    [Fact]
    public async Task HandleAsyncAllowsOnlyOneHeavyCommandAtATime()
    {
        var dispatcher = new BlockingDispatcher();
        var bridge = new DesktopBridgeSession(dispatcher);
        var first = bridge.HandleAsync(
            """{"protocol_version":1,"id":"solve-1","method":"solve_workbook","payload":{}}""");
        await dispatcher.Started.Task;

        var second = await bridge.HandleAsync(
            """{"protocol_version":1,"id":"validate-1","method":"validate_workbook","payload":{}}""");
        using var secondDocument = JsonDocument.Parse(second);
        Assert.False(secondDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("đang chạy", secondDocument.RootElement.GetProperty("error").GetString());

        await bridge.HandleAsync(
            """{"protocol_version":1,"id":"cancel-1","method":"cancel_command","payload":{"target_id":"solve-1"}}""");
        await first;
    }

    [Fact]
    public async Task HandleAsyncDoesNotExposeUnexpectedDispatcherFailures()
    {
        var bridge = new DesktopBridgeSession(new ThrowingDispatcher());
        var response = await bridge.HandleAsync(
            """{"protocol_version":1,"id":"request-1","method":"validate_workbook","payload":{}}""");

        using var document = JsonDocument.Parse(response);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Desktop bridge command failed.", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HandleAsyncCancelsTheTargetCommand()
    {
        var dispatcher = new BlockingDispatcher();
        var bridge = new DesktopBridgeSession(dispatcher);
        var command = bridge.HandleAsync(
            """{"protocol_version":1,"id":"solve-1","method":"solve_workbook","payload":{}}""");
        await dispatcher.Started.Task;

        var cancellation = await bridge.HandleAsync(
            """{"protocol_version":1,"id":"cancel-1","method":"cancel_command","payload":{"target_id":"solve-1"}}""");
        var commandResponse = await command;

        using var cancellationDocument = JsonDocument.Parse(cancellation);
        Assert.True(cancellationDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(cancellationDocument.RootElement.GetProperty("result").GetProperty("cancelled").GetBoolean());

        using var commandDocument = JsonDocument.Parse(commandResponse);
        Assert.False(commandDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Desktop bridge command was cancelled.", commandDocument.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HandleAsyncRecordsOnlySanitizedCommandMetadata()
    {
        var logger = new RecordingActivityLogger();
        var bridge = new DesktopBridgeSession(new RecordingDispatcher(), logger);

        await bridge.HandleAsync(
            """{"protocol_version":1,"id":"request-1","method":"validate_workbook","payload":{"file_name":"sensitive.xlsx"}}""");

        var activity = Assert.Single(logger.Activities);
        Assert.Equal("request-1", activity.CorrelationId);
        Assert.Equal("validate_workbook", activity.Command);
        Assert.Equal("completed", activity.Outcome);
        Assert.Null(activity.SolverVersion);
    }

    [Fact]
    public async Task HandleAsyncAppliesAValidThemePreferenceToTheNativeShell()
    {
        var shell = new RecordingShellController();
        var bridge = new DesktopBridgeSession(new RecordingDispatcher(), shellController: shell);

        var response = await bridge.HandleAsync(
            """{"protocol_version":1,"id":"theme-1","method":"set_theme","payload":{"preference":"dark"}}""");

        using var document = JsonDocument.Parse(response);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("dark", document.RootElement.GetProperty("result").GetProperty("preference").GetString());
        Assert.Equal(DesktopThemePreference.Dark, shell.LastTheme);
    }

    [Fact]
    public async Task HandleAsyncRejectsAnUnknownThemePreference()
    {
        var shell = new RecordingShellController();
        var bridge = new DesktopBridgeSession(new RecordingDispatcher(), shellController: shell);

        var response = await bridge.HandleAsync(
            """{"protocol_version":1,"id":"theme-1","method":"set_theme","payload":{"preference":"sepia"}}""");

        using var document = JsonDocument.Parse(response);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("must be system, light, or dark", document.RootElement.GetProperty("error").GetString());
        Assert.Null(shell.LastTheme);
    }

    [Fact]
    public async Task HandleAsyncMarksTheNativeShellReadyAfterReactRenders()
    {
        var shell = new RecordingShellController();
        var bridge = new DesktopBridgeSession(new RecordingDispatcher(), shellController: shell);

        var response = await bridge.HandleAsync(
            """{"protocol_version":1,"id":"ready-1","method":"desktop_ready","payload":{}}""");

        using var document = JsonDocument.Parse(response);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(document.RootElement.GetProperty("result").GetProperty("ready").GetBoolean());
        Assert.True(shell.IsFrontendReady);
    }

    [Fact]
    public async Task HandleAsyncDispatchesFormalArtifactExportWithoutChangingBridgeVersion()
    {
        var dispatcher = new RecordingDispatcher();
        var bridge = new DesktopBridgeSession(dispatcher);

        var response = await bridge.HandleAsync(
            """{"protocol_version":1,"id":"export-1","method":"export_unsat_artifact","payload":{}}""");

        using var document = JsonDocument.Parse(response);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("export_unsat_artifact", dispatcher.LastMethod);
        Assert.Equal(1, document.RootElement.GetProperty("protocol_version").GetInt32());
    }

    private sealed class RecordingDispatcher : IDesktopCommandDispatcher
    {
        public string? LastMethod { get; private set; }

        public Task<JsonElement> DispatchAsync(string method, JsonElement payload, CancellationToken cancellationToken)
        {
            LastMethod = method;
            using var document = JsonDocument.Parse($"{{\"method\":\"{method}\"}}");
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class ThrowingDispatcher : IDesktopCommandDispatcher
    {
        public Task<JsonElement> DispatchAsync(string method, JsonElement payload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("internal details");
    }

    private sealed class BlockingDispatcher : IDesktopCommandDispatcher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JsonElement> DispatchAsync(string method, JsonElement payload, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation did not stop the command.");
        }
    }

    private sealed class RecordingActivityLogger : IDesktopActivityLogger
    {
        public List<DesktopActivity> Activities { get; } = [];

        public Task RecordAsync(DesktopActivity activity)
        {
            Activities.Add(activity);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingShellController : IDesktopShellController
    {
        public bool IsFrontendReady { get; private set; }

        public DesktopThemePreference? LastTheme { get; private set; }

        public void ApplyTheme(DesktopThemePreference preference) => LastTheme = preference;

        public void MarkFrontendReady() => IsFrontendReady = true;
    }
}
