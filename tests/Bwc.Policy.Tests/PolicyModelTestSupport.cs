using System.Text;
using Bwc.Gpo.Pol;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests;

/// <summary>Helpers for the policy state tests: fixture policies and an independent Registry.pol byte builder.</summary>
internal static class PolicyModelTestSupport
{
    internal const string K = @"Software\Policies\BWC-Oracle";
    internal const string Basics = K + @"\Basics";
    internal const string Shared = K + @"\Shared";
    internal const string Bool = K + @"\Elements\Bool";
    internal const string Number = K + @"\Elements\Number";
    internal const string Text = K + @"\Elements\Text";
    internal const string Choice = K + @"\Elements\Choice";
    internal const string Lists = K + @"\Lists";

    private static readonly Lazy<TemplateSet> Set = new(Fixture.Load);

    internal static PolicyDefinition P(string name) => Set.Value.Policy(name);

    internal static PolDocument NewDocument() => PolDocument.Create();

    /// <summary>Serializes and parses again, so the baseline of the new document is the current content.</summary>
    internal static PolDocument Reload(PolDocument document) => PolDocument.Parse(document.ToBytes());

    internal static byte[] Dw(uint value) => BitConverter.GetBytes(value);

    internal static byte[] Qw(ulong value) => BitConverter.GetBytes(value);

    internal static byte[] Sz(string text) => Encoding.Unicode.GetBytes(text + "\0");

    internal static byte[] Multi(params string[] lines) => Encoding.Unicode.GetBytes(string.Concat(lines.Select(l => l + "\0")) + "\0");

    internal static byte[] Rec(string key, string name, uint type, byte[] data)
    {
        using var stream = new MemoryStream();
        void Ch(char c)
        {
            stream.WriteByte((byte)(c & 0xFF));
            stream.WriteByte((byte)(c >> 8));
        }

        void Str(string s)
        {
            foreach (var c in s)
            {
                Ch(c);
            }

            Ch('\0');
        }

        Ch('[');
        Str(key);
        Ch(';');
        Str(name);
        Ch(';');
        stream.Write(BitConverter.GetBytes(type));
        Ch(';');
        stream.Write(BitConverter.GetBytes((uint)data.Length));
        Ch(';');
        stream.Write(data);
        Ch(']');
        return stream.ToArray();
    }

    internal static byte[] RecDw(string key, string name, uint value) => Rec(key, name, 4, Dw(value));

    internal static byte[] RecSz(string key, string name, string value) => Rec(key, name, 1, Sz(value));

    internal static byte[] Del(string key, string name) => Rec(key, "**del." + name, 1, [0x20, 0x00, 0x00, 0x00]);

    internal static byte[] Pol(params byte[][] records)
    {
        using var stream = new MemoryStream();
        stream.Write([0x50, 0x52, 0x65, 0x67, 0x01, 0x00, 0x00, 0x00]);
        foreach (var record in records)
        {
            stream.Write(record);
        }

        return stream.ToArray();
    }

    internal static PolDocument Parse(params byte[][] records) => PolDocument.Parse(Pol(records));

    internal static void AssertBytes(PolDocument document, params byte[][] records) =>
        CollectionAssert.AreEqual(Pol(records), document.ToBytes());

    internal static PolicyApplyResult Apply(string policy, PolDocument document, PolicyEdit edit) =>
        PolicyStateWriter.Apply(P(policy), document, edit);

    internal static PolicyStateSnapshot Read(string policy, PolDocument document) =>
        PolicyStateReader.Read(P(policy), document);

    /// <summary>
    /// Applies an edit, checks the apply is valid, then applies it again on a reloaded copy and checks that nothing changes.
    /// Returns the document after the first apply.
    /// </summary>
    internal static PolDocument ApplyTwice(string policy, PolicyEdit edit, PolDocument? start = null)
    {
        var document = start ?? NewDocument();
        var first = Apply(policy, document, edit);
        Assert.IsTrue(first.IsValid, "First apply: " + string.Join(", ", first.Problems));
        var again = Reload(document);
        var second = Apply(policy, again, edit);
        Assert.IsTrue(second.IsValid);
        Assert.IsFalse(second.Changed, "A repeated apply must change nothing.");
        Assert.IsFalse(again.HasChanges, "A repeated apply must leave HasChanges false.");
        return document;
    }
}
