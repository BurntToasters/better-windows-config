using System.Text;
using Bwc.E2E.Controls;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Harness;

/// <summary>
/// Keeps mutating and E2E-only tests away from the ship publish, and proves before each mutating
/// step that the Registry Editor is looking at the run sandbox. Every guard records its check first
/// so the artifact shows what was observed, then fails the test. A failed guard never counts as a
/// pass and never lets input reach the application.
/// </summary>
public static class E2EGuards
{
    private const string AddressBarId = "AddressBar";
    private const string ComputerLabel = "Computer";
    private const string HkcuLong = "HKEY_CURRENT_USER";
    private const string HkcuShort = "HKCU";
    private static readonly TimeSpan AddressTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RowTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Fails unless the publish is the e2e flavor. Call first in every mutating test.</summary>
    public static void RequireMutatingFlavor(E2EConfig config) =>
        RequireE2EFlavor(config, "Mutating", "guard.flavor.mutating");

    /// <summary>Fails unless the publish is the e2e flavor. Call first in every E2EOnly test.</summary>
    public static void RequireE2EOnlyFlavor(E2EConfig config) =>
        RequireE2EFlavor(config, "E2EOnly", "guard.flavor.e2eonly");

    /// <summary>
    /// Requires that navigation to the run sandbox succeeded and that the address bar confirms it.
    /// Any failure aborts the test before the next step can send input.
    /// </summary>
    public static void RequireSandboxNavigation(
        AppSession session,
        SandboxRegistry sandbox,
        bool navigated,
        string checkId)
    {
        if (!navigated)
        {
            string message = $"Navigation to '{sandbox.RunRootDisplayPath}' failed. No input is sent after a failed navigation.";
            E2EResults.Record(checkId, false, message);
            Assert.Fail(message);
        }

        RequireSandboxSelection(session, sandbox, checkId);
    }

    /// <summary>
    /// Requires the address bar to show the run sandbox root or a key below it. Call right before
    /// each mutating input, not only once after navigation.
    /// </summary>
    public static void RequireSandboxSelection(AppSession session, SandboxRegistry sandbox, string checkId)
    {
        string root = sandbox.RunRootDisplayPath;
        string address = WaitForAddress(session, root, AddressTimeout);
        bool inside = IsWithin(address, root);
        E2EResults.Record(
            checkId,
            inside,
            $"address bar='{address}', required '{root}' or a key below it, inside={inside}.");

        if (!inside)
        {
            Assert.Fail($"Refusing input: the address bar shows '{address}', not '{root}' or a key below it.");
        }
    }

    /// <summary>Requires a value row in the details list to be selected before a value command.</summary>
    public static void RequireListRowSelected(AppSession session, string rowName, string checkId)
    {
        bool selected = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(
                ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.ListControl,
                    ControlType.DataItem,
                    rowName)),
            RowTimeout);
        E2EResults.Record(checkId, selected, $"list row '{rowName}' selected={selected}.");

        if (!selected)
        {
            Assert.Fail($"Refusing input: list row '{rowName}' is not selected.");
        }
    }

    /// <summary>Requires the details-list row with this automation id to be selected before a value command.</summary>
    public static void RequireListRowIdSelected(AppSession session, string automationId, string checkId)
    {
        bool selected = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(session.Find(automationId)),
            RowTimeout);
        E2EResults.Record(checkId, selected, $"list row id '{automationId}' selected={selected}.");

        if (!selected)
        {
            Assert.Fail($"Refusing input: list row id '{automationId}' is not selected.");
        }
    }

    /// <summary>
    /// Requires every key header in a .reg file that is about to be imported to sit at or below the
    /// run sandbox root. The file is the target of a CLI import, so its contents define the write.
    /// </summary>
    public static void RequireImportFileInsideSandbox(string filePath, SandboxRegistry sandbox, string checkId)
    {
        byte[] bytes = File.Exists(filePath) ? File.ReadAllBytes(filePath) : Array.Empty<byte>();
        string text = DecodeRegFile(bytes);
        string root = sandbox.RunRootDisplayPath;

        int headers = 0;
        var outside = new List<string>();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith('['))
            {
                continue;
            }

            headers++;
            string key = line.Trim('[', ']').TrimStart('-');
            if (!IsWithin(key, root))
            {
                outside.Add(key);
            }
        }

        bool inside = headers > 0 && outside.Count == 0;
        E2EResults.Record(
            checkId,
            inside,
            $"import file '{filePath}': key headers={headers}, outside sandbox=[{string.Join(", ", outside)}].");

        if (!inside)
        {
            Assert.Fail($"Refusing import: '{filePath}' has {headers} key header(s) and {outside.Count} outside '{root}'.");
        }
    }

    /// <summary>
    /// True when the address text names the root or a key below it. Accepts the address bar form
    /// (Computer\HKEY_CURRENT_USER\...) and the short form (HKCU\...), case-insensitively. The
    /// comparison needs the trailing separator so a sibling such as "...\run-id-2" is not inside.
    /// </summary>
    internal static bool IsWithin(string addressText, string rootDisplayPath)
    {
        string path = NormalizeKeyPath(addressText);
        return string.Equals(path, rootDisplayPath, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(rootDisplayPath + "\\", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeKeyPath(string text)
    {
        string path = text.Trim().TrimEnd('\\');
        if (string.Equals(path, ComputerLabel, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (path.StartsWith(ComputerLabel + "\\", StringComparison.OrdinalIgnoreCase))
        {
            path = path[(ComputerLabel.Length + 1)..];
        }

        if (string.Equals(path, HkcuLong, StringComparison.OrdinalIgnoreCase))
        {
            return HkcuShort;
        }

        if (path.StartsWith(HkcuLong + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return HkcuShort + path[HkcuLong.Length..];
        }

        return path;
    }

    /// <summary>
    /// Reads the flavor from the app binaries in the publish folder, not from the environment or the flavor the
    /// config chose. A ship folder fails here even when BWC_FLAVOR says e2e.
    /// </summary>
    private static void RequireE2EFlavor(E2EConfig config, string category, string checkId)
    {
        string? problem = FlavorMetadata.E2EProblem(config.PublishDir);
        if (problem is null)
        {
            E2EResults.Record(checkId, true, $"Every app assembly in '{config.PublishDir}' declares {FlavorMetadata.AttributeName}=E2E.");
            return;
        }

        string message =
            $"This test is tagged {category} and runs only against the e2e flavor, but {problem}. " +
            $"Build the e2e flavor with: node scripts/dotnet-build.js --rid {config.Rid} --flavor e2e";
        E2EResults.Record(checkId, false, message);
        Assert.Fail(message);
    }

    private static string WaitForAddress(AppSession session, string root, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string last;
        do
        {
            last = ReadAddress(session);
            if (IsWithin(last, root))
            {
                return last;
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return last;
    }

    private static string ReadAddress(AppSession session)
    {
        AutomationElement? address = session.Find(AddressBarId);
        return address is null ? "(missing)" : session.TextOf(address);
    }

    private static string DecodeRegFile(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return Encoding.ASCII.GetString(bytes);
    }
}
