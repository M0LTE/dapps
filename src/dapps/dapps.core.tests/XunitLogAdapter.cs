﻿using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace dapps.core.tests;

public class XunitLogAdapter(ITestOutputHelper testOutputHelper) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return new NullDisposable();
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        testOutputHelper.WriteLine(formatter(state, exception));
    }
}
