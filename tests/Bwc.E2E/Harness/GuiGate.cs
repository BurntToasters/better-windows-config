using Bwc.E2E.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Harness;

/// <summary>
/// Assembly level serialization for GUI tests. The named mutex is acquired on a dedicated thread
/// because Windows mutex ownership is thread affine, then released from that same thread during
/// cleanup. Assembly initialization also prepares the result artifact and sweeps stale sandboxes.
/// </summary>
[TestClass]
public sealed class GuiGate
{
    private const string GateName = @"Local\BwcGuiTestLock";
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromMinutes(15);

    private static Thread? _gateThread;
    private static ManualResetEventSlim? _acquired;
    private static ManualResetEventSlim? _release;
    private static Exception? _gateError;
    private static E2EConfig? _config;
    private static SandboxRegistry? _sandbox;
    private static bool _resultsStarted;
    private static volatile bool _isHeld;
    private static DateTime _runStartedUtc;

    public static bool IsHeld => _isHeld;

    public static E2EConfig Config =>
        _config ?? throw new InvalidOperationException("The E2E assembly did not initialize.");

    [AssemblyInitialize]
    public static void AssemblyInit(TestContext testContext)
    {
        _config = E2EConfig.Create();
        E2EResults.Begin(_config);
        _resultsStarted = true;
        _runStartedUtc = DateTime.UtcNow;

        _sandbox = new SandboxRegistry(_config);
        try
        {
            _sandbox.SweepStale(TimeSpan.FromHours(24));
            E2EResults.Note("sandbox.sweep", "Removed only stale matching run keys.");
        }
        catch (Exception ex)
        {
            E2EResults.Record("sandbox.sweep", false, ex.Message);
        }

        AcquireGate();
    }

    [AssemblyCleanup]
    public static void AssemblyCleanup()
    {
        if (_sandbox is not null)
        {
            try
            {
                // Removes this run's subtree, then the BWC-E2E parent only when it is empty.
                ControlTestSupport.CleanupRegistry(Config);
                E2EResults.Note("sandbox.cleanup", $"Removed {_sandbox.RunRootDisplayPath}.");
            }
            catch (Exception ex)
            {
                E2EResults.Record("sandbox.cleanup", false, ex.Message);
            }
        }

        try
        {
            if (_resultsStarted && _config is not null)
            {
                RecordStrayRegFiles(_config);
            }
        }
        finally
        {
            try
            {
                if (_resultsStarted)
                {
                    E2EResults.Finish();
                }
            }
            finally
            {
                ReleaseGate();
            }
        }
    }

    /// <summary>
    /// Fails when a .reg file was created or changed in a user folder during this run. The suite must
    /// write every file under artifacts/e2e, so a save dialog that falls back to a default folder shows
    /// up here. Files older than the run are not counted.
    /// </summary>
    private static void RecordStrayRegFiles(E2EConfig config)
    {
        var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                config.RepoRoot,
                config.PublishDir,
            }
            .Where(folder => !string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var since = _runStartedUtc.AddSeconds(-1);
        var strays = new List<string>();
        foreach (string folder in folders)
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.reg", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(file);
                if (info.CreationTimeUtc >= since || info.LastWriteTimeUtc >= since)
                {
                    strays.Add(file);
                }
            }
        }

        E2EResults.Record(
            "files.outside-artifacts",
            strays.Count == 0,
            strays.Count == 0
                ? $"No .reg file was written during the run in {folders.Length} folders."
                : $"Written outside artifacts/e2e during the run: {string.Join(", ", strays)}.");
    }

    private static void AcquireGate()
    {
        _acquired = new ManualResetEventSlim(initialState: false);
        _release = new ManualResetEventSlim(initialState: false);
        _gateThread = new Thread(GateThread)
        {
            IsBackground = true,
            Name = "BwcGuiTestGate",
        };
        _gateThread.Start();

        if (!_acquired.Wait(AcquireTimeout))
        {
            _release.Set();
            var message = $"Timed out after {AcquireTimeout.TotalMinutes:0} minutes waiting for {GateName}. " +
                          "Another GUI test assembly is still running.";
            E2EResults.Record("gate.acquire", false, message);
            throw new TimeoutException(message);
        }

        if (_gateError is not null)
        {
            E2EResults.Record("gate.acquire", false, _gateError.Message);
            throw new InvalidOperationException($"Could not acquire {GateName}.", _gateError);
        }

        E2EResults.Note("gate.acquire", $"Acquired {GateName}.");
    }

    private static void GateThread()
    {
        var owned = false;
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, GateName);
            try
            {
                try
                {
                    owned = mutex.WaitOne(AcquireTimeout);
                }
                catch (AbandonedMutexException)
                {
                    // The previous holder exited without releasing. Ownership moved to this thread.
                    owned = true;
                }
            }
            catch (Exception ex)
            {
                _gateError = ex;
                _acquired?.Set();
                return;
            }

            if (!owned)
            {
                _gateError = new TimeoutException(
                    $"Timed out after {AcquireTimeout.TotalMinutes:0} minutes waiting for {GateName}.");
                _acquired?.Set();
                return;
            }

            _isHeld = true;
            _acquired?.Set();
            _release?.Wait();

            mutex.ReleaseMutex();
            owned = false;
        }
        catch (Exception ex)
        {
            _gateError ??= ex;
            _acquired?.Set();
        }
        finally
        {
            _isHeld = false;
        }
    }

    private static void ReleaseGate()
    {
        _release?.Set();
        if (_gateThread is not null && _gateThread.IsAlive)
        {
            _gateThread.Join(TimeSpan.FromSeconds(10));
        }

        _isHeld = false;
        if (_gateThread is null || !_gateThread.IsAlive)
        {
            _acquired?.Dispose();
            _release?.Dispose();
            _acquired = null;
            _release = null;
            _gateThread = null;
        }
    }
}
