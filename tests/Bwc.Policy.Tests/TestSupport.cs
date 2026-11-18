using System.Diagnostics;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Tests;

/// <summary>Locations and loaders for the BWC-authored fixture copied next to the test assembly.</summary>
internal static class Fixture
{
    internal const string BaseNamespace = "BWC.Policies.OracleBase";
    internal const string OracleNamespace = "BWC.Policies.Oracle";

    internal static string Root => Path.Combine(AppContext.BaseDirectory, "Fixture");

    internal static string Bad(string name) => Path.Combine(Root, "bad", name);

    internal static TemplateSet Load() => TemplateLoader.Load([Root], "en-US");

    internal static PolicyDefinition Policy(this TemplateSet set, string name) =>
        set.FindPolicy(OracleNamespace, name) ?? throw new AssertFailedException("Fixture policy missing: " + name);

    internal static PolicyCategory Category(this TemplateSet set, string name) =>
        set.FindCategory(OracleNamespace, name) ?? throw new AssertFailedException("Fixture category missing: " + name);

    internal static T Element<T>(this PolicyDefinition policy, string id)
        where T : PolicyElement =>
        policy.Elements.OfType<T>().Single(e => e.Id == id);
}

/// <summary>A scratch template folder under the test output. Disposing removes it, junctions included.</summary>
internal sealed class TempTemplates : IDisposable
{
    internal const string Header = """<?xml version="1.0" encoding="utf-8"?>""";

    private readonly List<string> junctions = [];

    internal TempTemplates()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; }

    internal TemplateSet Load(string? culture = null) => TemplateLoader.Load([Root], culture);

    /// <summary>Writes a file under the folder, creating sub-folders, and returns its full path.</summary>
    internal string Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Writes <paramref name="name"/>.admx and the en-US ADML. The body is the text after the namespaces; the strings
    /// are the string table entries.
    /// </summary>
    internal void Add(string name, string ns, string prefix, string body, string strings, string usings = "",
        string presentations = "", string culture = "en-US")
    {
        Write(name + ".admx", $"""
            {Header}
            <policyDefinitions revision="1.0" schemaVersion="1.0">
              <policyNamespaces>
                <target prefix="{prefix}" namespace="{ns}" />
                {usings}
              </policyNamespaces>
              <resources minRequiredRevision="1.0" />
              {body}
            </policyDefinitions>
            """);
        Write(Path.Combine(culture, name + ".adml"), $"""
            {Header}
            <policyDefinitionResources revision="1.0" schemaVersion="1.0">
              <displayName>Test</displayName>
              <description>Test file</description>
              <resources>
                <stringTable>{strings}</stringTable>
                <presentationTable>{presentations}</presentationTable>
              </resources>
            </policyDefinitionResources>
            """);
    }

    /// <summary>A one-file template with a root category, a supported-on definition and the given policies.</summary>
    internal void AddSimple(string name, string ns, string policies, string extraStrings = "", string extraCategories = "",
        string presentations = "")
    {
        Add(
            name,
            ns,
            "t",
            $"""
            <supportedOn><definitions><definition name="SUP" displayName="$(string.SUP)" /></definitions></supportedOn>
            <categories><category name="Root" displayName="$(string.Root)" />{extraCategories}</categories>
            <policies>{policies}</policies>
            """,
            """<string id="Root">Root</string><string id="SUP">Sup</string>""" + extraStrings,
            presentations: presentations);
    }

    /// <summary>Makes a directory junction (no administrator rights needed) and records it for cleanup.</summary>
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

internal static class FailureAssert
{
    internal static TemplateFailure Single(TemplateSet set, TemplateFailureCode code)
    {
        var matches = set.Failures.Where(f => f.Code == code).ToList();
        Assert.AreEqual(1, matches.Count, "Expected one " + code + " in: " + Describe(set));
        return matches[0];
    }

    internal static void None(TemplateSet set) => Assert.AreEqual(0, set.Failures.Count, Describe(set));

    internal static string Describe(TemplateSet set) =>
        string.Join("; ", set.Failures.Select(f => $"{f.Code} {Path.GetFileName(f.FilePath)} {f.Line}:{f.Position} {f.Detail}"));
}
