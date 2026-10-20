using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Solace.Common;

public static class GlobalLoggerFactory
{
    private static ILoggerFactory _factory = new NullLoggerFactory();

    public static void Initialize(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory;
    }

    public static ILogger CreateLogger(string categoryName)
        => _factory.CreateLogger(categoryName);

    public static ILogger<T> CreateLogger<T>()
        => _factory.CreateLogger<T>();
}