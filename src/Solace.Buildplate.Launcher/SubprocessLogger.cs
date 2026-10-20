using Microsoft.Extensions.Logging;

namespace Solace.Buildplate.Launcher;

internal sealed partial class SubprocessLogger
{
    private readonly ILogger _logger;

    public SubprocessLogger(ILogger logger)
    {
        _logger = logger;
    }

    public bool IsEnabled(LogLevel level)
        => _logger.IsEnabled(level);

    public void Forward(string? line, bool isErrorStream)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        // todo: optimize this, SearchValues?
        if (isErrorStream)
        {
            if (line.StartsWith("SLF4J:", StringComparison.OrdinalIgnoreCase))
            {
                LogGenericWarning(line);
                return;
            }

            if (line.Contains("/FATAL]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("FATAL", StringComparison.OrdinalIgnoreCase))
            {
                LogGenericCritical(line);
                return;
            }

            if (line.Contains("/ERROR]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                LogGenericError(line);
                return;
            }

            if (line.Contains("/WARN]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("WARN", StringComparison.OrdinalIgnoreCase))
            {
                LogGenericWarning(line);
                return;
            }

            if (line.Contains("/INFO]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("INFO", StringComparison.OrdinalIgnoreCase))
            {
                LogGenericInfo(line);
                return;
            }

            if (line.Contains("/DEBUG]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("DEBUG", StringComparison.OrdinalIgnoreCase))
            {
                LogGenericDebug(line);
                return;
            }

            if (line.Contains("/TRACE]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("TRACE", StringComparison.OrdinalIgnoreCase))
            {
                LogGenericTrace(line);
                return;
            }

            LogGenericError(line);
            return;
        }

        if (line.Contains("/FATAL]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("FATAL", StringComparison.OrdinalIgnoreCase))
        {
            LogGenericCritical(line);
        }
        else if (line.Contains("/ERROR]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            LogGenericError(line);
        }
        else if (line.Contains("/WARN]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("WARN", StringComparison.OrdinalIgnoreCase))
        {
            LogGenericWarning(line);
        }
        else if (line.Contains("/DEBUG]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("DEBUG", StringComparison.OrdinalIgnoreCase))
        {
            LogGenericDebug(line);
        }
        else if (line.Contains("/TRACE]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("TRACE", StringComparison.OrdinalIgnoreCase))
        {
            LogGenericTrace(line);
        }
        else
        {
            LogGenericInfo(line);
        }
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Message}")]
    private partial void LogGenericTrace(string Message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Message}")]
    private partial void LogGenericDebug(string Message);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Message}")]
    private partial void LogGenericInfo(string Message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message}")]
    private partial void LogGenericWarning(string Message);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Message}")]
    private partial void LogGenericError(string Message);

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Message}")]
    private partial void LogGenericCritical(string Message);
}
