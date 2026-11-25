using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit.Probe;

// Finds the probe's own ADMX and ADML: the copy next to the executable when the build copied it, else the one in the source tree.
internal static class ProbeTemplate
{
    internal const string AdmxName = "BwcProbe.admx";

    internal static string Locate(RepoPaths repo)
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Gpedit", "Probe", "Template"),
            Path.Combine(repo.Root, "tools", "Bwc.Oracle.Capture", "Gpedit", "Probe", "Template"),
        };
        foreach (string dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, AdmxName)) && File.Exists(Path.Combine(dir, "en-US", "BwcProbe.adml")))
            {
                return dir;
            }
        }

        throw new FileNotFoundException("The probe template (BwcProbe.admx and en-US\\BwcProbe.adml) was not found next to the executable or in tools/Bwc.Oracle.Capture/Gpedit/Probe/Template.");
    }

    internal static TemplateSet Load(RepoPaths repo) => TemplateSet.FromDirectory(Locate(repo));
}
