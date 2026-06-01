using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using NetworkingTool.Shared;

namespace NetworkingTool.Tray;

/// <summary>
/// The only way the unelevated UI reaches the elevated service. It connects to the local
/// named pipe, writes one line of JSON for the request, and reads one line of JSON for the
/// response. All failure modes are converted into a failed <see cref="IpcResponse"/> so the
/// UI never has to catch exceptions from here.
/// </summary>
public static class PipeClient
{
    public static async Task<IpcResponse> SendAsync(IpcRequest request)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", Constants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await client.ConnectAsync(Constants.PipeConnectTimeoutMs);
            }
            catch (TimeoutException)
            {
                return IpcResponse.Fail("Could not reach the MyNetworkTool service. Is it installed and running?");
            }
            catch (IOException)
            {
                return IpcResponse.Fail("Could not reach the MyNetworkTool service. Is it installed and running?");
            }

            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(IpcJson.Serialize(request));
            string? line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) return IpcResponse.Fail("Empty response from service.");
            return IpcJson.Deserialize<IpcResponse>(line) ?? IpcResponse.Fail("Malformed response from service.");
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail("IPC error: " + ex.Message);
        }
    }
}
