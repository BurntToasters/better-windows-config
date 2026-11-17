using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Bwc.E2E.Harness;

/// <summary>
/// Writes values of an exact registry type through the product's own silent import (BwcRegedit.exe /s), under the
/// sandbox run root. The managed API cannot write REG_NONE, REG_DWORD_BIG_ENDIAN, REG_LINK, the resource types, or
/// unknown type numbers, and the native write APIs are outside the registry-writer gate. The import file is written
/// under artifacts/e2e/reg-seed, and E2EGuards checks that every key in it is inside the run root.
/// </summary>
internal static class RegFileSeed
{
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromSeconds(60);

    /// <summary>One value to write: its name, its raw registry type number, and its exact bytes.</summary>
    internal sealed record SeedValue(string Name, uint Type, byte[] Data);

    /// <summary>
    /// Imports the values into HKCU\&lt;run root&gt;\&lt;childKey&gt; with the product's silent import. Records one check
    /// and returns true when the import exited with 0.
    /// </summary>
    internal static bool Import(
        E2EConfig config,
        SandboxRegistry sandbox,
        string childKey,
        string fileName,
        IReadOnlyList<SeedValue> values)
    {
        string dir = Path.Combine(config.ArtifactsDir, "reg-seed", config.RunId);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, fileName);

        var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
        _ = text.Append("[HKEY_CURRENT_USER\\").Append(sandbox.RunRootPath).Append('\\').Append(childKey).Append("]\r\n");
        foreach (SeedValue value in values)
        {
            string bytes = string.Join(',', value.Data.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            _ = text.Append('"').Append(value.Name).Append("\"=hex(")
                .Append(value.Type.ToString("x", CultureInfo.InvariantCulture)).Append("):").Append(bytes).Append("\r\n");
        }

        byte[] body = Encoding.Unicode.GetBytes(text.ToString());
        File.WriteAllBytes(file, [0xFF, 0xFE, .. body]);
        E2EGuards.RequireImportFileInsideSandbox(file, sandbox, "reg-seed." + fileName);

        // The import creates keys only inside the run root, so the root is created first, as the other seed paths do.
        sandbox.CreateKey(string.Empty);
        var startInfo = new ProcessStartInfo(config.RegeditExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = config.PublishDir,
        };
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add(file);
        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        startInfo.Environment[E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("reg-seed");
        startInfo.Environment["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath;

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned no process for '{config.RegeditExePath}'.");
        bool exited = process.WaitForExit((int)ImportTimeout.TotalMilliseconds);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(5000);
        }

        int exitCode = exited ? process.ExitCode : -1;
        bool ok = exited && exitCode == 0;
        E2EResults.Record(
            "reg-seed." + fileName,
            ok,
            $"import into '{childKey}' exited={exited}, exit={exitCode}, values={values.Count}.");
        return ok;
    }
}
