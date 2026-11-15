using Microsoft.Extensions.Logging;

namespace YardStickOne.Api.Tests;

/// <summary>
/// Logger double that records every formatted message at all levels.
/// </summary>
internal sealed class ListLogger : ILogger
{
	public List<string> Messages { get; } = [];

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		=> Messages.Add($"{logLevel}: {formatter(state, exception)}");
}
