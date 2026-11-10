using Microsoft.UI.Xaml;

namespace Bwc.Spike.UI;

/// <summary>
/// Command-line options shared by both heads:
///   --exit-code N        process exit code returned when the app ends (default 0)
///   --auto-close-ms N    close the window N ms after launch, so the process ends by itself
/// </summary>
public sealed class SpikeOptions
{
    public static SpikeOptions Current { get; private set; } = new();

    public int ExitCode { get; private set; }
    public int? AutoCloseMs { get; private set; }

    /// <summary>
    /// Reads the options from the process command line. Heads call this from App's constructor
    /// (the WinUI-generated Main ignores args) and copy ExitCode into Environment.ExitCode, which
    /// the generated void Main returns to the OS when Application.Start returns.
    /// </summary>
    public static SpikeOptions FromCommandLine()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var options = new SpikeOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--exit-code":
                    options.ExitCode = ReadInt(args, ref i, "--exit-code");
                    break;
                case "--auto-close-ms":
                    options.AutoCloseMs = ReadInt(args, ref i, "--auto-close-ms");
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        Current = options;
        Environment.ExitCode = options.ExitCode;
        return options;
    }

    /// <summary>Closes the window after AutoCloseMs (if set) and ends the app when the window closes.</summary>
    public void Attach(Window window)
    {
        window.Closed += (_, _) => Application.Current.Exit();
        if (AutoCloseMs is int ms)
        {
            var timer = window.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(ms);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => window.Close();
            timer.Start();
        }
    }

    private static int ReadInt(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var value) || value < 0)
            throw new ArgumentException($"{name} needs a non-negative integer value");
        i++;
        return value;
    }
}
