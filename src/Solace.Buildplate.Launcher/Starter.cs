using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using BitcoderCZ.IO;
using Microsoft.Extensions.Logging;
using Solace.Buildplate.Common;
using Solace.Buildplate.Connector.Model;
using Solace.Common;
using Solace.Common.Utils;
using Solace.EventBus.Client;

namespace Solace.Buildplate.Launcher;

internal sealed partial class Starter : IAsyncDisposable
{
    private readonly EventBusClient _eventBusClient;
    private readonly string _publicAddress;
    public readonly ushort PublicPort;
    private readonly string _javaCmd;
    private readonly string _eventBusConnectionString;

    private readonly AbsoluteFile _fountainBridgeJar;
    private readonly AbsoluteDirectory _serverTemplateDir;
    private readonly RelativeFile _fabricJarName;
    private readonly AbsoluteFile _connectorPluginJar;
    private readonly ILogger<Starter> _logger;
    private readonly SubprocessLogger _serverLogger;
    private readonly SubprocessLogger _bridgeLogger;

    public const ushort SERVER_INTERNAL_PORT = 25565;
    public const string FABRIC_QUEUE_NAME = "fabric_server";
    public const string BUILDPLATES_QUEUE_NAME = "buildplates";

    private readonly AbsoluteDirectory _baseDir;
    private AbsoluteDirectory _serverWorkDir = null!;
    private AbsoluteDirectory _bridgeWorkDir = null!;

    private ConsoleProcess? _serverProcess;
    private ConsoleProcess? _bridgeProcess;
    private bool _shuttingDown;
    private readonly ReentrantAsyncLock.ReentrantAsyncLock _subprocessLock = new();
    private readonly TaskCompletionSource<bool> _serverReadyTcs = new();

    private Subscriber? _fabricSubscriber;

    public string PublicAddress => _publicAddress;

    public Starter(EventBusClient eventBusClient, string eventBusConnectionString, string publicAddress, ushort publicPort, string javaCmd, string bridgeJar, AbsoluteDirectory serverTemplateDir, string fabricJarName, string connectorPluginJar, ILoggerFactory loggerFactory, ILogger<Starter> logger)
    {
        _eventBusClient = eventBusClient;
        _eventBusConnectionString = eventBusConnectionString;
        _publicAddress = publicAddress;
        PublicPort = publicPort;
        _javaCmd = javaCmd;

        _fountainBridgeJar = new AbsoluteFile(Path.GetFullPath(bridgeJar));
        _serverTemplateDir = serverTemplateDir;
        _fabricJarName = new RelativeFile(fabricJarName);
        _connectorPluginJar = new AbsoluteFile(Path.GetFullPath(connectorPluginJar));
        _logger = logger;
        _serverLogger = new SubprocessLogger(loggerFactory.CreateLogger("FabricServer"));
        _bridgeLogger = new SubprocessLogger(loggerFactory.CreateLogger("FountainBridge"));

        _baseDir = new AbsoluteDirectory(Path.Combine(Path.GetTempPath(), "solace-server-runtime"));
    }

    [SupportedOSPlatform("android")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await ServerUtils.WaitForSetup(App.StaticDataPath, _logger, cancellationToken);

        var resolvedFabricJar = App.ResolveVersionedFile((App.StaticDataPath / "server_template_dir" / _fabricJarName).Value, Constants.GameVersion, _logger)
            ?? throw new InvalidOperationException("Fabric JAR not found in server template directory");

        var fabricJarFileName = Path.GetFileName(resolvedFabricJar);

        _baseDir.Create();
        _serverWorkDir = _baseDir / "server";
        _bridgeWorkDir = _baseDir / "bridge";
        _serverWorkDir.Create();
        _bridgeWorkDir.Create();

        await SetupServerFilesAsync(fabricJarFileName);
        SetupBridgeFiles();

        _fabricSubscriber = await _eventBusClient.AddSubscriberAsync(FABRIC_QUEUE_NAME,
            HandleFabricServerEvent,
            async ex =>
            {
                LogFabricServerSubscriberError(ex);
            });

        await StartServerProcessAsync(fabricJarFileName);
        await StartBridgeProcessAsync();
    }

    private Task HandleFabricServerEvent(SubscriberEvent @event, CancellationToken cancellationToken)
    {
        if (@event.Type is "started")
        {
            LogFabricServerReady();
            _serverReadyTcs.TrySetResult(true);
        }

        return Task.CompletedTask;
    }

    public Task WaitForServerReadyAsync() => _serverReadyTcs.Task;

    [SupportedOSPlatform("android")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    private async Task StartServerProcessAsync(string fabricJarName)
    {
        await using (await _subprocessLock.LockAsync(CancellationToken.None))
        {
            if (_shuttingDown || _serverProcess is not null)
            {
                return;
            }

            LogStartingServerProcess();

            try
            {
                _serverProcess = new ConsoleProcess(_javaCmd, _logger, useShellExecute: false, redirect: true, openInNewWindow: false);
                _serverProcess.StandartTextReceived += (_, e) => _serverLogger.Forward(e.Data, isErrorStream: false);
                _serverProcess.ErrorTextReceived += (_, e) => _serverLogger.Forward(e.Data, isErrorStream: true);

                var serverLogLevel = GetEffectiveLogLevel(_serverLogger).ToLowerInvariant();
                List<string> serverArgs =
                [
                    $"-Dfabric.log.level={serverLogLevel}",
                    "-jar", fabricJarName,
                    "-nogui"
                ];

                await _serverProcess.ExecuteAsync(_serverWorkDir.Value, [.. serverArgs]);
                LogServerProcessStarted(_serverProcess.Id);

                _ = Task.Run(async () =>
                {
                    await _serverProcess.WaitForExitAsync();
                    await using (await _subprocessLock.LockAsync(CancellationToken.None))
                    {
                        var exitCode = _serverProcess.ExitCodeText;
                        _serverProcess.Dispose();
                        _serverProcess = null;

                        if (!_shuttingDown)
                        {
                            LogServerProcessUnexpectedExit(exitCode);
                            await Task.Delay(2000);
                            if (!_shuttingDown)
                            {
                                await StartServerProcessAsync(fabricJarName);
                            }
                        }
                        else
                        {
                            LogServerProcessFinished(exitCode);
                        }
                    }
                });
            }
            catch (Exception exception)
            {
                LogServerProcessStartError(exception);
            }
        }
    }

    [SupportedOSPlatform("android")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    private async Task StartBridgeProcessAsync()
    {
        await using (await _subprocessLock.LockAsync(CancellationToken.None))
        {
            if (_shuttingDown || _bridgeProcess is not null)
            {
                return;
            }

            LogStartingBridgeProcess();

            try
            {
                var connectorPluginArg = JsonSerializer.Serialize(new ConnectorPluginArg(
                    _eventBusConnectionString,
                    BUILDPLATES_QUEUE_NAME
                ), AppJsonContext.Default.ConnectorPluginArg);

                _bridgeProcess = new ConsoleProcess(_javaCmd, _logger, useShellExecute: false, redirect: true, openInNewWindow: false);
                _bridgeProcess.StandartTextReceived += (_, e) => _bridgeLogger.Forward(e.Data, isErrorStream: false);
                _bridgeProcess.ErrorTextReceived += (_, e) => _bridgeLogger.Forward(e.Data, isErrorStream: true);

                var bridgeLogLevel = GetEffectiveLogLevel(_bridgeLogger);

                List<string> bridgeArgs = [
                    $"-Dfountain.logLevel={bridgeLogLevel}",
                    "-jar", _fountainBridgeJar.Value,
                    "-port", PublicPort.ToString(CultureInfo.InvariantCulture),
                    "-serverAddress", "127.0.0.1",
                    "-serverPort", SERVER_INTERNAL_PORT.ToString(CultureInfo.InvariantCulture),
                    "-connectorPluginJar", _connectorPluginJar.Value,
                    "-connectorPluginClass", "earthrestored.solace.buildplate.connector.plugin.SolaceConnectorPlugin",
                    "-connectorPluginArg", connectorPluginArg,
                    "-useUUIDAsUsername",
                    "-logLevel", bridgeLogLevel,
                ];

                await _bridgeProcess.ExecuteAsync(_bridgeWorkDir.Value, [.. bridgeArgs]);

                LogBridgeProcessStarted(_bridgeProcess.Id);

                _ = Task.Run(async () =>
                {
                    await _bridgeProcess.WaitForExitAsync();
                    await using (await _subprocessLock.LockAsync(CancellationToken.None))
                    {
                        var exitCode = _bridgeProcess.ExitCodeText;
                        _bridgeProcess.Dispose();
                        _bridgeProcess = null;

                        if (!_shuttingDown)
                        {
                            LogBridgeProcessUnexpectedExit(exitCode);
                            // auto-restart if unexpectedly exited
                            await Task.Delay(2000);
                            if (!_shuttingDown)
                            {
                                await StartBridgeProcessAsync();
                            }
                        }
                        else
                        {
                            LogBridgeProcessFinished(exitCode);
                        }
                    }
                });
            }
            catch (Exception exception)
            {
                LogBridgeProcessStartError(exception);
            }
        }
    }

    private async Task SetupServerFilesAsync(string fabricJarName)
    {
        CopyServerFile(_serverTemplateDir / new RelativeFile(fabricJarName), _serverWorkDir / new RelativeFile(fabricJarName), false);
        CopyServerFile(_serverTemplateDir / ".fabric" / "server", _serverWorkDir / ".fabric" / "server", true);
        CopyServerFile(_serverTemplateDir / "libraries", _serverWorkDir / "libraries", true);
        CopyServerFile(_serverTemplateDir / "versions", _serverWorkDir / "versions", true);
        CopyServerFile(_serverTemplateDir / "mods", _serverWorkDir / "mods", true);

        await (_serverWorkDir / new RelativeFile("eula.txt")).WriteAllTextAsync("eula=true");

        var serverProperties = new StringBuilder()
            .Append("online-mode=false\n")
            .Append("enforce-secure-profile=false\n")
            .Append("sync-chunk-writes=false\n")
            .Append("spawn-protection=0\n")
            .Append("enable-command-block=true\n")
            .Append(CultureInfo.InvariantCulture, $"server-port={SERVER_INTERNAL_PORT}\n")
            .Append("gamemode=creative\n")
            .Append(CultureInfo.InvariantCulture, $"solace-event-bus-address={_eventBusConnectionString}\n")
            .Append(CultureInfo.InvariantCulture, $"solace-event-bus-queue-name={FABRIC_QUEUE_NAME}\n")
            .ToString();

        await (_serverWorkDir / new RelativeFile("server.properties")).WriteAllTextAsync(serverProperties);
    }

    private static void SetupBridgeFiles()
    {
        // empty
    }

    private static bool CopyServerFile(IAbsolutePath src, IAbsolutePath dst, bool directory)
    {
        if (!src.Exists)
        {
            return false;
        }

        if (directory)
        {
            ((AbsoluteDirectory)src).CopyContentsTo((AbsoluteDirectory)dst, true, true);
        }
        else
        {
            ((AbsoluteFile)src).CopyTo((AbsoluteFile)dst, true);
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await using (await _subprocessLock.LockAsync(CancellationToken.None))
        {
            _shuttingDown = true;

            if (_fabricSubscriber is not null)
            {
                await _fabricSubscriber.DisposeAsync();
            }

            if (_bridgeProcess is not null)
            {
                LogWaitingForBridgeToShutDown();
                await _bridgeProcess.StopAndWaitAsync(_logger);
                _bridgeProcess.Dispose();
                _bridgeProcess = null;
            }

            if (_serverProcess is not null)
            {
                LogAskingTheServerToShutDown();
                await _serverProcess.StopNoWaitAsync(_logger);
                _serverProcess.Dispose();
                _serverProcess = null;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Fabric server subscriber error")]
    private partial void LogFabricServerSubscriberError(Exception? exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fabric Minecraft server is ready")]
    private partial void LogFabricServerReady();

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting persistent Fabric server process")]
    private partial void LogStartingServerProcess();

    [LoggerMessage(Level = LogLevel.Information, Message = "Persistent Fabric server process started, PID: {Pid}")]
    private partial void LogServerProcessStarted(int Pid);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not start server process")]
    private partial void LogServerProcessStartError(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Persistent Fabric server unexpectedly terminated with exit code {ExitCode}")]
    private partial void LogServerProcessUnexpectedExit(string ExitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Persistent Fabric server has finished with exit code {ExitCode}")]
    private partial void LogServerProcessFinished(string ExitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting persistent Fountain bridge process")]
    private partial void LogStartingBridgeProcess();

    [LoggerMessage(Level = LogLevel.Information, Message = "Persistent Fountain bridge process started, PID: {Pid}")]
    private partial void LogBridgeProcessStarted(int Pid);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not start bridge process")]
    private partial void LogBridgeProcessStartError(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Persistent Fountain bridge unexpectedly terminated with exit code {ExitCode}")]
    private partial void LogBridgeProcessUnexpectedExit(string ExitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Persistent Fountain bridge has finished with exit code {ExitCode}")]
    private partial void LogBridgeProcessFinished(string ExitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for bridge to shut down")]
    private partial void LogWaitingForBridgeToShutDown();

    [LoggerMessage(Level = LogLevel.Information, Message = "Asking the server to shut down")]
    private partial void LogAskingTheServerToShutDown();

    private static string GetEffectiveLogLevel(SubprocessLogger logger)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            return "TRACE";
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            return "DEBUG";
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            return "INFO";
        }

        if (logger.IsEnabled(LogLevel.Warning))
        {
            return "WARN";
        }

        if (logger.IsEnabled(LogLevel.Error))
        {
            return "ERROR";
        }

        if (logger.IsEnabled(LogLevel.Critical))
        {
            return "FATAL";
        }

        return "OFF";
    }
}
