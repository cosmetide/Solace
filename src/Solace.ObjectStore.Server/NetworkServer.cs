using Serilog;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Solace.ObjectStore.Server;

public sealed partial class NetworkServer : IDisposable
{
    private readonly Server _server;
    private readonly TcpListener _serverSocket;
    private readonly CancellationTokenSource _cts = new();

    public NetworkServer(Server server, int port)
    {
        _server = server;
        _serverSocket = new TcpListener(IPAddress.Loopback, port);
    }

    public async Task RunAsync()
    {
        _serverSocket.Start();
        Log.Information("Server started on port {Port}", ((IPEndPoint)_serverSocket.LocalEndpoint).Port);

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                TcpClient client = await _serverSocket.AcceptTcpClientAsync(_cts.Token);
                Log.Information("Connection from {RemoteEndPoint}", client.Client.RemoteEndPoint);

                _ = HandleConnectionAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _serverSocket.Stop();
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
        _serverSocket.Dispose();
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = PipeReader.Create(stream);
            var writer = PipeWriter.Create(stream);

            try
            {
                await ProcessLinesAsync(reader, writer);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error processing connection");
            }
        }

        Log.Information("Connection closed");
    }

    private async Task ProcessLinesAsync(PipeReader reader, PipeWriter writer)
    {
        while (true)
        {
            string commandLine;
            while (true)
            {
                ReadResult result = await reader.ReadAsync();

                if (TryReadLine(result.Buffer, out ReadOnlySequence<byte> line, out SequencePosition restPosition))
                {
                    commandLine = Encoding.ASCII.GetString(line);
                    reader.AdvanceTo(restPosition);
                    break;
                }

                reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);

                if (result.IsCompleted)
                {
                    await reader.CompleteAsync();
                    await writer.CompleteAsync();
                    return;
                }
            }

            string[] parts = commandLine.Split(' ', 2);

            if (parts.Length < 2)
            {
                await WriteMessageAsync(writer, "ERR");
                continue;
            }

            string cmd = parts[0].ToUpperInvariant();
            string arg = parts[1];

            switch (cmd)
            {
                case "STORE":
                    if (int.TryParse(arg, out int length) && length >= 0)
                    {
                        byte[] payload = [];
                        if (length > 0)
                        {
                            var payloadResult = await reader.ReadAtLeastAsync(length);
                            payload = payloadResult.Buffer.Slice(0, length).ToArray();
                            reader.AdvanceTo(payloadResult.Buffer.GetPosition(length));
                        }

                        string? id = await _server.StoreAsync(payload);
                        await WriteMessageAsync(writer, id != null ? $"OK {id}" : "ERR");
                    }

                    break;
                case "GET":
                    if (ValidateObjectId(arg))
                    {
                        byte[]? data = await _server.LoadAsync(arg);
                        if (data != null)
                        {
                            await WriteMessageAsync(writer, $"OK {data.Length}");
                            await writer.WriteAsync(data);
                        }
                        else
                        {
                            await WriteMessageAsync(writer, "ERR");
                        }
                    }

                    break;
                case "DEL":
                    bool deleted = await _server.DeleteAsync(arg);
                    await WriteMessageAsync(writer, deleted ? "OK" : "ERR");
                    break;
                default:
                    await WriteMessageAsync(writer, "ERR");
                    break;
            }
        }
    }

    private static bool TryReadLine(ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line, out SequencePosition rest)
    {
        SequencePosition? position = buffer.PositionOf((byte)'\n');

        if (position is null)
        {
            line = default;
            rest = buffer.Start;
            return false;
        }

        line = buffer.Slice(0, position.Value);
        rest = buffer.GetPosition(1, position.Value);
        return true;
    }

    private static async Task WriteMessageAsync(PipeWriter writer, string message)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(message + "\n");
        await writer.WriteAsync(bytes);
    }

    private static bool ValidateObjectId(string id)
    {
        if (!GetRegex1().IsMatch(id))
        {
            return false;
        }

        return true;
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex GetRegex1();
}
