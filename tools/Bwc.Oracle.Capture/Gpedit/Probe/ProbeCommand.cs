using System.Diagnostics;
using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit.Probe;

// `gpedit probe`: starts classic gpedit.msc inside a GpeditSession and records how it can be driven by UI Automation and how classic orders
// Registry.pol records. Every step records success or its exception and the run goes on. Output: artifacts/oracle/gpedit/probe.json and
// artifacts/oracle/gpedit/probe/*.png. The session restores the machine afterwards (docs/failure-modes/oracle-gpedit-session.md).
internal sealed class ProbeCommand
{
    private const string Zeta = "Probe Zeta";
    private const string Alpha = "Probe Alpha";
    private const string Options = "Probe Options";

    private static readonly string[] Plan =
    {
        "baseline-files: record the GroupPolicy files before classic starts",
        "start-mmc: mmc.exe %SystemRoot%\\System32\\gpedit.msc (UseShellExecute=false, inherits elevation), wait for the main window",
        "wait-populated: wait for the console tree to fill",
        "uia-main-tree: raw UI Automation tree of the main window with patterns, rectangles, ids",
        "msaa: IAccessible names of child windows, the menu bar, tree, list, toolbar, status bar",
        "menu-bar: open each top menu (expand only, nothing is invoked), dump the items, close",
        "toolbar: toolbar buttons",
        "panes: tree items, list header and columns, status bar, tabs",
        "navigate: Computer Configuration > Administrative Templates > BWC Probe",
        "result-list: rows, then the Extended and Standard tabs",
        "options-dialog: open Probe_Options, dump its UI Automation tree, tab order, rectangles, then Cancel",
        "byteorder-1..6: Zeta Enabled, Alpha Enabled, Zeta Disabled, Alpha Disabled, Zeta Not Configured, Alpha Not Configured; OK each; file bytes after each",
        "close-mmc: File > Exit, record whether classic asked anything",
        "final-files: record the GroupPolicy files after classic closed",
    };

    private readonly RepoPaths repo;
    private readonly GpeditContext ctx;
    private readonly string shotDir;
    private ProbeRecorder recorder = null!;
    private Process? mmc;
    private IntPtr main;
    private AutomationElement? mainElement;
    private JsonObject? lastFiles;

    private ProbeCommand(RepoPaths repo, GpeditContext ctx)
    {
        this.repo = repo;
        this.ctx = ctx;
        shotDir = Path.Combine(ctx.Paths.OutputDir, "probe");
    }

    // Prints what a real run would do and checks what it can without changing anything. Works unelevated.
    internal static void DryRun(RepoPaths repo)
    {
        GpeditPaths paths = GpeditPaths.Real(repo);
        Console.WriteLine("gpedit probe --dry-run: nothing is changed, started or written.");
        Console.WriteLine();
        Console.WriteLine("preflight (read only):");
        Console.WriteLine($"  elevated: {(SessionGuards.IsElevated() ? "yes" : "NO, a real run refuses with exit code 2")}");
        Console.WriteLine($"  interactive desktop: {SessionGuards.InteractiveProblem() ?? "ok"}");
        Console.WriteLine($"  backup manifest {paths.ManifestPath}: {(File.Exists(paths.ManifestPath) ? "EXISTS, a real run refuses until `gpedit restore`" : "absent")}");
        TemplateSet templates;
        try
        {
            templates = ProbeTemplate.Load(repo);
        }
        catch (FileNotFoundException ex)
        {
            Console.WriteLine($"  probe template: MISSING ({ex.Message})");
            templates = TemplateSet.Empty;
        }

        try
        {
            GpeditSession.RequireTemplatesFree(paths, templates);
            Console.WriteLine("  template destinations: free");
        }
        catch (GpeditRefusal refusal)
        {
            Console.WriteLine("  template destinations: " + refusal.Message);
        }

        Console.WriteLine();
        Console.WriteLine("backup plan (into artifacts/oracle/gpedit/backup/, manifest written last, atomically):");
        Console.WriteLine($"  {paths.GroupPolicyDir}: {Describe(paths.GroupPolicyDir)}");
        Console.WriteLine($"  {paths.MmcStatePath}: {Describe(paths.MmcStatePath)}");
        Console.WriteLine("  every file is copied, hashed (SHA-256) in the copy and at the source, and the source is scanned again afterwards.");
        Console.WriteLine();
        Console.WriteLine("templates copied (names and hashes are in the manifest before the copy):");
        foreach (TemplateSet.Item item in templates.Items)
        {
            Console.WriteLine($"  {item.SourcePath}  ->  {Path.Combine(paths.PolicyDefinitionsDir, item.RelativePath)}");
        }

        Console.WriteLine();
        Console.WriteLine("probe steps:");
        foreach (string step in Plan)
        {
            Console.WriteLine("  " + step);
        }

        Console.WriteLine();
        Console.WriteLine("restore plan (finally block, Ctrl+C, console close, or `gpedit restore`):");
        Console.WriteLine("  close only the mmc.exe this run started (pid and start time recorded)");
        Console.WriteLine("  make GroupPolicy and the MMC state equal to the backup (delete extra, rewrite changed, recreate missing, remove if it did not exist)");
        Console.WriteLine("  delete only the template files whose hash matches what was copied");
        foreach (RegistryTarget key in paths.RegistryTargets)
        {
            Console.WriteLine($"  delete registry key {key.Display}");
        }

        Console.WriteLine("  verify every hash and that the four keys are gone, and only then delete the manifest");
        Console.WriteLine("  gpupdate and RefreshPolicyEx are never called");
        Console.WriteLine();
        Console.WriteLine($"outputs: {Path.Combine(paths.OutputDir, "probe.json")} and {Path.Combine(paths.OutputDir, "probe")}\\*.png");
    }

    private static string Describe(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return $"folder, {Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count()} files";
            }

            return File.Exists(path) ? $"file, {new FileInfo(path).Length} bytes" : "does not exist";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "cannot be read: " + ex.Message;
        }
    }

    internal static void Run(RepoPaths repo)
    {
        Win32.SetProcessDpiAwarenessContext(Msg.DpiAwarePerMonitorV2);
        TemplateSet templates = ProbeTemplate.Load(repo);
        GpeditSession.Run("probe", templates, ctx => new ProbeCommand(repo, ctx).Execute());
    }

    private void Execute()
    {
        Directory.CreateDirectory(shotDir);
        var header = new JsonObject
        {
            ["tool"] = "gpedit probe",
            ["startedUtc"] = DateTime.UtcNow.ToString("O"),
            ["os"] = Report.OsInfo(),
            ["systemDpi"] = Win32.GetDpiForSystem(),
            ["elevated"] = SessionGuards.IsElevated(),
            ["templates"] = new JsonArray(ProbeTemplateNames(repo)),
            ["plan"] = new JsonArray(Plan.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        };
        recorder = new ProbeRecorder(Path.Combine(ctx.Paths.OutputDir, "probe.json"), header);

        Step("baseline-files", () => lastFiles = PolFiles.Snapshot(ctx.Paths, "baseline"));
        if (Step("start-mmc", StartMmc))
        {
            Step("wait-populated", WaitPopulated);
            Step("uia-main-tree", () => ProbeUia.Tree(Main(), 10, 3500, rawView: true));
            Step("msaa", Msaa);
            Step("menu-bar", MenuBar);
            Step("toolbar", Toolbar);
            Step("panes", Panes);
            Step("navigate", Navigate);
            Step("result-list", ResultList);
            Step("options-dialog", OptionsDialog);

            (string Policy, string State)[] sequence =
            {
                (Zeta, "Enabled"),
                (Alpha, "Enabled"),
                (Zeta, "Disabled"),
                (Alpha, "Disabled"),
                (Zeta, "Not Configured"),
                (Alpha, "Not Configured"),
            };
            for (int i = 0; i < sequence.Length; i++)
            {
                (string policy, string state) = sequence[i];
                string label = $"byteorder-{i + 1}-{policy.Replace("Probe ", string.Empty, StringComparison.Ordinal).ToLowerInvariant()}-{state.Replace(' ', '-').ToLowerInvariant()}";
                Step(label, () => SetPolicy(policy, state, label));
            }
        }

        if (mmc is not null)
        {
            Step("close-mmc", CloseMmc);
        }

        Step("final-files", () => PolFiles.Snapshot(ctx.Paths, "final"));
        recorder.Root["finishedUtc"] = DateTime.UtcNow.ToString("O");
        recorder.Flush();
        Console.WriteLine($"probe: wrote {Path.Combine(ctx.Paths.OutputDir, "probe.json")} ({recorder.Failed} failed steps)");
    }

    private static JsonNode?[] ProbeTemplateNames(RepoPaths repo) =>
        ProbeTemplate.Load(repo).Items.Select(i => (JsonNode?)JsonValue.Create(i.RelativePath)).ToArray();

    private bool Step(string name, Func<JsonNode?> action)
    {
        ctx.ThrowIfCancelled();
        return recorder.Step(name, action);
    }

    private AutomationElement Main()
    {
        if (mainElement is null || mmc is null)
        {
            throw new ProbeSkipException("the gpedit window was not found");
        }

        if (mmc.HasExited)
        {
            throw new ProbeSkipException("mmc.exe has exited");
        }

        return mainElement;
    }

    private int Pid => mmc?.Id ?? throw new ProbeSkipException("mmc.exe was not started");

    private JsonNode StartMmc()
    {
        mmc = ctx.StartMmc(ctx.Paths.GpeditMsc);
        int pid = mmc.Id;
        WinInfo? window = ProbeUia.Wait(() =>
        {
            if (mmc.HasExited)
            {
                throw new InvalidOperationException($"mmc.exe exited with code {mmc.ExitCode} before its window appeared");
            }

            List<WinInfo> visible = ProbeWindows.TopLevel(pid).Where(w => w.Visible && w.Rect.Width > 0).ToList();
            return visible.FirstOrDefault(w => w.Class == "MMCMainFrame")
                ?? visible.FirstOrDefault(w => w.Title.Contains("Group Policy", StringComparison.OrdinalIgnoreCase));
        }, TimeSpan.FromSeconds(90));

        if (window is null)
        {
            var seen = new JsonArray();
            foreach (WinInfo w in ProbeWindows.TopLevel(pid).Where(w => w.Visible))
            {
                seen.Add(ProbeWindows.ToJson(w));
                Shot(w.Hwnd, "start-timeout-" + w.Hwnd.ToString("X"));
            }

            throw new TimeoutException("no gpedit main window within 90 seconds. Visible windows of the process: " + seen.ToJsonString());
        }

        main = window.Hwnd;
        mainElement = AutomationElement.FromHandle(main);
        ProbeInput.Foreground(main);
        return new JsonObject
        {
            ["pid"] = pid,
            ["window"] = ProbeWindows.ToJson(window),
            ["windowDpi"] = Win32.GetDpiForWindow(main),
            ["uiaName"] = mainElement.Current.Name,
            ["uiaFramework"] = mainElement.Current.FrameworkId,
            ["topLevelWindows"] = new JsonArray(ProbeWindows.TopLevel(pid).Select(w => (JsonNode?)ProbeWindows.ToJson(w)).ToArray()),
            ["screenshot"] = Shot(main, "main-start"),
        };
    }

    private AutomationElement? FindTree()
    {
        AutomationElement root = Main();
        return ProbeUia.OfType(root, ControlType.Tree).FirstOrDefault() ?? ProbeUia.OfClass(root, "SysTreeView32").FirstOrDefault();
    }

    private JsonNode WaitPopulated()
    {
        AutomationElement? tree = ProbeUia.Wait(FindTree, TimeSpan.FromSeconds(30));
        if (tree is null)
        {
            throw new TimeoutException("no tree control found through UI Automation within 30 seconds");
        }

        List<AutomationElement>? items = ProbeUia.Wait(() =>
        {
            List<AutomationElement> found = ProbeUia.OfType(tree, ControlType.TreeItem);
            return found.Count > 0 ? found : null;
        }, TimeSpan.FromSeconds(30));
        return new JsonObject { ["tree"] = ProbeUia.Describe(tree), ["treeItemCount"] = items?.Count ?? 0 };
    }

    private JsonNode Msaa()
    {
        AutomationElement root = Main();
        var children = new JsonArray();
        int emptyUiaNamedMsaa = 0;
        List<WinInfo> childWindows = ProbeWindows.Children(main);
        foreach (WinInfo w in childWindows)
        {
            string? uiaName = null;
            try
            {
                uiaName = AutomationElement.FromHandle(w.Hwnd).Current.Name;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                uiaName = "<unavailable>";
            }

            string? msaaName = ProbeMsaa.NameOf(w.Hwnd);
            if (string.IsNullOrEmpty(uiaName) && !string.IsNullOrEmpty(msaaName))
            {
                emptyUiaNamedMsaa++;
            }

            children.Add(new JsonObject { ["window"] = ProbeWindows.ToJson(w), ["uiaName"] = uiaName, ["msaaName"] = msaaName });
        }

        var controls = new JsonArray();
        foreach (string className in new[] { "SysTreeView32", "SysListView32", "SysHeader32", "ToolbarWindow32", "msctls_statusbar32", "SysTabControl32" })
        {
            foreach (WinInfo w in childWindows.Where(c => c.Class == className))
            {
                JsonObject msaa = ProbeMsaa.DescribeWindow(w.Hwnd);
                int? uiaChildren = null;
                try
                {
                    uiaChildren = AutomationElement.FromHandle(w.Hwnd).FindAll(TreeScope.Children, Condition.TrueCondition).Count;
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
                {
                    uiaChildren = null;
                }

                controls.Add(new JsonObject { ["class"] = className, ["window"] = ProbeWindows.ToJson(w), ["msaa"] = msaa, ["uiaChildCount"] = uiaChildren });
            }
        }

        IntPtr hmenu = Win32.GetMenu(main);
        var hmenuNames = new JsonArray();
        if (hmenu != IntPtr.Zero)
        {
            for (int i = 0; i < Win32.GetMenuItemCount(hmenu); i++)
            {
                var sb = new System.Text.StringBuilder(256);
                Win32.GetMenuStringW(hmenu, (uint)i, sb, sb.Capacity, Msg.MF_BYPOSITION);
                hmenuNames.Add(sb.ToString());
            }
        }

        return new JsonObject
        {
            ["mainWindowMsaa"] = ProbeMsaa.DescribeWindow(main),
            ["menuBarMsaa"] = ProbeMsaa.DescribeMenuBar(main),
            ["win32MenuHandle"] = hmenu == IntPtr.Zero ? null : JsonValue.Create("0x" + hmenu.ToString("X")),
            ["win32MenuTopItems"] = hmenuNames,
            ["childWindowCount"] = childWindows.Count,
            ["childWindowsUiaEmptyButMsaaNamed"] = emptyUiaNamedMsaa,
            ["childWindows"] = children,
            ["controls"] = controls,
            ["rootName"] = root.Current.Name,
        };
    }

    private JsonNode MenuBar()
    {
        AutomationElement root = Main();
        int pid = Pid;
        AutomationElement? bar = ProbeUia.OfType(root, ControlType.MenuBar).FirstOrDefault();
        List<AutomationElement> items = bar is null ? new List<AutomationElement>() : ProbeUia.OfType(bar, ControlType.MenuItem, TreeScope.Children);
        if (items.Count == 0)
        {
            items = ProbeUia.OfType(root, ControlType.MenuItem);
        }

        var menus = new JsonArray();
        foreach (AutomationElement item in items)
        {
            ctx.ThrowIfCancelled();
            menus.Add(OpenMenu(item, pid));
        }

        return new JsonObject
        {
            ["menuBarFound"] = bar is not null,
            ["menuBar"] = bar is null ? null : ProbeUia.Describe(bar),
            ["topItemCount"] = items.Count,
            ["menus"] = menus,
        };
    }

    // Opens one top-level menu without invoking any item in it, dumps the popup, and closes it again.
    private JsonObject OpenMenu(AutomationElement item, int pid)
    {
        string name = ProbeUia.Clean(ProbeUia.NameOf(item));
        JsonObject node = ProbeUia.Describe(item);
        string via;
        try
        {
            ProbeInput.Foreground(main);
            if (ProbeUia.Expand(item))
            {
                via = "ExpandCollapse";
            }
            else if (ProbeUia.Invoke(item))
            {
                via = "Invoke";
            }
            else
            {
                string access = item.Current.AccessKey ?? string.Empty;
                char key = access.Length > 0 ? access[^1] : name.FirstOrDefault(char.IsLetter);
                ProbeInput.AltKey(char.ToUpperInvariant(key));
                via = "Alt+" + char.ToUpperInvariant(key);
            }
        }
        catch (Exception ex)
        {
            node["openError"] = ex.GetType().Name + ": " + ex.Message;
            return node;
        }

        Thread.Sleep(600);
        node["openedVia"] = via;
        var popups = new JsonArray();
        string shotName = "menu-" + string.Concat(name.Where(char.IsLetterOrDigit));
        foreach (WinInfo popup in ProbeWindows.PopupMenus(pid))
        {
            popups.Add(new JsonObject
            {
                ["window"] = ProbeWindows.ToJson(popup),
                ["tree"] = ProbeUia.Tree(AutomationElement.FromHandle(popup.Hwnd), 4, 200, rawView: false),
                ["screenshot"] = Shot(popup.Hwnd, shotName),
            });
        }

        node["popups"] = popups;
        node["itemChildren"] = new JsonArray(ProbeUia.OfType(item, ControlType.MenuItem, TreeScope.Children).Take(60).Select(c => (JsonNode?)ProbeUia.Describe(c)).ToArray());

        try
        {
            ProbeUia.Collapse(item);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException)
        {
            node["collapseError"] = ex.Message;
        }

        for (int i = 0; i < 4 && ProbeWindows.PopupMenus(pid).Count > 0; i++)
        {
            ProbeInput.Foreground(main);
            ProbeInput.Key(ProbeInput.VkEscape);
            Thread.Sleep(250);
        }

        if (via.StartsWith("Alt+", StringComparison.Ordinal))
        {
            ProbeInput.Key(ProbeInput.VkEscape);
        }

        node["closed"] = ProbeWindows.PopupMenus(pid).Count == 0;
        return node;
    }

    private JsonNode Toolbar()
    {
        AutomationElement root = Main();
        List<AutomationElement> bars = ProbeUia.OfType(root, ControlType.ToolBar);
        if (bars.Count == 0)
        {
            bars = ProbeUia.OfClass(root, "ToolbarWindow32");
        }

        var result = new JsonArray();
        foreach (AutomationElement bar in bars)
        {
            result.Add(new JsonObject
            {
                ["toolbar"] = ProbeUia.Describe(bar),
                ["buttons"] = new JsonArray(ProbeUia.OfType(bar, ControlType.Button).Select(b => (JsonNode?)ProbeUia.Describe(b)).ToArray()),
            });
        }

        return new JsonObject { ["toolbarCount"] = bars.Count, ["toolbars"] = result };
    }

    private JsonNode Panes()
    {
        AutomationElement root = Main();
        AutomationElement? tree = FindTree();
        List<AutomationElement> lists = ProbeUia.OfClass(root, "SysListView32");
        List<AutomationElement> statusBars = ProbeUia.OfType(root, ControlType.StatusBar);
        if (statusBars.Count == 0)
        {
            statusBars = ProbeUia.OfClass(root, "msctls_statusbar32");
        }

        return new JsonObject
        {
            ["tree"] = tree is null ? null : ProbeUia.Describe(tree),
            ["treeItems"] = tree is null
                ? null
                : new JsonArray(ProbeUia.OfType(tree, ControlType.TreeItem).Take(200).Select(i => (JsonNode?)ProbeUia.Describe(i)).ToArray()),
            ["lists"] = new JsonArray(lists.Select(l => (JsonNode?)DumpList(l)).ToArray()),
            ["statusBars"] = new JsonArray(statusBars.Select(s => (JsonNode?)new JsonObject
            {
                ["statusBar"] = ProbeUia.Describe(s),
                ["texts"] = new JsonArray(ProbeUia.Find(s, TreeScope.Descendants, Condition.TrueCondition).Select(t => (JsonNode?)ProbeUia.Trim(ProbeUia.NameOf(t))).ToArray()),
            }).ToArray()),
            ["tabs"] = new JsonArray(ProbeUia.OfType(root, ControlType.TabItem).Select(t => (JsonNode?)ProbeUia.Describe(t)).ToArray()),
            ["htmlHosts"] = new JsonArray(ProbeUia.OfClass(root, "Internet Explorer_Server").Select(h => (JsonNode?)ProbeUia.Describe(h)).ToArray()),
        };
    }

    private static JsonObject DumpList(AutomationElement list)
    {
        List<AutomationElement> children = ProbeUia.Find(list, TreeScope.Children, Condition.TrueCondition);
        var headers = new JsonArray();
        var rows = new JsonArray();
        foreach (AutomationElement child in children.Take(320))
        {
            JsonObject description = ProbeUia.Describe(child);
            string type = description["type"]?.GetValue<string>() ?? string.Empty;
            if (type.Contains("Header", StringComparison.Ordinal))
            {
                headers.Add(description);
                foreach (AutomationElement headerItem in ProbeUia.OfType(child, ControlType.HeaderItem))
                {
                    headers.Add(ProbeUia.Describe(headerItem));
                }

                continue;
            }

            description["cells"] = new JsonArray(ProbeUia.Find(child, TreeScope.Children, Condition.TrueCondition).Take(12).Select(c => (JsonNode?)ProbeUia.Trim(ProbeUia.NameOf(c))).ToArray());
            rows.Add(description);
        }

        return new JsonObject
        {
            ["list"] = ProbeUia.Describe(list),
            ["childCount"] = children.Count,
            ["headers"] = headers,
            ["rows"] = rows,
        };
    }

    private AutomationElement? FindResultList()
    {
        AutomationElement root = Main();
        List<AutomationElement> lists = ProbeUia.OfClass(root, "SysListView32");
        AutomationElement? visible = lists.FirstOrDefault(l => !l.Current.IsOffscreen);
        return visible ?? lists.FirstOrDefault() ?? ProbeUia.OfType(root, ControlType.List).FirstOrDefault();
    }

    private JsonNode Navigate()
    {
        AutomationElement tree = FindTree() ?? throw new InvalidOperationException("no tree control");
        string[] path = { "Computer Configuration", "Administrative Templates", "BWC Probe" };
        var hops = new JsonArray();
        AutomationElement current = ProbeUia.OfType(tree, ControlType.TreeItem, TreeScope.Children).FirstOrDefault()
            ?? throw new InvalidOperationException("the tree has no root item");
        hops.Add(Hop("root", current));
        foreach (string segment in path)
        {
            ctx.ThrowIfCancelled();
            ProbeUia.Expand(current);
            AutomationElement? parent = current;
            AutomationElement child = ProbeUia.Wait(() => ProbeUia.ByName(parent, ControlType.TreeItem, segment, prefix: true, TreeScope.Children), TimeSpan.FromSeconds(20))
                ?? ProbeUia.ByName(tree, ControlType.TreeItem, segment, prefix: true)
                ?? throw new InvalidOperationException($"no tree item starting with '{segment}'. Children of the last node: " + ProbeUia.NamesOf(ProbeUia.OfType(parent, ControlType.TreeItem, TreeScope.Children)));
            hops.Add(Hop(segment, child));
            current = child;
        }

        string via = "SelectionItem";
        if (!ProbeUia.Select(current))
        {
            ProbeUia.Click(current, doubleClick: false);
            via = "mouse click";
        }

        Thread.Sleep(1500);
        return new JsonObject
        {
            ["hops"] = hops,
            ["selectedVia"] = via,
            ["screenshot"] = Shot(main, "navigated"),
        };
    }

    private static JsonObject Hop(string segment, AutomationElement item)
    {
        JsonObject d = ProbeUia.Describe(item);
        d["segment"] = segment;
        return d;
    }

    private JsonNode ResultList()
    {
        AutomationElement root = Main();
        AutomationElement? list = FindResultList();
        var result = new JsonObject { ["initial"] = list is null ? new JsonObject { ["found"] = false } : DumpList(list) };

        List<AutomationElement> tabs = ProbeUia.OfType(root, ControlType.TabItem);
        result["tabNames"] = new JsonArray(tabs.Select(t => (JsonNode?)ProbeUia.Trim(ProbeUia.NameOf(t))).ToArray());
        AutomationElement? originallySelected = tabs.FirstOrDefault(t => t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? p) && ((SelectionItemPattern)p).Current.IsSelected);
        var byTab = new JsonObject();
        foreach (string tabName in new[] { "Extended", "Standard" })
        {
            ctx.ThrowIfCancelled();
            AutomationElement? tab = tabs.FirstOrDefault(t => string.Equals(ProbeUia.Clean(ProbeUia.NameOf(t)), tabName, StringComparison.OrdinalIgnoreCase));
            if (tab is null)
            {
                byTab[tabName] = new JsonObject { ["found"] = false };
                continue;
            }

            if (!ProbeUia.Select(tab))
            {
                ProbeUia.Click(tab, doubleClick: false);
            }

            Thread.Sleep(1200);
            AutomationElement? tabList = FindResultList();
            byTab[tabName] = new JsonObject
            {
                ["found"] = true,
                ["list"] = tabList is null ? null : DumpList(tabList),
                ["htmlHosts"] = new JsonArray(ProbeUia.OfClass(root, "Internet Explorer_Server")
                    .Select(h => (JsonNode?)ProbeUia.Tree(h, 6, 250, rawView: true)).ToArray()),
                ["documentCount"] = ProbeUia.OfType(root, ControlType.Document).Count,
                ["screenshot"] = Shot(main, "tab-" + tabName.ToLowerInvariant()),
            };
        }

        result["tabs"] = byTab;
        if (originallySelected is not null)
        {
            ProbeUia.Select(originallySelected);
            Thread.Sleep(600);
            result["reselectedOriginalTab"] = ProbeUia.Clean(ProbeUia.NameOf(originallySelected));
        }

        return result;
    }

    // Opens the setting dialog of the list row whose name contains policy: select the row, then Enter, then a double click if no dialog came.
    private IntPtr OpenPolicy(string policy)
    {
        EnsureNoDialog();
        AutomationElement list = FindResultList() ?? throw new InvalidOperationException("no result list");
        AutomationElement row = ProbeUia.Find(list, TreeScope.Children, Condition.TrueCondition)
            .FirstOrDefault(r => ProbeUia.NameOf(r).Contains(policy, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"no row containing '{policy}'. Rows: " + ProbeUia.NamesOf(ProbeUia.Find(list, TreeScope.Children, Condition.TrueCondition)));
        var known = ProbeWindows.Dialogs(Pid, main).Select(w => w.Hwnd).ToHashSet();

        ProbeInput.Foreground(main);
        if (!ProbeUia.Select(row))
        {
            ProbeUia.Click(row, doubleClick: false);
        }

        ProbeUia.Focus(row);
        Thread.Sleep(300);
        ProbeInput.Key(ProbeInput.VkReturn);
        WinInfo? dialog = ProbeUia.Wait(() => ProbeWindows.Dialogs(Pid, main).FirstOrDefault(w => !known.Contains(w.Hwnd)), TimeSpan.FromSeconds(10));
        if (dialog is null)
        {
            ProbeUia.Click(row, doubleClick: true);
            dialog = ProbeUia.Wait(() => ProbeWindows.Dialogs(Pid, main).FirstOrDefault(w => !known.Contains(w.Hwnd)), TimeSpan.FromSeconds(10));
        }

        if (dialog is null)
        {
            throw new TimeoutException($"no dialog opened for '{policy}' after Enter and after a double click");
        }

        Thread.Sleep(600);
        return dialog.Hwnd;
    }

    private JsonNode OptionsDialog()
    {
        IntPtr dialog = OpenPolicy(Options);
        try
        {
            AutomationElement element = AutomationElement.FromHandle(dialog);
            return new JsonObject
            {
                ["window"] = ProbeWindows.ToJson(ProbeWindows.TopLevel(Pid).First(w => w.Hwnd == dialog)),
                ["screenshot"] = Shot(dialog, "dialog-options"),
                ["uiaTree"] = ProbeUia.Tree(element, 14, 1500, rawView: false),
                ["win32Children"] = new JsonArray(ProbeWindows.Children(dialog).Select(w => (JsonNode?)ProbeWindows.ToJson(w)).ToArray()),
                ["msaa"] = ProbeMsaa.DescribeWindow(dialog),
                ["tabOrder"] = TabOrder(dialog),
            };
        }
        finally
        {
            CancelDialog(dialog);
        }
    }

    // The real tab order: press Tab and record which element has focus, until the order repeats.
    private JsonArray TabOrder(IntPtr dialog)
    {
        var order = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ProbeInput.Foreground(dialog);
        for (int i = 0; i < 60; i++)
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            if (focused is null || focused.Current.ProcessId != Pid)
            {
                order.Add(new JsonObject { ["step"] = i, ["note"] = "focus left the process or is unknown" });
                break;
            }

            AutomationElement.AutomationElementInformation c = focused.Current;
            string key = c.AutomationId + "|" + c.ClassName + "|" + c.Name + "|" + c.BoundingRectangle;
            bool first = seen.Add(key);
            order.Add(new JsonObject
            {
                ["step"] = i,
                ["type"] = c.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal),
                ["name"] = ProbeUia.Trim(c.Name),
                ["automationId"] = c.AutomationId,
                ["className"] = c.ClassName,
                ["rect"] = ProbeUia.Rect(c.BoundingRectangle),
                ["repeat"] = !first,
            });
            if (!first)
            {
                break;
            }

            ProbeInput.Key(ProbeInput.VkTab);
            Thread.Sleep(100);
        }

        return order;
    }

    private static AutomationElement? FindButton(AutomationElement dialog, string text)
    {
        AutomationElement? byName = ProbeUia.ByName(dialog, ControlType.Button, text, prefix: false);
        if (byName is not null)
        {
            return byName;
        }

        string[] ids = { "button" + text, text + "Button", "btn" + text, text };
        return ProbeUia.OfType(dialog, ControlType.Button)
            .FirstOrDefault(b => ids.Contains(b.Current.AutomationId, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsOpen(IntPtr hwnd) => Win32Ext.IsWindow(hwnd) && Win32.IsWindowVisible(hwnd);

    private static bool WaitClosed(IntPtr hwnd, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (IsOpen(hwnd))
        {
            if (DateTime.UtcNow >= end)
            {
                return false;
            }

            Thread.Sleep(150);
        }

        return true;
    }

    // Cancels a dialog: the Cancel button, else Escape. Never presses OK.
    private void CancelDialog(IntPtr dialog)
    {
        if (!IsOpen(dialog))
        {
            return;
        }

        bool invoked = false;
        try
        {
            AutomationElement? cancel = FindButton(AutomationElement.FromHandle(dialog), "Cancel");
            invoked = cancel is not null && ProbeUia.Invoke(cancel);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            invoked = false;
        }

        if (!invoked)
        {
            ProbeInput.Foreground(dialog);
            ProbeInput.Key(ProbeInput.VkEscape);
        }

        if (!WaitClosed(dialog, TimeSpan.FromSeconds(10)))
        {
            throw new InvalidOperationException("a dialog stayed open after Cancel. Later steps that need the list are skipped.");
        }
    }

    private void EnsureNoDialog()
    {
        foreach (WinInfo dialog in ProbeWindows.Dialogs(Pid, main))
        {
            Shot(dialog.Hwnd, "unexpected-dialog-" + dialog.Hwnd.ToString("X"));
            CancelDialog(dialog.Hwnd);
        }
    }

    // Sets one policy's state in its dialog and presses OK, then records how the GPO files changed and in what order.
    private JsonNode SetPolicy(string policy, string state, string label)
    {
        EnsureNoDialog();
        Dictionary<string, string> before = PolFiles.Fingerprint(ctx.Paths);
        IntPtr dialog = OpenPolicy(policy);
        var data = new JsonObject { ["policy"] = policy, ["state"] = state };
        try
        {
            AutomationElement element = AutomationElement.FromHandle(dialog);
            List<AutomationElement> radios = ProbeUia.OfType(element, ControlType.RadioButton);
            data["radios"] = new JsonArray(radios.Select(r => (JsonNode?)ProbeUia.Clean(ProbeUia.NameOf(r))).ToArray());
            AutomationElement radio = radios.FirstOrDefault(r => string.Equals(ProbeUia.Clean(ProbeUia.NameOf(r)), state, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"no radio button named '{state}'");
            string via = "SelectionItem";
            if (!ProbeUia.Select(radio))
            {
                if (ProbeUia.Invoke(radio))
                {
                    via = "Invoke";
                }
                else
                {
                    ProbeUia.Click(radio, doubleClick: false);
                    via = "mouse click";
                }
            }

            Thread.Sleep(300);
            data["radioVia"] = via;
            data["radioSelected"] = radio.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? p) && ((SelectionItemPattern)p).Current.IsSelected;
            data["screenshotBeforeOk"] = Shot(dialog, "dialog-" + label);

            AutomationElement ok = FindButton(element, "OK") ?? throw new InvalidOperationException("no OK button");
            if (!ProbeUia.Invoke(ok))
            {
                ProbeUia.Click(ok, doubleClick: false);
            }

            if (!WaitClosed(dialog, TimeSpan.FromSeconds(15)))
            {
                data["screenshotStuck"] = Shot(dialog, "dialog-stuck-" + label);
                throw new InvalidOperationException("the dialog stayed open 15 seconds after OK");
            }
        }
        catch
        {
            try
            {
                CancelDialog(dialog);
            }
            catch (InvalidOperationException)
            {
                // The original failure is the one to report.
            }

            throw;
        }

        data["fileChangeOrder"] = WaitForFileChanges(before);
        Thread.Sleep(1500);
        JsonObject snapshot = PolFiles.Snapshot(ctx.Paths, label);
        data["diff"] = PolFiles.Diff(lastFiles, snapshot);
        data["after"] = snapshot;
        lastFiles = snapshot;
        return data;
    }

    // Polls file hashes every 50 ms after OK and records when each file first changed (the order classic writes them in).
    private JsonArray WaitForFileChanges(Dictionary<string, string> before)
    {
        var firstSeen = new List<(string File, DateTime At)>();
        DateTime start = DateTime.UtcNow;
        DateTime end = start + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < end)
        {
            Dictionary<string, string> now = PolFiles.Fingerprint(ctx.Paths);
            foreach (string name in PolFiles.Names)
            {
                if (!string.Equals(now[name], before[name], StringComparison.Ordinal) && firstSeen.All(f => f.File != name))
                {
                    firstSeen.Add((name, DateTime.UtcNow));
                    end = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
                }
            }

            Thread.Sleep(50);
        }

        return new JsonArray(firstSeen.Select(f => (JsonNode?)new JsonObject
        {
            ["file"] = f.File,
            ["firstSeenUtc"] = f.At.ToString("O"),
            ["msAfterOk"] = (int)(f.At - start).TotalMilliseconds,
        }).ToArray());
    }

    private JsonNode CloseMmc()
    {
        Process process = mmc ?? throw new ProbeSkipException("mmc.exe was not started");
        if (process.HasExited)
        {
            return new JsonObject { ["alreadyExited"] = true, ["exitCode"] = process.ExitCode };
        }

        int pid = process.Id;
        var data = new JsonObject();
        string via = string.Empty;
        try
        {
            ProbeInput.Foreground(main);
            AutomationElement? file = ProbeUia.ByName(Main(), ControlType.MenuItem, "File", prefix: false);
            if (file is not null && ProbeUia.Expand(file))
            {
                Thread.Sleep(600);
                foreach (WinInfo popup in ProbeWindows.PopupMenus(pid))
                {
                    AutomationElement? exit = ProbeUia.ByName(AutomationElement.FromHandle(popup.Hwnd), ControlType.MenuItem, "Exit", prefix: true);
                    if (exit is not null && ProbeUia.Invoke(exit))
                    {
                        via = "File > Exit";
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ProbeSkipException)
        {
            data["menuError"] = ex.Message;
        }

        if (via.Length == 0)
        {
            Win32.PostMessageW(main, Msg.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            via = "WM_CLOSE";
        }

        data["via"] = via;
        var dialogs = new JsonArray();
        DateTime end = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < end && !process.HasExited)
        {
            List<WinInfo> asked = ProbeWindows.Dialogs(pid, main);
            if (asked.Count > 0)
            {
                foreach (WinInfo w in asked)
                {
                    dialogs.Add(new JsonObject
                    {
                        ["window"] = ProbeWindows.ToJson(w),
                        ["tree"] = ProbeUia.Tree(AutomationElement.FromHandle(w.Hwnd), 8, 200, rawView: false),
                        ["screenshot"] = Shot(w.Hwnd, "close-dialog-" + w.Hwnd.ToString("X")),
                    });
                }

                break;
            }

            Thread.Sleep(300);
        }

        data["exited"] = process.HasExited;
        data["exitCode"] = process.HasExited ? process.ExitCode : null;
        data["askedSomething"] = dialogs.Count > 0;
        data["dialogs"] = dialogs;
        if (!process.HasExited)
        {
            data["note"] = "mmc.exe is still running. The session restore closes it (the question, if any, is not answered by the probe).";
        }

        return data;
    }

    // Two screenshots: the window's own paint (PrintWindow) and a copy of its screen area. Either may fail on its own.
    private string Shot(IntPtr hwnd, string name)
    {
        var notes = new List<string>();
        string own = Path.Combine(shotDir, name + ".png");
        try
        {
            Report.SaveWindow(hwnd, own);
            notes.Add("probe/" + name + ".png");
        }
        catch (Exception ex)
        {
            notes.Add("printwindow failed: " + ex.Message);
        }

        try
        {
            if (!Win32.GetWindowRect(hwnd, out NativeRect rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                throw new InvalidOperationException("empty window rectangle");
            }

            using var bitmap = new Bitmap(rect.Width, rect.Height);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(rect.Width, rect.Height));
            }

            string screen = Path.Combine(shotDir, name + "-screen.png");
            bitmap.Save(screen, System.Drawing.Imaging.ImageFormat.Png);
            notes.Add("probe/" + name + "-screen.png");
        }
        catch (Exception ex)
        {
            notes.Add("screen copy failed: " + ex.Message);
        }

        return string.Join("; ", notes);
    }
}
