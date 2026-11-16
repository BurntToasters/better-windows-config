using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Dialogs.ResourceSeeds;

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
    private const string DetailChildKey = "ResourceDetails";
    private const string DetailRequirements = "DetailRequirements";
    private const string DetailList = "DetailList";
    private const string DetailDescriptor = "DetailDescriptor";
    private const int ClassicDisabledBit = 0x08000000;
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    // The four requirement rows, in seed order: name (alternative list, resource list, descriptor, device type).
    private static readonly string[][] DetailRequirementRows =
    [
        ["0", "0", "0", "Port"],
        ["0", "0", "1", "Interrupt"],
        ["0", "0", "2", "DMA"],
        ["0", "0", "3", "Memory"],
    ];

    // One detail viewer: the requirement row that opens it, the classic dialog, a control only this viewer shows, and
    // each value field with its classic control id and the text decoded from the seed.
    private sealed record DetailCase(string Kind, int RowIndex, int DialogId, string Sentinel, (string Id, int ClassicId, string Text)[] Fields);

    private static readonly DetailCase[] DetailCases =
    [
        new("port", 0, 1700, "AlignmentValue",
        [
            ("TypeValue", 1701, "Port"),
            ("LengthValue", 1703, "0x00000008"),
            ("AlignmentValue", 1706, "0x00000001"),
            ("MinimumAddressValue", 1704, "0x000003f8"),
            ("MaximumAddressValue", 1705, "0x000003ff"),
            ("ShareDispositionValue", 2000, "Device Exclusive"),
        ]),
        new("interrupt", 1, 1800, "MinimumVectorValue",
        [
            ("TypeValue", 1801, "Latched"),
            ("MinimumVectorValue", 1803, "23"),
            ("MaximumVectorValue", 1804, "31"),
            ("ShareDispositionValue", 2000, "Shared"),
        ]),
        new("dma", 2, 1900, "MinimumChannelValue",
        [
            ("MinimumChannelValue", 1902, "3"),
            ("MaximumChannelValue", 1903, "5"),
            ("ShareDispositionValue", 2000, "Driver Exclusive"),
        ]),
        new("memory", 3, 1601, "AccessValue",
        [
            ("AccessValue", 1602, "Read / Write"),
            ("LengthValue", 1603, "0x00001000"),
            ("AlignmentValue", 1606, "0x00001000"),
            ("MinimumAddressValue", 1604, "0x40000000"),
            ("MaximumAddressValue", 1605, "0x40000fff"),
            ("ShareDispositionValue", 2000, "Undetermined"),
        ]),
    ];

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

    /// <summary>
    /// Opens the Requirements List seeded with one Port, one Interrupt, one DMA, and one Memory descriptor, then opens
    /// each detail viewer (1700, 1800, 1900, 1601) with Display.... Every static text of the classic dialog (the dump)
    /// must show, the caption must match, each field must show the text decoded from the seed, and no field is editable.
    /// The Preferred and Alternative captions must be disabled, as the dump's style says.
    /// </summary>
    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("ResourceViewers")]
    [TestCategory("Mutating")]
    public void ResourceViewers_DetailViewersMatchClassicLabelsValuesAndReadOnly()
    {
        int checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        RequireBuilt(config);

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            _ = RegFileSeed.Import(config, sandbox, DetailChildKey, "resource-details.reg", DetailValues());

            string dataRoot = ClassicDialogTests.FreshDataRoot(config, "resource-details");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using JsonDocument oracle = ReadOracle(config);
            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, DetailChildKey);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "resources.detail.navigate");

            OpenWithEnter(session, driver, DetailRequirements);
            CheckRows(driver.WaitForElement(RequirementColumns, UiTimeout), "resources.detail.rows", DetailRequirementRows);

            foreach (DetailCase detail in DetailCases)
            {
                if (SelectAndOpenDetail(driver, detail))
                {
                    CheckDetail(driver, oracle, detail);
                    CloseDetail(driver, detail);
                }
            }

            Activate(driver, RequirementColumns);
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            E2EResults.Record("resources.detail.list-closed", driver.WaitForNoDialog(TimeSpan.FromSeconds(5)), "Escape closed the Requirements List.");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        var failures = E2EResults.FailedChecksSince(checkStart);
        Assert.AreEqual(0, failures.Count, $"Detail viewer checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>
    /// Opens Resource Lists and Resources from the seeded REG_RESOURCE_LIST, REG_FULL_RESOURCE_DESCRIPTOR values. Every
    /// partial type (DMA, Interrupt, Memory, Port, Device specific) must show its seeded row, and the column titles and the
    /// static labels must be the classic texts from the dump.
    /// </summary>
    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("ResourceViewers")]
    [TestCategory("Mutating")]
    public void ResourceViewers_ResourcesShowEveryPartialWithClassicTitles()
    {
        int checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        RequireBuilt(config);

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            _ = RegFileSeed.Import(config, sandbox, DetailChildKey, "resource-details.reg", DetailValues());

            string dataRoot = ClassicDialogTests.FreshDataRoot(config, "resource-partials");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using JsonDocument oracle = ReadOracle(config);
            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, DetailChildKey);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "resources.partials.navigate");

            OpenWithEnter(session, driver, DetailList);
            CheckRows(driver.WaitForElement(ListColumns, UiTimeout), "resources.partials.list-rows", new[] { new[] { "0", "PCI" } });
            Activate(driver, ListColumns);
            bool selected = SelectRowAt(driver, ListColumns, 0);
            E2EResults.Record("resources.partials.list-row-selected", selected, "the list row was selected.");
            _ = ControlTestSupport.WaitUntil(() => IsEnabled(driver, DisplayButton), TimeSpan.FromSeconds(3));
            _ = driver.Invoke(DisplayButton);
            if (driver.WaitForElement("MemoryColumns", UiTimeout) is not null)
            {
                CheckPartials(driver, oracle, "resources.partials.from-list");
                Activate(driver, "MemoryColumns");
                Keyboard.Type(VirtualKeyShort.ESCAPE);
                E2EResults.Record(
                    "resources.partials.from-list.closed",
                    ControlTestSupport.WaitUntil(() => driver.WaitForElement("MemoryColumns", TimeSpan.FromMilliseconds(200)) is null, TimeSpan.FromSeconds(5)),
                    "Escape closed the Resources viewer opened from the list.");
            }

            Activate(driver, ListColumns);
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            _ = driver.WaitForNoDialog(TimeSpan.FromSeconds(5));

            OpenWithEnter(session, driver, DetailDescriptor);
            if (driver.WaitForElement("MemoryColumns", UiTimeout) is not null)
            {
                CheckPartials(driver, oracle, "resources.partials.direct");
                Activate(driver, "DeviceSpecificColumns");
                _ = SelectRowAt(driver, "DeviceSpecificColumns", 0);
                E2EResults.Record(
                    "resources.partials.data-enabled",
                    ControlTestSupport.WaitUntil(() => IsEnabled(driver, ResourceDataButton), TimeSpan.FromSeconds(3)),
                    "Data... enabled for the seeded device specific row with data.");
                Activate(driver, "DeviceSpecificColumns");
                Keyboard.Type(VirtualKeyShort.ESCAPE);
                _ = ControlTestSupport.WaitUntil(() => driver.WaitForElement("MemoryColumns", TimeSpan.FromMilliseconds(200)) is null, TimeSpan.FromSeconds(5));
            }

            // A descriptor opened directly from the value list has no parent list, so Escape on it closes the viewer.
            if (driver.WaitForElement("MemoryColumns", TimeSpan.FromMilliseconds(200)) is not null)
            {
                Activate(driver, "MemoryColumns");
                Keyboard.Type(VirtualKeyShort.ESCAPE);
            }

            E2EResults.Record("resources.partials.closed", driver.WaitForNoDialog(TimeSpan.FromSeconds(5)), "the Resources viewer is closed.");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        var failures = E2EResults.FailedChecksSince(checkStart);
        Assert.AreEqual(0, failures.Count, $"Resources checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
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

    private static void RequireBuilt(E2EConfig config)
    {
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("resources.publish", false, ex.Message);
            Assert.Fail(ex.Message);
        }
    }

    // The classic dump is the oracle for static texts, control ids, captions, and the disabled style bit.
    private static JsonDocument ReadOracle(E2EConfig config)
    {
        string path = Path.Combine(config.RepoRoot, "docs", "parity", "oracle", "regedit-resources.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static JsonElement ClassicDialog(JsonDocument oracle, int id)
    {
        foreach (JsonElement dialog in oracle.RootElement.GetProperty("modules")[0].GetProperty("dialogs").EnumerateArray())
        {
            if (dialog.GetProperty("id").GetInt32() == id)
            {
                return dialog;
            }
        }

        throw new InvalidDataException($"RT_DIALOG {id} is not in regedit-resources.json.");
    }

    private static IEnumerable<JsonElement> ClassicControls(JsonElement dialog, string className) =>
        dialog.GetProperty("controls").EnumerateArray()
            .Where(c => c.TryGetProperty("windowClass", out JsonElement cls) && cls.ValueKind == JsonValueKind.Object &&
                cls.TryGetProperty("name", out JsonElement name) && name.GetString() == className);

    // Every non-empty text of a Static control: the labels the classic dialog shows.
    private static string[] ClassicStaticTexts(JsonElement dialog) =>
        ClassicControls(dialog, "Static")
            .Select(c => c.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty)
            .Where(t => t.Length > 0)
            .ToArray();

    // The column titles of a ColumnListBox are one dump title with the names separated by semicolons.
    private static string[] ClassicColumnTitles(JsonElement dialog) =>
        dialog.GetProperty("controls").EnumerateArray()
            .Select(c => c.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty)
            .Where(t => t.Contains(';', StringComparison.Ordinal))
            .SelectMany(t => t.Split(';'))
            .ToArray();

    private static bool HasClassicStatic(JsonElement dialog, int id) =>
        ClassicControls(dialog, "Static").Any(c => c.GetProperty("id").GetInt32() == id);

    private static bool ClassicStaticIsDisabled(JsonElement dialog, int id) =>
        ClassicControls(dialog, "Static").Any(c => c.GetProperty("id").GetInt32() == id &&
            (Convert.ToInt64(c.GetProperty("style").GetString()!.Replace("0x", string.Empty), 16) & ClassicDisabledBit) != 0);

    // The Text elements of a window, by their UI Automation name. Static texts and cell texts are both Text elements.
    private static HashSet<string> TextNames(AutomationElement window) =>
        window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Select(e => e.Name ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

    private static bool SelectRowAt(EditingDriver driver, string listId, int index)
    {
        AutomationElement? list = driver.WaitForElement(listId, UiTimeout);
        AutomationElement[] items = list is null ? [] : list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
        if (index < 0 || index >= items.Length)
        {
            return false;
        }

        items[index].Patterns.SelectionItem.Pattern.Select();
        return true;
    }

    private static bool SelectAndOpenDetail(EditingDriver driver, DetailCase detail)
    {
        Activate(driver, RequirementColumns);
        bool selected = SelectRowAt(driver, RequirementColumns, detail.RowIndex);
        E2EResults.Record($"resources.detail.{detail.Kind}.row-selected", selected, $"requirement row {detail.RowIndex} selected.");
        bool enabled = selected && ControlTestSupport.WaitUntil(() => IsEnabled(driver, RequirementDisplay), TimeSpan.FromSeconds(3));
        if (enabled)
        {
            _ = driver.Invoke(RequirementDisplay);
        }

        bool opened = enabled && driver.WaitForElement(detail.Sentinel, UiTimeout) is not null;
        E2EResults.Record($"resources.detail.{detail.Kind}.opened", opened, $"Display... opened the {detail.Kind} viewer (sentinel {detail.Sentinel}).");
        return opened;
    }

    private static void CheckDetail(EditingDriver driver, JsonDocument oracle, DetailCase detail)
    {
        string kind = detail.Kind;
        AutomationElement window = ClassicDialogTests.WindowOf(driver.WaitForElement(detail.Sentinel, UiTimeout)!);
        JsonElement classic = ClassicDialog(oracle, detail.DialogId);

        string caption = window.Name ?? string.Empty;
        string classicCaption = classic.GetProperty("title").GetString() ?? string.Empty;
        E2EResults.Record($"resources.detail.{kind}.caption", caption == classicCaption, $"caption='{caption}', classic='{classicCaption}'.");

        HashSet<string> shown = TextNames(window);
        string[] missing = ClassicStaticTexts(classic).Where(text => !shown.Contains(text)).ToArray();
        E2EResults.Record(
            $"resources.detail.{kind}.labels",
            missing.Length == 0,
            $"{ClassicStaticTexts(classic).Length} classic static texts, missing=[{string.Join(", ", missing)}].");

        int[] unknownIds = detail.Fields.Where(f => !HasClassicStatic(classic, f.ClassicId)).Select(f => f.ClassicId).ToArray();
        E2EResults.Record(
            $"resources.detail.{kind}.ids",
            unknownIds.Length == 0,
            $"{detail.Fields.Length} value fields; classic ids without a Static control=[{string.Join(", ", unknownIds)}].");

        var mismatches = detail.Fields
            .Select(f => (f.Id, Expected: f.Text, Actual: driver.TextOf(f.Id)))
            .Where(f => f.Actual != f.Expected)
            .Select(f => $"{f.Id}: '{f.Actual}' expected '{f.Expected}'")
            .ToArray();
        E2EResults.Record(
            $"resources.detail.{kind}.values",
            mismatches.Length == 0,
            mismatches.Length == 0 ? "every field shows the text decoded from the seed." : string.Join("; ", mismatches));

        bool classicDisabled = ClassicStaticIsDisabled(classic, 2001) && ClassicStaticIsDisabled(classic, 2002);
        bool templateDisabled = classicDisabled && !IsEnabled(driver, "PreferredText") && !IsEnabled(driver, "AlternativeText");
        E2EResults.Record(
            $"resources.detail.{kind}.template-disabled",
            templateDisabled,
            $"classic 2001/2002 disabled={classicDisabled}; Preferred and Alternative disabled in the viewer={templateDisabled}.");

        int edits = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Length;
        E2EResults.Record($"resources.detail.{kind}.read-only", edits == 0, $"edit controls in the viewer={edits}.");
    }

    private static void CloseDetail(EditingDriver driver, DetailCase detail)
    {
        Activate(driver, detail.Sentinel);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        bool closed = ControlTestSupport.WaitUntil(
            () => driver.WaitForElement(detail.Sentinel, TimeSpan.FromMilliseconds(200)) is null,
            TimeSpan.FromSeconds(5));
        E2EResults.Record($"resources.detail.{detail.Kind}.closed", closed, "Escape closed the detail viewer.");
        E2EResults.Record(
            $"resources.detail.{detail.Kind}.list-open",
            driver.WaitForElement(RequirementColumns, TimeSpan.FromSeconds(3)) is not null,
            "the Requirements List stayed open.");
    }

    // The Resources viewer shows the five column lists of the seeded descriptor, one row each.
    private static void CheckPartials(EditingDriver driver, JsonDocument oracle, string prefix)
    {
        AutomationElement? memory = driver.WaitForElement("MemoryColumns", UiTimeout);
        if (memory is null)
        {
            E2EResults.Record(prefix + ".viewer", false, "the Resources viewer did not open.");
            return;
        }

        AutomationElement window = ClassicDialogTests.WindowOf(memory);
        JsonElement classic = ClassicDialog(oracle, 1305);
        string[] titles = ClassicStaticTexts(classic).Concat(ClassicColumnTitles(classic)).ToArray();
        HashSet<string> shown = TextNames(window);
        string[] missing = titles.Where(t => !shown.Contains(t)).Distinct(StringComparer.Ordinal).ToArray();
        E2EResults.Record(
            prefix + ".titles",
            missing.Length == 0,
            $"{titles.Length} classic titles and labels of RT_DIALOG 1305, missing=[{string.Join(", ", missing)}].");

        CheckRows(driver.WaitForElement("DmaColumns", UiTimeout), prefix + ".dma-row", new[] { new[] { "3", "1" } });
        CheckRows(
            driver.WaitForElement("InterruptColumns", UiTimeout),
            prefix + ".interrupt-row",
            new[] { new[] { "31", "23", "0x00000001", "Latched" } });
        CheckRows(memory, prefix + ".memory-row", new[] { new[] { "0x40000000", "0x00001000", "Read / Write" } });
        CheckRows(driver.WaitForElement("PortColumns", UiTimeout), prefix + ".port-row", new[] { new[] { "0x000003f8", "0x00000008", "Port" } });
        CheckRows(driver.WaitForElement("DeviceSpecificColumns", UiTimeout), prefix + ".device-row", new[] { new[] { "17", "34", "4" } });
    }

    // REG_RESOURCE_REQUIREMENTS_LIST: one alternative list with a Port, an Interrupt, a DMA, and a Memory descriptor.
    private static byte[] RequirementDetailBytes()
    {
        byte[] port = Requirement(0, 1, 1, 1, Bytes().U32(8).U32(1).U64(0x3F8UL).U64(0x3FFUL));
        byte[] interrupt = Requirement(0, 2, 3, 1, Bytes().U32(0x17).U32(0x1F).Zeros(16));
        byte[] dma = Requirement(0, 4, 2, 0, Bytes().U32(3).U32(5).Zeros(16));
        byte[] memory = Requirement(0, 3, 0, 0, Bytes().U32(0x1000).U32(0x1000).U64(0x40000000UL).U64(0x40000FFFUL));
        byte[] list = Bytes().U16(1).U16(1).U32(4).Raw(port).Raw(interrupt).Raw(dma).Raw(memory).ToArray();
        int size = 32 + list.Length;
        return Bytes()
            .U32((uint)size).U32(5).U32(0).U32(0).U32(0).U32(0).U32(0).U32(1)
            .Raw(list)
            .ToArray();
    }

    // One full descriptor with a Memory, a Port, an Interrupt, a DMA, and a device specific partial with four data bytes.
    private static byte[] PartialFullBytes() => Bytes()
        .U32(5).U32(0).U16(1).U16(2).U32(5)
        .Partial(3, 0, 0, Memory(0x40000000UL, 0x1000u))
        .Partial(1, 1, 1, Memory(0x3F8UL, 8u))
        .Partial(2, 3, 1, Interrupt(0x17u, 0x1Fu, 1UL))
        .Partial(4, 1, 0, Dma(3u, 1u, 0u))
        .Partial(5, 0, 0, Union(4u, 0x11u, 0x22u))
        .Raw([0x01, 0x02, 0x03, 0x04])
        .ToArray();

    // REG_RESOURCE_LIST: a count of one, then the full descriptor.
    private static byte[] PartialListBytes() => Bytes().U32(1).Raw(PartialFullBytes()).ToArray();

    private static IReadOnlyList<RegFileSeed.SeedValue> DetailValues() =>
    [
        new RegFileSeed.SeedValue(DetailRequirements, 10, RequirementDetailBytes()),
        new RegFileSeed.SeedValue(DetailList, 8, PartialListBytes()),
        new RegFileSeed.SeedValue(DetailDescriptor, 9, PartialFullBytes()),
    ];
}
