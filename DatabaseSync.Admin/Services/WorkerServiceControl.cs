using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace DatabaseSync.Admin.Services;

public sealed class WorkerServiceStatusInfo
{
    public string ServiceName { get; init; } = WorkerServiceControl.ServiceName;

    public bool IsInstalled { get; init; }

    public bool IsRunning { get; init; }

    /// <summary>True when controlling a Windows Service; false when controlling Worker.exe process.</summary>
    public bool UsesWindowsService { get; init; }

    public string StatusText { get; init; } = "Unknown";

    public string? Message { get; init; }

    public bool CanStart => !IsRunning;

    public bool CanStop => IsRunning;
}

public interface IWorkerServiceControl
{
    WorkerServiceStatusInfo GetStatus();

    WorkerServiceStatusInfo Start();

    WorkerServiceStatusInfo Stop();
}

/// <summary>
/// Controls Coditium.DatabaseSync via Windows Service when installed;
/// otherwise starts/stops DatabaseSync.Worker.exe as a local process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkerServiceControl : IWorkerServiceControl
{
    public const string ServiceName = "Coditium.DatabaseSync";
    private const string WorkerProcessName = "DatabaseSync.Worker";

    public WorkerServiceStatusInfo GetStatus()
    {
        var serviceStatus = TryGetServiceStatus();
        if (serviceStatus.IsInstalled)
        {
            return serviceStatus;
        }

        var running = IsWorkerProcessRunning();
        return new WorkerServiceStatusInfo
        {
            IsInstalled = true, // treat as controllable via process mode
            UsesWindowsService = false,
            IsRunning = running,
            StatusText = running ? "Running (process)" : "Stopped (process)",
            Message = $"Windows service not installed. Controlling {WorkerProcessName}.exe directly."
        };
    }

    public WorkerServiceStatusInfo Start()
    {
        var serviceStatus = TryGetServiceStatus();
        if (serviceStatus.IsInstalled)
        {
            return StartWindowsService();
        }

        return StartWorkerProcess();
    }

    public WorkerServiceStatusInfo Stop()
    {
        var serviceStatus = TryGetServiceStatus();
        if (serviceStatus.IsInstalled)
        {
            return StopWindowsService();
        }

        return StopWorkerProcess();
    }

    private static WorkerServiceStatusInfo TryGetServiceStatus()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            var status = controller.Status;

            return new WorkerServiceStatusInfo
            {
                IsInstalled = true,
                UsesWindowsService = true,
                IsRunning = status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending,
                StatusText = status.ToString(),
                Message = null
            };
        }
        catch (InvalidOperationException)
        {
            return new WorkerServiceStatusInfo
            {
                IsInstalled = false,
                UsesWindowsService = false,
                IsRunning = false,
                StatusText = "NotInstalled"
            };
        }
        catch (Exception ex)
        {
            return new WorkerServiceStatusInfo
            {
                IsInstalled = false,
                UsesWindowsService = false,
                IsRunning = false,
                StatusText = "Error",
                Message = ex.Message
            };
        }
    }

    private WorkerServiceStatusInfo StartWindowsService()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
            {
                var current = GetStatus();
                return CloneWithMessage(current, "Service is already running.");
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            return CloneWithMessage(GetStatus(), "Sync service started.");
        }
        catch (Exception ex)
        {
            return Error($"Could not start Windows service. Run Admin as Administrator. Details: {ex.Message}");
        }
    }

    private WorkerServiceStatusInfo StopWindowsService()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            {
                return CloneWithMessage(GetStatus(), "Service is already stopped.");
            }

            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"config {ServiceName} start= demand",
                    CreateNoWindow = true,
                    UseShellExecute = false
                })?.WaitForExit(5000);
            }
            catch
            {
                // best effort
            }

            return CloneWithMessage(GetStatus(), "Sync service stopped.");
        }
        catch (Exception ex)
        {
            return Error($"Could not stop Windows service. Run Admin as Administrator. Details: {ex.Message}");
        }
    }

    private WorkerServiceStatusInfo StartWorkerProcess()
    {
        try
        {
            if (IsWorkerProcessRunning())
            {
                return CloneWithMessage(GetStatus(), "Worker process is already running.");
            }

            var exePath = ResolveWorkerExePath();
            if (exePath is null)
            {
                return Error($"Could not find {WorkerProcessName}.exe. Expected next to Admin or in parent folder (live_sync_dbs).");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            });

            // Brief wait for process to appear.
            Thread.Sleep(1000);
            return CloneWithMessage(GetStatus(), $"Started {WorkerProcessName}.exe");
        }
        catch (Exception ex)
        {
            return Error($"Could not start worker process: {ex.Message}");
        }
    }

    private WorkerServiceStatusInfo StopWorkerProcess()
    {
        try
        {
            var processes = Process.GetProcessesByName(WorkerProcessName);
            if (processes.Length == 0)
            {
                return CloneWithMessage(GetStatus(), "Worker process is already stopped.");
            }

            foreach (var process in processes)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(15000);
                }
                finally
                {
                    process.Dispose();
                }
            }

            return CloneWithMessage(GetStatus(), $"Stopped {WorkerProcessName}.exe");
        }
        catch (Exception ex)
        {
            return Error($"Could not stop worker process: {ex.Message}");
        }
    }

    private static bool IsWorkerProcessRunning() =>
        Process.GetProcessesByName(WorkerProcessName).Length > 0;

    private static string? ResolveWorkerExePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, $"{WorkerProcessName}.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", $"{WorkerProcessName}.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", $"{WorkerProcessName}.exe"))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static WorkerServiceStatusInfo CloneWithMessage(WorkerServiceStatusInfo status, string message) =>
        new()
        {
            IsInstalled = status.IsInstalled,
            UsesWindowsService = status.UsesWindowsService,
            IsRunning = status.IsRunning,
            StatusText = status.StatusText,
            Message = message
        };

    private static WorkerServiceStatusInfo Error(string message) =>
        new()
        {
            IsInstalled = false,
            UsesWindowsService = false,
            IsRunning = false,
            StatusText = "Error",
            Message = message
        };
}
