using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Bwc.Policy.Store;
using Bwc.Update;

namespace Bwc.Policy.Tests;

/// <summary>
/// A throw-away tree under the temp folder with a system folder, an import store folder and source folders. Nothing in
/// it is a real template folder and nothing outside the temp folder is written.
/// </summary>
internal sealed class TemplateStoreSandbox : IDisposable
{
    internal const string Header = """<?xml version="1.0" encoding="utf-8"?>""";

    private readonly List<string> junctions = [];

    internal TemplateStoreSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "bwc-e5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        SystemFolder = Path.Combine(Root, "sys");
        StoreFolder = Path.Combine(Root, "store");
        Directory.CreateDirectory(SystemFolder);
    }

    internal string Root { get; }

    internal string SystemFolder { get; }

    internal string StoreFolder { get; }

    internal ProtectedAcl Acl { get; } = ProtectedAcl.ForCurrentUser();

    internal TemplateSources Sources() => new(SystemFolder, StoreFolder, Acl);

    internal TemplateImport Importer() => new(Sources());

    internal TemplateStoreWriter Writer() => new(StoreFolder, SystemFolder, Acl);

    /// <summary>A new folder under the sandbox.</summary>
    internal string Folder(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string AdmxText(string ns, string prefix, string usings = "", string policies = "") => $"""
        {Header}
        <policyDefinitions revision="1.0" schemaVersion="1.0">
          <policyNamespaces>
            <target prefix="{prefix}" namespace="{ns}" />
            {usings}
          </policyNamespaces>
          <resources minRequiredRevision="1.0" />
          <supportedOn><definitions><definition name="SUP" displayName="$(string.SUP)" /></definitions></supportedOn>
          <categories><category name="Root" displayName="$(string.Root)" /></categories>
          <policies>{policies}</policies>
        </policyDefinitions>
        """;

    internal static string AdmlText(string strings = "") => $"""
        {Header}
        <policyDefinitionResources revision="1.0" schemaVersion="1.0">
          <displayName>Test</displayName>
          <description>Test file</description>
          <resources>
            <stringTable><string id="Root">Root</string><string id="SUP">Sup</string>{strings}</stringTable>
            <presentationTable />
          </resources>
        </policyDefinitionResources>
        """;

    internal static string PolicyText(string name) =>
        $"""<policy name="{name}" class="Machine" displayName="$(string.{name})" key="Software\Policies\BwcStore" valueName="{name}" supportedOn="t:SUP"><parentCategory ref="Root" /></policy>""";

    /// <summary>Writes a good template and its ADML under a folder and returns the ADMX path.</summary>
    internal string AddTemplate(string folder, string fileName, string ns, string usings = "", string policy = "P1", string culture = "en-US")
    {
        Directory.CreateDirectory(Path.Combine(folder, culture));
        var admx = Path.Combine(folder, fileName + ".admx");
        File.WriteAllText(admx, AdmxText(ns, "t", usings, PolicyText(policy)));
        File.WriteAllText(Path.Combine(folder, culture, fileName + ".adml"), AdmlText($"""<string id="{policy}">{policy}</string>"""));
        return admx;
    }

    /// <summary>Makes a directory junction (no administrator rights needed) and removes it on dispose.</summary>
    internal void MakeJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/J");
        info.ArgumentList.Add(link);
        info.ArgumentList.Add(target);
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, "mklink /J failed");
        junctions.Add(link);
    }

    /// <summary>Gives Everyone write access to a folder, which no protected store may have.</summary>
    internal static void GrantEveryoneWrite(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.WriteData | FileSystemRights.AppendData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    internal static IReadOnlyList<string> FilesBelow(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(folder, p)).Order(StringComparer.Ordinal).ToArray()
            : [];

    public void Dispose()
    {
        foreach (var junction in junctions)
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction, recursive: false);
            }
        }

        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
