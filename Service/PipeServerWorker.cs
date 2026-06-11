using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkingTool.Shared;

namespace NetworkingTool.Service;

/// <summary>
/// Owns the named-pipe accept loop. Each connection carries exactly one request line and one
/// response line of JSON. The loop ACCEPTS a connection and then hands it to a separate task so it
/// can immediately create the next listening instance — a slow request (the network ops shell out
/// to PowerShell for up to ~60s) therefore never leaves the server with no instance listening, and
/// concurrent tray requests are served in parallel instead of timing out. Concurrency is bounded by
/// a semaphore matching the number of pipe instances, and each connection has a read timeout so a
/// client that connects but never sends a complete line cannot hold a slot indefinitely.
/// </summary>
public sealed class PipeServerWorker : BackgroundService
{
    private const int MaxConcurrentClients = 4;

    /// <summary>Per-connection budget. A little above the 60s PowerShell cap in NetworkOperations.</summary>
    private const int RequestTimeoutMs = 70_000;

    private readonly ILogger<PipeServerWorker> _logger;

    public PipeServerWorker(ILogger<PipeServerWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ServiceLog.Write("Pipe server worker started.");
        _logger.LogInformation("Pipe server worker started on pipe {PipeName}.", Constants.PipeName);

        using var gate = new SemaphoreSlim(MaxConcurrentClients, MaxConcurrentClients);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Bound the number of in-flight connections to the pipe instance count.
                await gate.WaitAsync(stoppingToken);

                NamedPipeServerStream server = CreateServerStream();
                try
                {
                    await server.WaitForConnectionAsync(stoppingToken);
                }
                catch
                {
                    server.Dispose();
                    gate.Release();
                    throw;
                }

                // Service this client on its own task; the loop immediately creates the next listener.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleConnectionAsync(server, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        ServiceLog.Write("Connection error", ex);
                        _logger.LogError(ex, "Connection handling error.");
                    }
                    finally
                    {
                        server.Dispose();
                        gate.Release();
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break; // Service is stopping.
            }
            catch (Exception ex)
            {
                // An accept-side failure must never kill the loop.
                ServiceLog.Write("Accept loop error", ex);
                _logger.LogError(ex, "Accept loop error.");
            }
        }

        ServiceLog.Write("Pipe server worker stopping.");
        _logger.LogInformation("Pipe server worker stopping.");
    }

    /// <summary>
    /// Reads one request line, dispatches it, and writes one response line. Does NOT dispose the
    /// server stream — the caller's finally owns that.
    /// </summary>
    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken stoppingToken)
    {
        ServiceLog.Write("Client connected.");

        // Read timeout: a client that connects but never sends a full line is dropped rather than
        // holding its slot forever.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(RequestTimeoutMs);
        var ct = timeoutCts.Token;

        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true,
        };

        string? line;
        try
        {
            line = await reader.ReadLineAsync(ct);
        }
        catch (OperationCanceledException)
        {
            ServiceLog.Write("Client read timed out (or service stopping); dropping connection.");
            return;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        IpcResponse response;
        try
        {
            var req = IpcJson.Deserialize<IpcRequest>(line)
                ?? throw new InvalidOperationException("Empty request.");
            response = RequestHandler.Handle(req);
            _logger.LogInformation("Handled action {Action}: success={Success}.", req.Action, response.Success);
        }
        catch (Exception ex)
        {
            response = IpcResponse.Fail("Request failed: " + ex.Message);
            ServiceLog.Write("Request error", ex);
            _logger.LogError(ex, "Request handling failed.");
        }

        await writer.WriteLineAsync(IpcJson.Serialize(response));

        // Ensure the client has actually read the response before the caller disposes the pipe;
        // otherwise closing the server end can truncate the in-flight response.
        try { server.WaitForPipeDrain(); }
        catch { /* client may have already read and disconnected */ }
    }

    /// <summary>
    /// Creates a fresh secured named-pipe server stream.
    ///
    /// The service runs as LocalSystem. We grant the local INTERACTIVE logon group (S-1-5-4)
    /// ReadWrite — NOT Authenticated Users. The Interactive SID is present in the token of a user
    /// logged on at the console or over RDP (so the unelevated tray can talk to the elevated service
    /// without per-change UAC prompts), but it is NOT present in a token created by a network (SMB)
    /// logon. A remote caller reaching the pipe over \\host\pipe\MyNetworkTool is therefore denied,
    /// which keeps this privileged interface local-only. Administrators and LocalSystem get full
    /// control for management.
    /// </summary>
    private static NamedPipeServerStream CreateServerStream()
    {
        var security = new PipeSecurity();

        var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
        security.AddAccessRule(new PipeAccessRule(interactive, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            Constants.PipeName, PipeDirection.InOut, MaxConcurrentClients,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            4096, 4096, security);
    }
}
