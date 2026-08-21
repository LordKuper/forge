using System.Text.Json;
using Forge.Infrastructure;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class SafeLoggerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task InformationAsyncAppendsOneJsonLinePerCallToANewlyCreatedLogDirectory()
    {
        using TestEnvironment environment = new();
        using SafeLogger logger = new(environment);
        string logPath = LogPath(environment);
        Assert.False(File.Exists(logPath));

        await logger.InformationAsync(
            "first_event",
            new Dictionary<string, object?> { ["count"] = 1 },
            TestContext.Current.CancellationToken);
        await logger.InformationAsync(
            "second_event",
            new Dictionary<string, object?> { ["count"] = 2 },
            TestContext.Current.CancellationToken);

        string[] lines = await File.ReadAllLinesAsync(logPath, TestContext.Current.CancellationToken);
        Assert.Equal(2, lines.Length);
        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal("first_event", first.RootElement.GetProperty("event_name").GetString());
        Assert.Equal(1, first.RootElement.GetProperty("properties").GetProperty("count").GetInt32());
        Assert.True(first.RootElement.TryGetProperty("timestamp", out _));
        using JsonDocument second = JsonDocument.Parse(lines[1]);
        Assert.Equal("second_event", second.RootElement.GetProperty("event_name").GetString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InformationAsyncRedactsASensitivePropertyNameInsteadOfWritingItRaw()
    {
        using TestEnvironment environment = new();
        using SafeLogger logger = new(environment);

        await logger.InformationAsync(
            "provider_authenticated",
            new Dictionary<string, object?> { ["password"] = "hunter2-the-real-secret-value" },
            TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(LogPath(environment), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("hunter2-the-real-secret-value", content, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:credential]", content, StringComparison.Ordinal);
    }

    /// <summary>Round 1 review of PR #86: an uncaught I/O failure here (disk full, permission
    /// denied, the log directory blocked by a same-named file) would propagate out of
    /// <c>InformationAsync</c> into whatever hosted service called it — and since nothing
    /// configures <c>BackgroundServiceExceptionBehavior</c>, an uncaught hosted-service exception
    /// crashes the whole Host process. Best-effort telemetry must never do that. Blocks the log
    /// directory's own path with a plain file (a real, portable way to force
    /// <see cref="Directory.CreateDirectory(string)"/> to throw <see cref="IOException"/> on every
    /// OS this test runs on) and proves the call still completes normally.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task InformationAsyncSwallowsAnIOExceptionInsteadOfThrowing()
    {
        using TestEnvironment environment = new();
        string logsDirectory = Path.Combine(environment.LocalApplicationData, "Forge", environment.InstanceId, "logs");
        Directory.CreateDirectory(Path.GetDirectoryName(logsDirectory)!);
        await File.WriteAllTextAsync(
            logsDirectory, "blocking the logs directory itself", TestContext.Current.CancellationToken);
        using SafeLogger logger = new(environment);

        await logger.InformationAsync(
            "blocked_event", new Dictionary<string, object?>(), TestContext.Current.CancellationToken);
    }

    private static string LogPath(TestEnvironment environment) =>
        Path.Combine(environment.LocalApplicationData, "Forge", environment.InstanceId, "logs", "forge.jsonl");
}
