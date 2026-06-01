using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkingTool.Shared;

namespace NetworkingTool.Service;

/// <summary>
/// The single integration point the rest of the app uses to launch the Windows Service host.
/// Program.cs routes <c>MyNetworkTool.exe service</c> here; the SCM starts that as LocalSystem.
/// </summary>
public static class ServiceEntry
{
    public static int Run(string[] args)
    {
        try
        {
            var builder = Host.CreateApplicationBuilder(args);

            builder.Services.AddWindowsService(options => options.ServiceName = Constants.ServiceName);

            // Keep logging simple. The EventLog provider is added automatically by AddWindowsService;
            // the console sink helps when running the host interactively for debugging.
            builder.Logging.AddSimpleConsole();

            builder.Services.AddHostedService<PipeServerWorker>();

            var host = builder.Build();

            ServiceLog.Write("Service host starting");
            host.Run();
            return 0;
        }
        catch (Exception ex)
        {
            ServiceLog.Write("Fatal", ex);
            return 1;
        }
    }
}
