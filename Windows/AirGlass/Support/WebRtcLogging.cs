using System;
using Microsoft.Extensions.Logging;

namespace AirGlass.Support;

/// <summary>Routes the WebRTC library's warnings and errors into the app log.</summary>
internal sealed class WebRtcLogging : ILoggerFactory, ILogger
{
    /// <summary>Must run before any WebRTC type is used: the library creates
    /// its loggers in static initializers.</summary>
    public static void Install() => SIPSorcery.LogFactory.Set(new WebRtcLogging());

    public ILogger CreateLogger(string categoryName) => this;
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception != null) message += $" ({exception.GetType().Name}: {exception.Message})";
        Support.Log.WebRtc($"{logLevel}: {message}");
    }
}
