using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace OrderSaga.IntegrationTests.Infrastructure;

/// <summary>
/// The real application host (<c>OrderSaga.App.dll</c>, Roles=worker) as a separate OS process, so a test can kill it
/// the way an orchestrator or an out-of-memory killer does: no shutdown hooks, no graceful drain, in-flight work lost.
/// </summary>
public sealed class WorkerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly int _port;

    private WorkerProcess(Process process, int port)
    {
        _process = process;
        _port = port;
    }

    public static async Task<WorkerProcess> StartAsync(SagaEnvironment env, string taskQueue, string? stickyQueueTimeout = null)
    {
        var port = FreePort();
        var info = new ProcessStartInfo("dotnet", $"\"{AppDll()}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment["Roles"] = "worker";
        info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        info.Environment["DOTNET_ENVIRONMENT"] = "Production";
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["ConnectionStrings__Downstream"] = env.AppConnectionString;
        info.Environment["Temporal__Address"] = env.TemporalAddress;
        info.Environment["Temporal__TaskQueue"] = taskQueue;
        info.Environment["Serilog__MinimumLevel__Default"] = "Warning";
        if (stickyQueueTimeout is not null)
        {
            info.Environment["Temporal__StickyQueueTimeout"] = stickyQueueTimeout;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("could not start the worker process");
        // Drain the pipes so a chatty process never blocks on a full buffer.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var worker = new WorkerProcess(process, port);
        await worker.WaitUntilReadyAsync();
        return worker;
    }

    /// <summary>Hard kill: the equivalent of SIGKILL. In-flight activities die with the process.</summary>
    public void Kill()
    {
        _process.Kill(entireProcessTree: true);
        _process.WaitForExit(10_000);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            Kill();
        }

        _process.Dispose();
        await Task.CompletedTask;
    }

    private async Task WaitUntilReadyAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/"), Timeout = TimeSpan.FromSeconds(5) };
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"worker process exited with code {_process.ExitCode}");
            }

            try
            {
                using var response = await http.GetAsync(new Uri("health/ready", UriKind.Relative));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("worker process did not become ready");
    }

    private static string AppDll()
    {
        var configuration = typeof(WorkerProcess).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrderSaga.slnx")))
        {
            dir = dir.Parent;
        }

        var dll = Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found"),
            "src", "OrderSaga.App", "bin", configuration, "net10.0", "OrderSaga.App.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("build the solution first", dll);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
