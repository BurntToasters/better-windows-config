using System.Text;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

// Start files the runner builds for cases that must not start from an empty folder. Everything here is BWC-authored: fixture key and value
// names, made up GUIDs and made up namespaces.
internal static class Crafted
{
    internal const string Root = @"Software\Policies\BWC-Oracle";

    internal static string K(string suffix) => suffix.Length == 0 ? Root : Root + "\\" + suffix;

    internal const string MachinePol = @"Machine\Registry.pol";
    internal const string UserPol = @"User\Registry.pol";
    internal const string GptIni = "gpt.ini";
    internal const string MachineCmtx = @"Machine\comment.cmtx";
    internal const string UserCmtx = @"User\comment.cmtx";

    // Fake client side extension GUIDs, never used by Windows.
    internal const string FakeGuidOne = "{0BC0BC0B-0000-4000-8000-000000000001}";
    internal const string FakeGuidTwo = "{0BC0BC0B-0000-4000-8000-000000000002}";

    internal static StartState Machine(string description, params PregWriter.Rec[] records) =>
        new(description, new[] { new StartFile(MachinePol, PregWriter.Build(records)) });

    internal static StartState User(string description, params PregWriter.Rec[] records) =>
        new(description, new[] { new StartFile(UserPol, PregWriter.Build(records)) });

    internal static StartState Raw(string description, string name, byte[] bytes) => new(description, new[] { new StartFile(name, bytes) });

    internal static StartState Both(string description, PregWriter.Rec[] machine, PregWriter.Rec[] user) =>
        new(description, new[] { new StartFile(MachinePol, PregWriter.Build(machine)), new StartFile(UserPol, PregWriter.Build(user)) });

    // gpt.ini as text with CRLF line endings, ANSI (ASCII only here).
    internal static StartFile Gpt(params string[] lines) => new(GptIni, Encoding.ASCII.GetBytes(string.Join("\r\n", lines) + "\r\n"));

    internal static StartFile Cmtx(params string[] namespaces)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
        xml.Append("<policyComments xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" revision=\"1.0\" schemaVersion=\"1.0\" xmlns=\"http://www.microsoft.com/GroupPolicy/CommentDefinitions\">\r\n");
        xml.Append("  <policyNamespaces>\r\n");
        for (int i = 0; i < namespaces.Length; i++)
        {
            xml.Append($"    <using prefix=\"bwcfake{i + 1}\" namespace=\"{namespaces[i]}\" />\r\n");
        }

        xml.Append("  </policyNamespaces>\r\n");
        xml.Append("  <comments>\r\n  </comments>\r\n");
        xml.Append("  <resources>\r\n    <stringTable />\r\n  </resources>\r\n");
        xml.Append("</policyComments>");
        byte[] body = new UTF8Encoding(false).GetBytes(xml.ToString());
        return new StartFile(MachineCmtx, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray());
    }

    internal static StartState Files(string description, params StartFile[] files) => new(description, files);
}
