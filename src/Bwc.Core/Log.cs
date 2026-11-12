using System.Globalization;
using System.Text;

namespace Bwc.Core;

public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
}

public sealed class LogOptions
{
    /// <summary>Gets or sets the log directory. When null, the per-tool directory is used.</summary>
    public string? Directory { get; init; }

    /// <summary>Gets or sets the maximum size of one log file before a part file is used.</summary>
    public long MaxFileBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Gets or sets how many days log files are kept.</summary>
    public int RetainDays { get; init; } = 7;

    /// <summary>Gets or sets the lowest level that is written.</summary>
    public LogLevel MinimumLevel { get; init; } = LogLevel.Info;
}

/// <summary>
/// Process-wide, thread-safe file logger. Every public member is non-throwing by design; a
/// logger failure falls back to the internal safe trace helper. Each entry is written through a
/// short-lived append handle so callers can read <see cref="CurrentFile"/> as soon as the call
/// returns.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    private static string? _toolId;
    private static LogOptions _options = new();
    private static DateOnly _currentDay;

    /// <summary>Gets the last initialized log directory, or null when never initialized.</summary>
    public static string? Directory { get; private set; }

    /// <summary>Gets the file currently receiving entries, or null when no file is selected.</summary>
    public static string? CurrentFile { get; private set; }

    public static void Initialize(string toolId, LogOptions? options = null)
    {
        try
        {
            AppPaths.ValidateToolId(toolId);
            LogOptions settings = options ?? new LogOptions();
            if (settings.MaxFileBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxFileBytes must be positive.");
            }

            if (settings.RetainDays < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "RetainDays must be zero or greater.");
            }

            string directory = ResolveDirectory(toolId, settings.Directory);

            lock (Gate)
            {
                _toolId = toolId;
                _options = settings;
                Directory = directory;
                CurrentFile = null;

                System.IO.Directory.CreateDirectory(directory);
                _currentDay = DateOnly.FromDateTime(DateTime.UtcNow);
                DeleteExpiredLocked(directory, settings.RetainDays);

                string path = DailyPath(directory, _currentDay, 0);
                EnsureWritable(path);
                CurrentFile = path;
            }
        }
        catch (Exception ex)
        {
            SafeTrace.WriteLine("Log.Initialize failed: " + ex);
            lock (Gate)
            {
                _toolId = null;
                Directory = null;
                CurrentFile = null;
            }
        }
    }

    private static string ResolveDirectory(string toolId, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return AppPaths.LogDirectory(toolId);
        }

        try
        {
            if (Path.IsPathFullyQualified(configured))
            {
                return Path.GetFullPath(configured);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SafeTrace.WriteLine("Log directory '" + configured + "' is invalid: " + ex.Message);
            return AppPaths.LogDirectory(toolId);
        }

        SafeTrace.WriteLine(
            "Log directory '" + configured + "' is not absolute; using the per-tool directory instead.");
        return AppPaths.LogDirectory(toolId);
    }

    public static void Info(string message)
    {
        Write(LogLevel.Info, message, null);
    }

    public static void Warn(string message)
    {
        Write(LogLevel.Warn, message, null);
    }

    public static void Error(string message)
    {
        Write(LogLevel.Error, message, null);
    }

    public static void Exception(string message, System.Exception? ex = null)
    {
        Write(LogLevel.Error, message, ex);
    }

    /// <summary>Stops writing and allows a later <see cref="Initialize"/> call.</summary>
    public static void Shutdown()
    {
        try
        {
            lock (Gate)
            {
                _toolId = null;
            }
        }
        catch (Exception ex)
        {
            SafeTrace.WriteLine("Log.Shutdown failed: " + ex);
        }
    }

    private static void Write(LogLevel level, string message, System.Exception? ex)
    {
        if (level < _options.MinimumLevel)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (_toolId is null || Directory is null || CurrentFile is null)
                {
                    SafeTrace.WriteLine(FormatEntry("unknown", level, message, ex));
                    return;
                }

                DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (today != _currentDay)
                {
                    _currentDay = today;
                    DeleteExpiredLocked(Directory, _options.RetainDays);
                    string daily = DailyPath(Directory, today, 0);
                    EnsureWritable(daily);
                    CurrentFile = daily;
                }

                string text = FormatEntry(_toolId, level, message, ex);
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                string file = CurrentFile;
                long length = GetFileLength(file);
                if (length > 0 && length + bytes.Length > _options.MaxFileBytes)
                {
                    file = FindNextPart(Directory, _currentDay);
                    CurrentFile = file;
                }

                using var stream = new FileStream(
                    file,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }
        catch (Exception writeError)
        {
            SafeTrace.WriteLine("Log write failed: " + writeError);
            try
            {
                SafeTrace.WriteLine(FormatEntry("unknown", level, message, ex));
            }
            catch (Exception traceError)
            {
                SafeTrace.WriteLine("Log trace fallback failed: " + traceError.Message);
            }
        }
    }

    private static string FormatEntry(string toolId, LogLevel level, string message, System.Exception? ex)
    {
        var builder = new StringBuilder(160);
        builder.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(LevelText(level));
        builder.Append(' ');
        builder.Append(toolId);
        builder.Append(" [thread ");
        builder.Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
        builder.Append("] ");
        builder.Append(message ?? string.Empty);

        if (ex is not null)
        {
            builder.Append(Environment.NewLine);
            builder.Append(ex.GetType().FullName ?? ex.GetType().Name);
            builder.Append(": ");
            builder.Append(ex.Message);
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                builder.Append(Environment.NewLine);
                builder.Append(ex.StackTrace);
            }
        }

        builder.Append(Environment.NewLine);
        return builder.ToString();
    }

    private static string LevelText(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERROR",
            _ => "INFO",
        };
    }

    private static void EnsureWritable(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
    }

    private static long GetFileLength(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string FindNextPart(string directory, DateOnly day)
    {
        int part = 1;
        while (true)
        {
            string candidate = DailyPath(directory, day, part);
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            part++;
        }
    }

    private static string DailyPath(string directory, DateOnly day, int part)
    {
        string stamp = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string name = part == 0
            ? "log-" + stamp + ".txt"
            : "log-" + stamp + "." + part.ToString(CultureInfo.InvariantCulture) + ".txt";
        return Path.Combine(directory, name);
    }

    private static void DeleteExpiredLocked(string directory, int retainDays)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-retainDays);
            foreach (string path in System.IO.Directory.EnumerateFiles(
                directory,
                "log-*.txt",
                SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SafeTrace.WriteLine("Log retention could not delete '" + path + "': " + ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SafeTrace.WriteLine("Log retention scan failed for '" + directory + "': " + ex.Message);
        }
    }
}
