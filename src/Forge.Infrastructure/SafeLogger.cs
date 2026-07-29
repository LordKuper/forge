using Forge.Application;
using Microsoft.Extensions.Logging;

namespace Forge.Infrastructure;

public sealed class SafeLogger(ILogger<SafeLogger> logger) : ISafeLogger
{
    private static readonly Action<
        ILogger,
        string,
        IReadOnlyDictionary<string, object?>,
        Exception?> LogEvent = LoggerMessage.Define<string, IReadOnlyDictionary<string, object?>>(
            LogLevel.Information,
            new EventId(1000, "ForgeEvent"),
            "{EventName} {@Properties}");

    public void Information(
        string eventName,
        IReadOnlyDictionary<string, object?> properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(properties);
        LogEvent(
            logger,
            eventName,
            SecretRedactor.RedactProperties(properties),
            null);
    }
}
