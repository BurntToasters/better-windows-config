using System.Diagnostics;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// What a session body gets: the paths, a cancellation token that Ctrl+C sets, and the only way to start mmc.exe.
internal sealed class GpeditContext
{
    private readonly Manifest manifest;

    internal GpeditContext(GpeditPaths paths, string name, Manifest manifest, CancellationToken token)
    {
        Paths = paths;
        Name = name;
        this.manifest = manifest;
        Token = token;
    }

    internal GpeditPaths Paths { get; }

    internal string Name { get; }

    internal CancellationToken Token { get; }

    internal void Log(string message) => Console.WriteLine($"{Name}: {message}");

    internal void ThrowIfCancelled() => Token.ThrowIfCancellationRequested();

    // Starts mmc.exe with UseShellExecute false, so it inherits this process's elevation and no UAC prompt can appear. The process id and
    // start time go into the manifest on disk before this returns, so `gpedit restore` can close it after a crash.
    internal Process StartMmc(string consolePath)
    {
        if (Paths.Sandbox)
        {
            throw new InvalidOperationException("the self test never starts mmc.exe");
        }

        ThrowIfCancelled();
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mmc.exe"))
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(consolePath);
        Process process = Process.Start(psi) ?? throw new InvalidOperationException("mmc.exe did not start.");
        RegisterProcess(process, process.StartTime.ToUniversalTime().Ticks);
        Log($"started mmc.exe pid {process.Id}");
        return process;
    }

    // Records a process in the manifest on disk so restore can close it. startTicks is the process start time in UTC ticks.
    internal void RegisterProcess(Process process, long startTicks)
    {
        manifest.Processes.Add(new StartedProcess { Pid = process.Id, Name = process.ProcessName, StartUtcTicks = startTicks });
        manifest.Write(Paths.ManifestPath);
    }
}
