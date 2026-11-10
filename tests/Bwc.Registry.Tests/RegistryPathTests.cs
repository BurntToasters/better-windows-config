using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

[TestClass]
public sealed class RegistryPathTests
{
    [TestMethod]
    public void ParseAcceptsFullNamesAndAbbreviations()
    {
        var cases = new (string Text, RegistryRoot Root, string Display, string Abbreviated)[]
        {
            ("HKCR\\Foo", RegistryRoot.ClassesRoot, "Computer\\HKEY_CLASSES_ROOT\\Foo", "HKCR\\Foo"),
            ("HKCU\\Software\\Foo", RegistryRoot.CurrentUser, "Computer\\HKEY_CURRENT_USER\\Software\\Foo", "HKCU\\Software\\Foo"),
            ("HKLM\\Software\\Foo", RegistryRoot.LocalMachine, "Computer\\HKEY_LOCAL_MACHINE\\Software\\Foo", "HKLM\\Software\\Foo"),
            ("HKU\\S-1-5-21\\Foo", RegistryRoot.Users, "Computer\\HKEY_USERS\\S-1-5-21\\Foo", "HKU\\S-1-5-21\\Foo"),
            ("HKCC\\System\\Foo", RegistryRoot.CurrentConfig, "Computer\\HKEY_CURRENT_CONFIG\\System\\Foo", "HKCC\\System\\Foo"),
            ("HKEY_CURRENT_USER\\Software\\Foo", RegistryRoot.CurrentUser, "Computer\\HKEY_CURRENT_USER\\Software\\Foo", "HKCU\\Software\\Foo"),
            ("hkey_local_machine\\Software\\Foo", RegistryRoot.LocalMachine, "Computer\\HKEY_LOCAL_MACHINE\\Software\\Foo", "HKLM\\Software\\Foo"),
            ("Computer\\HKEY_CURRENT_USER\\Software\\Foo", RegistryRoot.CurrentUser, "Computer\\HKEY_CURRENT_USER\\Software\\Foo", "HKCU\\Software\\Foo"),
            ("Computer\\HKCU", RegistryRoot.CurrentUser, "Computer\\HKEY_CURRENT_USER", "HKCU"),
            ("computer\\hkcu\\software\\foo", RegistryRoot.CurrentUser, "Computer\\HKEY_CURRENT_USER\\software\\foo", "HKCU\\software\\foo"),
        };

        foreach (var item in cases)
        {
            var path = RegistryPath.Parse(item.Text);

            Assert.AreEqual(item.Root, path.Root, item.Text);
            Assert.AreEqual(item.Display, path.ToDisplayString(), item.Text);
            Assert.AreEqual(item.Abbreviated, path.ToAbbreviatedString(), item.Text);
            Assert.AreEqual(item.Display, path.ToString(), item.Text);
        }
    }

    [TestMethod]
    public void ParseAcceptsRootOnlyPaths()
    {
        var path = RegistryPath.Parse("HKLM");

        Assert.AreEqual(RegistryRoot.LocalMachine, path.Root);
        Assert.IsTrue(path.IsRoot);
        Assert.AreEqual(0, path.Components.Count);
        Assert.IsNull(path.Parent);
        Assert.AreEqual("Computer\\HKEY_LOCAL_MACHINE", path.ToDisplayString());
        Assert.AreEqual("HKLM", path.ToAbbreviatedString());
    }

    [TestMethod]
    public void ParsePreservesComponentSpellingAndOrder()
    {
        var path = RegistryPath.Parse("Computer\\HKEY_CURRENT_USER\\Software\\Vendor\\Product");

        CollectionAssert.AreEqual(
            new[] { "Software", "Vendor", "Product" },
            path.Components.ToArray());
    }

    [TestMethod]
    public void AppendAndParentWalkThePath()
    {
        var path = RegistryPath.Parse("HKCU\\Software")
            .Append("Foo")
            .Append("Bar");

        Assert.AreEqual("HKCU\\Software\\Foo\\Bar", path.ToAbbreviatedString());
        Assert.AreEqual("HKCU\\Software\\Foo", path.Parent!.ToAbbreviatedString());
        Assert.AreEqual("HKCU\\Software", path.Parent.Parent!.ToAbbreviatedString());
        Assert.AreEqual("HKCU", path.Parent.Parent.Parent!.ToAbbreviatedString());
        Assert.IsTrue(path.Parent.Parent.Parent!.IsRoot);
        Assert.IsNull(path.Parent.Parent.Parent.Parent);
    }

    [TestMethod]
    public void EqualityIsCaseInsensitiveAcrossRootAndComponents()
    {
        var first = RegistryPath.Parse("HKCU\\SOFTWARE\\foo");
        var second = RegistryPath.Parse("hkcu\\Software\\Foo");
        var otherRoot = RegistryPath.Parse("HKLM\\Software\\Foo");

        Assert.AreEqual(first, second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
        Assert.AreNotEqual(first, otherRoot);
    }

    [TestMethod]
    public void DescendantCheckComparesComponentsNotStringPrefixes()
    {
        var foo = RegistryPath.Parse("HKCU\\Software\\Foo");
        var foobar = RegistryPath.Parse("HKCU\\Software\\Foobar");

        Assert.IsTrue(RegistryPath.Parse("HKCU\\Software\\Foo\\Bar").IsDescendantOf(foo));
        Assert.IsTrue(RegistryPath.Parse("HKCU\\Software\\Foo").IsDescendantOf(RegistryPath.Parse("HKCU\\Software")));
        Assert.IsTrue(RegistryPath.Parse("HKCU\\Software\\Foo").IsDescendantOf(RegistryPath.Parse("HKCU")));
        Assert.IsTrue(RegistryPath.Parse("hkcu\\software\\foo\\bar").IsDescendantOf(foo));

        Assert.IsFalse(foo.IsDescendantOf(foo));
        Assert.IsFalse(RegistryPath.Parse("HKCU").IsDescendantOf(RegistryPath.Parse("HKCU")));
        Assert.IsFalse(foo.IsDescendantOf(RegistryPath.Parse("HKCU\\Software\\F")));
        Assert.IsFalse(foo.IsDescendantOf(foobar));
        Assert.IsFalse(foobar.IsDescendantOf(foo));
        Assert.IsFalse(foo.IsDescendantOf(RegistryPath.Parse("HKLM\\Software\\Foo")));
    }

    [TestMethod]
    public void ParseRejectsInvalidInput()
    {
        string[] invalid =
        {
            string.Empty,
            "\\",
            "\\HKCU",
            "HKCU\\",
            "HKCU\\Software\\",
            "HKCU\\\\Software",
            "HKCU\\Software\\\\Foo",
            "BOGUS\\Foo",
            " HKCU\\Software",
            "Computer",
            "Computer\\BOGUS\\Foo",
            "HKCU\\Software\\.",
            "HKCU\\Software\\..",
            "HKCU\\Software\\Foo\0Bar",
            "HKCU\\Software\\" + new string('a', 256),
        };

        foreach (var text in invalid)
        {
            Assert.ThrowsExactly<FormatException>(
                () => RegistryPath.Parse(text),
                $"Expected a FormatException for '{text.Replace("\0", "<NUL>")}'.");
        }

        Assert.ThrowsExactly<ArgumentNullException>(() => RegistryPath.Parse(null!));
    }

    [TestMethod]
    public void AppendRejectsUnsafeComponents()
    {
        var root = RegistryPath.Parse("HKCU");

        Assert.ThrowsExactly<ArgumentException>(() => root.Append(string.Empty));
        Assert.ThrowsExactly<ArgumentException>(() => root.Append("Foo\\Bar"));
        Assert.ThrowsExactly<ArgumentException>(() => root.Append("Foo\0Bar"));
        Assert.ThrowsExactly<ArgumentException>(() => root.Append("."));
        Assert.ThrowsExactly<ArgumentException>(() => root.Append(".."));
        Assert.ThrowsExactly<ArgumentException>(() => root.Append(new string('a', 256)));
        Assert.ThrowsExactly<ArgumentNullException>(() => root.Append(null!));
    }

    [TestMethod]
    public void TryParseReportsFailureWithoutThrowing()
    {
        Assert.IsTrue(RegistryPath.TryParse("HKCU\\Software\\Foo", out var parsed));
        Assert.IsNotNull(parsed);
        Assert.AreEqual("HKCU\\Software\\Foo", parsed!.ToAbbreviatedString());

        Assert.IsFalse(RegistryPath.TryParse("HKCU\\", out var trailing));
        Assert.IsNull(trailing);

        Assert.IsFalse(RegistryPath.TryParse(string.Empty, out var empty));
        Assert.IsNull(empty);

        Assert.IsFalse(RegistryPath.TryParse(null, out var missing));
        Assert.IsNull(missing);
    }
}
