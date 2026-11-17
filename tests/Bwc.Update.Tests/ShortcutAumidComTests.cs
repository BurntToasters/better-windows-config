using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// --set-aumid writes System.AppUserModel.ID with typed vtable calls (update-elevation.md HN11). These tests use the
// real shell link object on a .lnk in the test's own temp folder: nothing starts, nothing is written outside that
// folder, and the value is read back through a separate instance (ShortcutAumid.ReadAppId), not through the code that
// wrote it.
[TestClass]
public sealed unsafe class ShortcutAumidComTests
{
    private const int StgmReadWrite = 2;
    private static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");

    public TestContext TestContext { get; set; } = null!;

    // HN11
    [TestMethod]
    public void Aumid_Write_Roundtrips_On_Real_Shortcut()
    {
        string folder = NewFolder();
        try
        {
            string shortcut = CreateShortcut(folder, "Better Windows Config.lnk");

            Assert.IsNull(ShortcutAumid.ReadAppId(shortcut), "a new shortcut has no app id");

            var written = ShortcutAumid.Write(shortcut, "BetterWindowsConfig.Hub");

            Assert.IsTrue(written.Ok, written.Detail);
            Assert.AreEqual("BetterWindowsConfig.Hub", ShortcutAumid.ReadAppId(shortcut));

            var second = ShortcutAumid.Write(shortcut, "BetterWindowsConfig.Regedit");

            Assert.IsTrue(second.Ok, second.Detail);
            Assert.AreEqual("BetterWindowsConfig.Regedit", ShortcutAumid.ReadAppId(shortcut), "a second write replaces the first");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // HN11: the other properties of the shortcut stay (SA12).
    [TestMethod]
    public void Aumid_Write_Keeps_The_Target()
    {
        string folder = NewFolder();
        try
        {
            string shortcut = CreateShortcut(folder, "Registry Editor (BWC).lnk");
            long before = new FileInfo(shortcut).Length;

            var written = ShortcutAumid.Write(shortcut, "BetterWindowsConfig.Regedit");

            Assert.IsTrue(written.Ok, written.Detail);
            Assert.IsTrue(new FileInfo(shortcut).Length > before, "the property block was added");
            Assert.IsTrue(
                string.Equals(NotepadPath, ReadTarget(shortcut), StringComparison.OrdinalIgnoreCase),
                "the target is the one that was saved: " + ReadTarget(shortcut));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // HN11, SA9
    [TestMethod]
    public void Aumid_Missing_File_Fails_With_Io()
    {
        string folder = NewFolder();
        try
        {
            string missing = Path.Combine(folder, "Missing.lnk");

            var written = ShortcutAumid.Write(missing, "BetterWindowsConfig.Hub");

            Assert.IsFalse(written.Ok);
            Assert.AreEqual(UpdateFailure.Io, written.Failure);
            StringAssert.Contains(written.Detail, "could not be loaded", StringComparison.Ordinal);
            StringAssert.Contains(written.Detail, "HRESULT 0x", StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(missing), "a failed load creates nothing");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // HN11, SA9: a file that is not a shortcut is not changed.
    [TestMethod]
    public void Aumid_Not_A_Shortcut_Fails_And_Is_Unchanged()
    {
        string folder = NewFolder();
        try
        {
            string file = Path.Combine(folder, "Not A Link.lnk");
            byte[] content = "this is not a shell link"u8.ToArray();
            File.WriteAllBytes(file, content);

            var written = ShortcutAumid.Write(file, "BetterWindowsConfig.Hub");

            Assert.IsFalse(written.Ok);
            Assert.AreEqual(UpdateFailure.Io, written.Failure);
            CollectionAssert.AreEqual(content, File.ReadAllBytes(file));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string NotepadPath => Path.Combine(Environment.SystemDirectory, "notepad.exe");

    private string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "bwc-aumid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        TestContext.WriteLine("temp folder " + folder);
        return folder;
    }

    // A real .lnk through IShellLinkW (SetPath) and IPersistFile (Save), built with the same vtable layer as the code.
    private static string CreateShortcut(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        using var apartment = ComApartment.EnterSta();
        Assert.IsTrue(apartment.Usable);
        Assert.AreEqual(0, ComNative.CoCreateInstance(ComIds.ShellLinkClass, 0, ComNative.ClsctxInprocServer, IidShellLinkW, out nint link));
        using ComObject linkObject = ComObject.Own(link)!;
        fixed (char* target = NotepadPath)
        {
            Assert.AreEqual(0, ((delegate* unmanaged<nint, char*, int>)ComObject.Slot(link, ComSlots.ShellLinkSetPath))(link, target));
        }

        Assert.AreEqual(0, linkObject.QueryInterface(ComIds.PersistFile, out ComObject? file));
        using ComObject fileObject = file!;
        fixed (char* destination = path)
        {
            Assert.AreEqual(0, ((delegate* unmanaged<nint, char*, int, int>)ComObject.Slot(fileObject.Pointer, ComSlots.PersistFileSave))(fileObject.Pointer, destination, 1));
        }

        return path;
    }

    private static string ReadTarget(string shortcut)
    {
        using var apartment = ComApartment.EnterSta();
        Assert.AreEqual(0, ComNative.CoCreateInstance(ComIds.ShellLinkClass, 0, ComNative.ClsctxInprocServer, IidShellLinkW, out nint link));
        using ComObject linkObject = ComObject.Own(link)!;
        Assert.AreEqual(0, linkObject.QueryInterface(ComIds.PersistFile, out ComObject? file));
        using ComObject fileObject = file!;
        fixed (char* source = shortcut)
        {
            Assert.AreEqual(0, ((delegate* unmanaged<nint, char*, uint, int>)ComObject.Slot(fileObject.Pointer, ComSlots.PersistFileLoad))(fileObject.Pointer, source, 0));
        }

        char* buffer = stackalloc char[520];

        // IShellLinkW::GetPath is slot 3: (self, LPWSTR file, int max, WIN32_FIND_DATAW* data, DWORD flags).
        Assert.AreEqual(0, ((delegate* unmanaged<nint, char*, int, nint, uint, int>)ComObject.Slot(link, 3))(link, buffer, 520, 0, 0));
        return new string(buffer);
    }
}
