using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Bwc.Core;

namespace Bwc.Gpedit;

/// <summary>
/// The Help command of the editor. It shows the native About box with the product, the version and the copyright, as the
/// Registry Editor's Help does (declared difference DIFF-006 in docs/parity/gpedit-audit.md).
/// </summary>
internal static class GpeditAbout
{
    private const string ProductFallback = "Better Windows Config";

    /// <summary>Shows the box owned by <paramref name="owner"/>. Returns false when the call failed.</summary>
    internal static bool Show(nint owner, string applicationName, string executablePath)
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(GpeditAbout).Assembly;
        nint icon = string.IsNullOrEmpty(executablePath) ? 0 : ExtractIconW(0, executablePath, 0);
        int result;
        try
        {
            result = ShellAboutW(owner, applicationName, BuildText(assembly), icon);
        }
        finally
        {
            // ExtractIconW returns a copy that the caller owns.
            if (icon != 0)
            {
                _ = DestroyIcon(icon);
            }
        }

        if (result == 0)
        {
            Log.Warn("Local Group Policy Editor could not show the About box.");
            return false;
        }

        return true;
    }

    /// <summary>The lines under the title: product, version and copyright.</summary>
    internal static string BuildText(Assembly assembly)
    {
        string product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? ProductFallback;
        string copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
        return product + "\r\n" +
            GpeditStrings.Commands("GpeditAboutVersion").Replace("{0}", VersionText(assembly), StringComparison.Ordinal) + "\r\n" +
            copyright;
    }

    private static string VersionText(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return (plus < 0 ? informational : informational[..plus]).Trim();
        }

        Version? version = assembly.GetName().Version;
        return version is null
            ? GpeditStrings.Commands("GpeditAboutVersionUnknown")
            : string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Build}");
    }

#pragma warning disable SYSLIB1054 // The project does not allow unsafe code, which LibraryImport needs.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ShellAboutW(nint hwnd, string applicationName, string otherStuff, nint icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint ExtractIconW(nint instance, string file, uint iconIndex);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DestroyIcon(nint icon);
#pragma warning restore SYSLIB1054
}
