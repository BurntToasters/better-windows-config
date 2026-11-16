using System.Runtime.InteropServices;
using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Dialogs;

/// <summary>
/// Seeds valid REG_RESOURCE_LIST, REG_FULL_RESOURCE_DESCRIPTOR, and REG_RESOURCE_REQUIREMENTS_LIST
/// values and one malformed
/// REG_RESOURCE_LIST through the silent import, opens each with Enter as classic does, and checks the
/// classic layout ids, the
/// list rows, the field texts, the Display... and Data... enable rules, and the detail viewers. The
/// malformed value opens the
/// viewer with the rows that parsed, then the value error over it. Escape closes each viewer.
/// </summary>
[TestClass]
public sealed class ResourceViewerTests
{
    private const string ChildKey = "ResourceViewers";
    private const string ListValue = "ResourceList";
    private const string DescriptorValue = "FullDescriptor";
    private const string RequirementsValue = "Requirements";
    private const string BadValue = "BadResourceList";
    private const string ListColumns = "ResourceListColumns";
    private const string DisplayButton = "ResourceDisplay";
    private const string ResourceDataButton = "ResourceData";
    private const string RequirementColumns = "RequirementColumns";
    private const string RequirementDisplay = "RequirementDisplay";
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("ResourceViewers")]
    [TestCategory("Mutating")]
    public void ResourceViewers_OpenWithEnterAndShowTheClassicLayout()
    {
        int checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("resources.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            _ = RegFileSeed.Import(config, sandbox, ChildKey, "resource-viewers.reg", new[]
            {
                new RegFileSeed.SeedValue(ListValue, 8, ResourceListBytes()),
                new RegFileSeed.SeedValue(DescriptorValue, 9, FullDescriptorBytes()),
                new RegFileSeed.SeedValue(RequirementsValue, 10, RequirementsBytes()),
                new RegFileSeed.SeedValue(BadValue, 8, TruncatedListBytes()),
            });

            string dataRoot = ClassicDialogTests.FreshDataRoot(config, "resource-viewers");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, ChildKey);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "resources.navigate");

            OpenWithEnter(session, driver, ListValue);
            CheckResourceList(session, driver, automation);

            OpenWithEnter(session, driver, DescriptorValue);
            CheckDescriptor(session, driver, "0x00001000", hasDeviceRow: true);

            OpenWithEnter(session, driver, RequirementsValue);
            CheckRequirements(session, driver);

            OpenWithEnter(session, driver, BadValue);
            CheckMalformed(session, driver);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        var failures = E2EResults.FailedChecksSince(checkStart);
        Assert.AreEqual(0, failures.Count, $"Resource viewer checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

#pragma warning disable SYSLIB1054 // The small user32 surface does not justify LibraryImport plumbing.
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);
#pragma warning restore SYSLIB1054

    // Evidence for the keyboard input target: the foreground window and the focus of its thread, with class names.
    private static void LogFocus(string tag)
    {
        IntPtr foreground = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        bool ok = GetGUIThreadInfo(thread, ref info);
        E2EResults.Note(
            "resources.focus." + tag,
            $"foreground={Describe(foreground)} active={Describe(info.Active)} focus={Describe(info.Focus)} " +
            $"guiThreadInfo={ok} thread={thread}.");
    }

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "0";
        }

        var name = new StringBuilder(256);
        _ = GetClassNameW(hwnd, name, name.Capacity);
        return $"0x{hwnd:X}({name})";
    }

    private static void OpenWithEnter(AppSession session, EditingDriver driver, string name)
    {
        AutomationElement? row = ControlTestSupport.FindRowByName(session, "ListControl", ControlType.DataItem, name);
        if (row is null)
        {
            E2EResults.Record($"resources.{name}.row", false, "list row was not found.");
            return;
        }

        row.Click();
        _ = ControlTestSupport.EnsureForeground(session);
        Keyboard.Type(VirtualKeyShort.ENTER);
        E2EResults.Record($"resources.{name}.opened", driver.WaitForDialog(), "a viewer opened on Enter.");
    }

    // Gives the viewer that owns the element the foreground, so its keys and clicks go to it.
    private static void Activate(EditingDriver driver, string automationId)
    {
        AutomationElement? element = driver.WaitForElement(automationId, UiTimeout);
        if (element is not null)
        {
            _ = ClassicDialogTests.EnsureForeground(ClassicDialogTests.DialogHandle(element));
        }
    }

    // Selects the first row of a column list through UI Automation, the same selection a click makes.
    private static bool SelectFirstRow(EditingDriver driver, string listId)
    {
        AutomationElement? list = driver.WaitForElement(listId, UiTimeout);
        AutomationElement? item = list?.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem));
        if (item is null)
        {
            return false;
        }

        item.Patterns.SelectionItem.Pattern.Select();
        return true;
    }

    private static void CheckResourceList(AppSession session, EditingDriver driver, UIA3Automation automation)
    {
        _ = session;
        _ = automation;
        AutomationElement? columns = driver.WaitForElement(ListColumns, UiTimeout);
        E2EResults.Record("resources.list.ids", columns is not null, "ResourceListColumns present.");
        CheckRows(columns, "resources.list.rows", new[] { new[] { "0", "PCI" } });
        CheckText(driver, DisplayButton, "resources.list.display-disabled-before-select", enabled: false);

        // Probe: Escape with no child open. The Resource Lists template has no Cancel button (id 2) and no default
        // button, and classic regedit closes the viewer on IDCANCEL, so Escape must close it here.
        Activate(driver, ListColumns);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        bool closedAlone = driver.WaitForNoDialog(TimeSpan.FromSeconds(3));
        E2EResults.Record("resources.list.escape-without-child", closedAlone, "Escape closed the Resource Lists viewer with no child open.");
        if (closedAlone)
        {
            OpenWithEnter(session, driver, ListValue);
            CheckRowsAgain(driver);
        }

        Activate(driver, ListColumns);
        bool selected = SelectFirstRow(driver, ListColumns);
        E2EResults.Record("resources.list.row-selected", selected, "the first row was selected through UI Automation.");
        E2EResults.Record("resources.list.display-enabled", ControlTestSupport.WaitUntil(() => IsEnabled(driver, DisplayButton), TimeSpan.FromSeconds(3)), "Display... enabled after a row is selected.");

        // Display... opens the Resources viewer on top of the list. Escape closes the child, and the list stays open.
        _ = driver.Invoke(DisplayButton);
        _ = ControlTestSupport.WaitUntil(() => driver.WaitForElement("MemoryColumns", TimeSpan.FromMilliseconds(200)) is not null, TimeSpan.FromSeconds(5));
        LogFocus("child-open");
        // The seeded list value has one memory descriptor of 0x10000 bytes and no device specific row.
        CheckDescriptor(session, driver, "0x00010000", hasDeviceRow: false);
        LogFocus("child-closed");
        E2EResults.Record("resources.list.still-open", driver.WaitForElement(ListColumns, TimeSpan.FromSeconds(3)) is not null, "the list stayed open.");

        // Escape on the owner after its nested viewer closed must close the owner.
        Activate(driver, ListColumns);
        LogFocus("list-before-escape");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        bool closed = driver.WaitForNoDialog(TimeSpan.FromSeconds(5));
        E2EResults.Record("resources.list.escape-after-child", closed, "Escape closed the Resource Lists viewer after its nested viewer closed.");
        if (!closed)
        {
            _ = driver.Invoke("DialogPrimary");
        }
    }

    /// <summary>The seeded list value has one row again after a reopen, so the rows are checked once more.</summary>
    private static void CheckRowsAgain(EditingDriver driver)
    {
        AutomationElement? columns = driver.WaitForElement(ListColumns, UiTimeout);
        CheckRows(columns, "resources.list.reopen-rows", new[] { new[] { "0", "PCI" } });
    }

    private static void CheckDescriptor(AppSession session, EditingDriver driver, string memoryLength, bool hasDeviceRow)
    {
        _ = session;
        E2EResults.Record("resources.descriptor.ids", driver.WaitForElement("MemoryColumns", UiTimeout) is not null, "classic column lists present.");
        E2EResults.Record("resources.descriptor.bus", driver.TextOf("BusNumberValue") == "0", $"bus='{driver.TextOf("BusNumberValue")}'.");
        E2EResults.Record("resources.descriptor.interface", driver.TextOf("InterfaceTypeValue") == "PCI", $"interface='{driver.TextOf("InterfaceTypeValue")}'.");
        E2EResults.Record("resources.descriptor.version", driver.TextOf("VersionValue") == "1" && driver.TextOf("RevisionValue") == "2", "version and revision.");
        CheckRows(driver.WaitForElement("MemoryColumns", UiTimeout), "resources.descriptor.memory-row", new[] { new[] { "0x40000000", memoryLength, "Read / Write" } });
        E2EResults.Record("resources.descriptor.share-undetermined-disabled", !IsEnabled(driver, "ShareUndetermined"), "share statics template-disabled.");
        CheckText(driver, ResourceDataButton, "resources.descriptor.data-disabled-before-select", enabled: false);
        string focusId = "MemoryColumns";
        if (hasDeviceRow)
        {
            Activate(driver, "DeviceSpecificColumns");
            _ = SelectFirstRow(driver, "DeviceSpecificColumns");
            E2EResults.Record("resources.descriptor.data-enabled-on-data-row", ControlTestSupport.WaitUntil(() => IsEnabled(driver, ResourceDataButton), TimeSpan.FromSeconds(3)), "Data... enabled for a Device Specific row with data.");
            focusId = "DeviceSpecificColumns";
        }

        Activate(driver, focusId);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        bool childClosed = ControlTestSupport.WaitUntil(() => driver.WaitForElement("MemoryColumns", TimeSpan.FromMilliseconds(200)) is null, TimeSpan.FromSeconds(5));
        E2EResults.Record("resources.descriptor.closed", childClosed, "Escape closed the Resources viewer.");
    }

    private static void CheckRequirements(AppSession session, EditingDriver driver)
    {
        _ = session;
        AutomationElement? columns = driver.WaitForElement(RequirementColumns, UiTimeout);
        CheckRows(columns, "resources.requirements.rows", new[] { new[] { "0", "0", "0", "Memory" }, new[] { "0", "0", "1", "Port" } });
        E2EResults.Record("resources.requirements.interface", driver.TextOf("InterfaceTypeValue") == "PCI", "Interface Type shows the bus name.");
        Activate(driver, RequirementColumns);
        E2EResults.Record("resources.requirements.row-selected", SelectFirstRow(driver, RequirementColumns), "row 0 selected.");
        E2EResults.Record("resources.requirements.display-enabled", ControlTestSupport.WaitUntil(() => IsEnabled(driver, RequirementDisplay), TimeSpan.FromSeconds(3)), "Display... enabled after a row is selected.");
        _ = driver.Invoke(RequirementDisplay);
        E2EResults.Record("resources.requirements.detail-memory", driver.WaitForElement("MinimumAddressValue", UiTimeout) is not null, "the Memory detail opened.");
        E2EResults.Record("resources.requirements.access", driver.TextOf("AccessValue") == "Read / Write", $"access='{driver.TextOf("AccessValue")}'.");
        E2EResults.Record("resources.requirements.min", driver.TextOf("MinimumAddressValue") == "0x40000000", $"minimum='{driver.TextOf("MinimumAddressValue")}'.");
        Activate(driver, "MinimumAddressValue");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        bool detailClosed = ControlTestSupport.WaitUntil(() => driver.WaitForElement("MinimumAddressValue", TimeSpan.FromMilliseconds(200)) is null, TimeSpan.FromSeconds(5));
        E2EResults.Record("resources.requirements.detail-closed", detailClosed, "Escape closed the Memory detail.");
        // Escape on the owner after the nested detail closed must close the owner.
        Activate(driver, RequirementColumns);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        bool closed = driver.WaitForNoDialog(TimeSpan.FromSeconds(5));
        E2EResults.Record("resources.requirements.escape-after-child", closed, "Escape closed the Requirements List viewer after its nested detail closed.");
        if (!closed)
        {
            _ = driver.Invoke("DialogPrimary");
        }

        E2EResults.Record("resources.requirements.closed", driver.WaitForNoDialog(), "the requirement viewer is closed.");
    }

    private static void CheckMalformed(AppSession session, EditingDriver driver)
    {
        _ = session;
        bool errorShown = ControlTestSupport.WaitUntil(() => driver.DismissErrorBox(TimeSpan.FromSeconds(1)), UiTimeout);
        E2EResults.Record("resources.malformed.error", errorShown, "the value error was shown over the viewer.");
        AutomationElement? columns = driver.WaitForElement(ListColumns, UiTimeout);
        CheckRows(columns, "resources.malformed.rows", new[] { new[] { "0", "PCI" } });
        Activate(driver, ListColumns);
        _ = driver.Invoke("DialogPrimary");
        E2EResults.Record("resources.malformed.ok-closed", driver.WaitForNoDialog(), "OK closed the viewer with the rows that parsed.");
    }

    private static void CheckRows(AutomationElement? columns, string checkId, string[][] expected)
    {
        IReadOnlyList<string[]> rows = columns is null
            ? Array.Empty<string[]>()
            : columns.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Select(item => item.Name.Split('|'))
                .ToArray();
        bool ok = rows.Count == expected.Length && rows.Zip(expected).All(pair => pair.First.SequenceEqual(pair.Second));
        E2EResults.Record(checkId, ok, $"rows=[{string.Join(" | ", rows.Select(r => string.Join(",", r)))}].");
    }

    private static void CheckText(EditingDriver driver, string id, string checkId, bool enabled)
    {
        E2EResults.Record(checkId, IsEnabled(driver, id) == enabled, $"{id} enabled={IsEnabled(driver, id)}.");
    }

    private static bool IsEnabled(EditingDriver driver, string id)
    {
        AutomationElement? element = driver.WaitForElement(id, TimeSpan.FromMilliseconds(300));
        return element is not null && element.IsEnabled;
    }

    // Count 1, one full descriptor: InterfaceType 5 (PCI), bus 0, version 1, revision 2, one memory partial.
    private static byte[] ResourceListBytes() => Bytes()
        .U32(1)
        .U32(5).U32(0).U16(1).U16(2).U32(1)
        .Partial(3, 1, 0, Memory(0x40000000UL, 0x10000u))
        .ToArray();

    // Partial list: memory, port (I/O), interrupt (latched), device specific with four data bytes.
    private static byte[] FullDescriptorBytes()
    {
        byte[] data = [0x01, 0x02, 0x03, 0x04];
        return Bytes()
            .U32(5).U32(0).U16(1).U16(2).U32(4)
            .Partial(3, 1, 0, Memory(0x40000000UL, 0x1000u))
            .Partial(1, 1, 1, Memory(0x3F8UL, 8u))
            .Partial(2, 1, 1, Interrupt(0x17u, 0x23u, 0xFUL))
            .Partial(5, 0, 0, Union(4u, 0x11u, 0x22u))
            .Raw(data)
            .ToArray();
    }

    // One IO list with two descriptors: memory (read/write, 4 KiB) and port (I/O, 8 bytes at 0x3F8).
    private static byte[] RequirementsBytes()
    {
        byte[] memory = Bytes().U8(0).U8(3).U8(1).U8(0).U16(0).U16(0).U32(0x1000).U32(0x1000).U64(0x40000000UL).U64(0x40000FFFUL).ToArray();
        byte[] port = Bytes().U8(0).U8(1).U8(1).U8(0).U16(1).U16(0).U32(8).U32(1).U64(0x3F8UL).U64(0x3FFUL).ToArray();
        byte[] list = Bytes().U16(1).U16(1).U32(2).Raw(memory).Raw(port).ToArray();
        int size = 32 + list.Length;
        return Bytes()
            .U32((uint)size).U32(5).U32(0).U32(0).U32(0).U32(0).U32(0).U32(1)
            .Raw(list)
            .ToArray();
    }

    // Count says two full descriptors, but only the first is complete. The first row should still show.
    private static byte[] TruncatedListBytes()
    {
        // The second header fits, so the count passes the forged-count guard. It claims one partial that is missing.
        byte[] first = Bytes().U32(5).U32(0).U16(1).U16(2).U32(0).ToArray();
        byte[] second = Bytes().U32(5).U32(0).U16(1).U16(2).U32(1).ToArray();
        return Bytes().U32(2).Raw(first).Raw(second).ToArray();
    }

    private static byte[] Memory(ulong start, uint length) => Bytes().U64(start).U32(length).Zeros(4).ToArray();

    private static byte[] Interrupt(uint level, uint vector, ulong affinity) => Bytes().U32(level).U32(vector).U64(affinity).ToArray();

    private static byte[] Union(uint dataSize, uint reserved1, uint reserved2) =>
        Bytes().U32(dataSize).U32(reserved1).U32(reserved2).Zeros(4).ToArray();

    private static Builder Bytes() => new();

    // Little-endian builder for the seeded layouts. The partial descriptor is 20 bytes: type, share,
    // flags, 16-byte union.
    private sealed class Builder
    {
        private readonly List<byte> _data = [];

        public Builder U8(byte value) { _data.Add(value); return this; }

        public Builder U16(ushort value) { _data.AddRange(BitConverter.GetBytes(value)); return this; }

        public Builder U32(uint value) { _data.AddRange(BitConverter.GetBytes(value)); return this; }

        public Builder U64(ulong value) { _data.AddRange(BitConverter.GetBytes(value)); return this; }

        public Builder Zeros(int count) { _data.AddRange(new byte[count]); return this; }

        public Builder Raw(byte[] data) { _data.AddRange(data); return this; }

        public Builder Partial(byte type, byte share, ushort flags, byte[] union)
        {
            U8(type).U8(share).U16(flags);
            return Raw(union.Length == 16 ? union : union.Concat(new byte[16 - union.Length]).ToArray());
        }

        public byte[] ToArray() => _data.ToArray();
    }
}
