using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Commands;

/// <summary>
/// The Help menu of the Local Group Policy Editor (MENU-031 to MENU-036, DLG-120, DIFF-006): the entries at every node
/// kind compared with the captures F/Menus_Computer and F/Menus_User by command role (Help Topics, the web site entry,
/// a separator, About, and the snap-in About entry at the two configuration nodes and the placeholder nodes only), the web
/// site entry checked through the E2E counter and never launched, and the native About boxes found, read and closed the
/// way the Registry Editor test of its About box does it. Help Topics and F1 are G2's (Gpedit_Menu_Help_Topics,
/// Gpedit_Keys_F1) and are not repeated. Failure modes: docs/failure-modes/console-options.md, sections 8 to 12. Every test
/// writes artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditHelpTests
{
    /// <summary>The node kinds of the captures, and whether the snap-in About entry shows there.</summary>
    private static readonly (string[] Path, string Id, bool SnapIn)[] Kinds =
    [
        ([Root], "root", false),
        ([Root, Computer], "computerConfiguration", true),
        ([Root, Computer, Admin], "administrativeTemplates", false),
        ([Root, Computer, "Software Settings"], "softwarePlaceholder", true),
        (OraclePath, "category", false),
        ([Root, Computer, "Windows Settings"], "windowsPlaceholder", true),
        (BasicsPath, "categoryWithPolicies", false),
        ([Root, Computer, Admin, AllSettings], "allSettings", false),
        ([Root, User], "userConfiguration", true),
        ([Root, User, Admin], "userAdministrativeTemplates", false),
        ([Root, User, "Software Settings"], "userSoftwarePlaceholder", true),
        ([Root, User, Admin, Oracle, BasicsName], "userCategoryWithPolicies", false),
        ([Root, User, "Windows Settings"], "userWindowsPlaceholder", true),
    ];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Help_Menu_NodeKinds()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("help", "menu.nodes");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-help-menu-nodes");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-help-menu-nodes", sandbox);

        // MENU-032, MENU-036: Help Topics, the web site entry, a separator, About; the snap-in About entry follows About at
        // the two configuration nodes and the placeholders. The order alternates between nodes with and without that entry
        // so a popup built once and kept would show.
        var listings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string[] path, string id, bool snapIn) in Kinds)
        {
            run.GoTo(path);
            _ = run.WaitNode(path);
            listings[id] = CheckHelpPopup(checks, run, id, string.Join(" > ", path), snapIn);
        }

        // With a policy row selected the popup has no snap-in About entry (the last dump of each capture).
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        listings["policyRow"] = CheckHelpPopup(checks, run, "policyRow", "a policy row selected", snapIn: false);
        run.GoTo(Root, User, Admin, Oracle, BasicsName);
        run.SelectRow("Class user flag setting");
        listings["userPolicyRow"] = CheckHelpPopup(checks, run, "userPolicyRow", "a user policy row selected", snapIn: false);

        // The access key of the Help menu opens it (classic: H).
        run.GoTo(Root, Computer);
        _ = run.WaitNode(Root, Computer);
        run.PressAlt(OptionsIds.VkH);
        bool opened = ShellRun.WaitUntil(() => MenuShape.Ids(MenuShape.Read(run.Shell)).Contains(OptionsIds.AboutCommand), TimeSpan.FromSeconds(5));
        checks.Record("altHOpens", opened, "Alt+H opens the Help popup.");
        _ = MenuShape.Close(run.Shell);

        run.Measure("entriesByNode", listings);
        run.WriteArtifacts("gpedit-help-menu-nodes.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Help_WebSite_Entry()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("help", "website");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-help-website");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-help-website", sandbox);

        // MENU-034 as BWC decides it: the entry is there, enabled, labelled in BWC's words, and names the project page. The
        // E2E flavor never launches a browser: it counts the request and publishes the address it would open.
        List<ShapeEntry> entries = MenuShape.OpenTop(run.Shell, OptionsIds.HelpMenu);
        ShapeEntry? web = MenuShape.Entry(entries, OptionsIds.WebSiteCommand);
        checks.Record("present", web is { Enabled: true }, $"the web site entry is present and enabled; entries {MenuShape.Describe(entries)}.");
        checks.Record("label", web is not null && web.Name.Contains("web", StringComparison.OrdinalIgnoreCase), $"the label '{web?.Name}' names the web.");
        _ = MenuShape.Close(run.Shell);

        int windowsBefore = ProcessWindows.Of(run.Shell.Automation, run.Shell.Session.ProcessId).Length;
        int helps = Math.Max(0, run.Count("HelpRequests"));
        int requests = Math.Max(0, run.Count("WebSiteRequests"));
        bool invoked = run.InvokeMenuEntry(OptionsIds.HelpMenu, OptionsIds.WebSiteCommand);
        checks.Record("runs", invoked && run.WaitCount("WebSiteRequests", requests + 1), $"invoked={invoked}, WebSiteRequests={run.Str("WebSiteRequests")}, expected {requests + 1}.");
        string url = run.Str("WebSiteUrl");
        checks.Record("address", url == OptionsIds.WebSiteUrl, $"the address is '{url}', expected '{OptionsIds.WebSiteUrl}'.");
        checks.Record("secure", url.StartsWith("https://", StringComparison.Ordinal), "the address is an https address.");
        checks.Record("notHelp", run.Count("HelpRequests") == helps, $"HelpRequests stayed at {helps}; now {run.Count("HelpRequests")}: the entry is not the Help command.");
        checks.Record("noWindow", run.Shell.Session.IsEnabled && ProcessWindows.Of(run.Shell.Automation, run.Shell.Session.ProcessId).Length == windowsBefore, $"the main window is enabled and the process shows {windowsBefore} windows as before.");

        // The command works from another node and a second time, once per request.
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        invoked = run.InvokeMenuEntry(OptionsIds.HelpMenu, OptionsIds.WebSiteCommand);
        checks.Record("again", invoked && run.WaitCount("WebSiteRequests", requests + 2), $"invoked={invoked}, WebSiteRequests={run.Str("WebSiteRequests")}, expected {requests + 2}.");
        checks.Record("noEdit", run.Count("Edits") <= 0 && run.Count("HelpRequests") == helps, "the policy row was not edited and no Help request came with the second request.");

        run.Measure("webSiteUrl", url);
        run.Measure("webSiteRequests", run.Count("WebSiteRequests"));
        run.WriteArtifacts("gpedit-help-website.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Help_About_Box()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("help", "about");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-help-about");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-help-about", sandbox);

        // DLG-120, DIFF-006: the native box with the product, the version and the copyright, as the Registry Editor shows it.
        string title = AboutProbe.TitleFor(OptionsDialogRead.WindowTitle(run.Shell));
        string version = AboutProbe.ExpectedVersion(GuiGate.Config);
        int helps = Math.Max(0, run.Count("HelpRequests"));
        int windowsBefore = ProcessWindows.Of(run.Shell.Automation, run.Shell.Session.ProcessId).Length;
        for (int round = 1; round <= 2; round++)
        {
            string id = round == 1 ? "first" : "second";
            bool invoked = run.InvokeMenuEntry(OptionsIds.HelpMenu, OptionsIds.AboutCommand);
            IntPtr box = invoked ? AboutProbe.Wait(run, title) : IntPtr.Zero;
            checks.Record(id + ".opens", box != IntPtr.Zero, $"Help > About opened the native box titled '{title}' (invoked={invoked}).");
            if (box == IntPtr.Zero)
            {
                break;
            }

            string text = AboutProbe.TextOf(box);
            checks.Record(id + ".product", text.Contains("Better Windows Config", StringComparison.Ordinal), "the text names the product Better Windows Config.");
            checks.Record(id + ".version", version.Length > 0 && text.Contains("Version " + version, StringComparison.Ordinal), $"the text names the version '{version}': {text.Replace("\n", " | ", StringComparison.Ordinal)}.");
            checks.Record(id + ".copyright", text.Contains("Copyright", StringComparison.Ordinal), "the text carries the copyright line.");
            checks.Record(id + ".modal", !run.Shell.Session.IsEnabled, "the main window is disabled while the box is open.");
            checks.Record(id + ".notHelp", run.Count("HelpRequests") == helps, $"HelpRequests stayed at {helps}; now {run.Count("HelpRequests")}: About is not the Help command.");
            if (round == 1)
            {
                run.Measure("aboutText", text);
            }

            (bool closed, bool byEnter) = AboutProbe.Close(box, "gpedit.help.about." + id + ".foreground");
            checks.Record(id + ".closes", closed, "the box closed.");
            checks.Record(id + ".enter", byEnter, "Enter closed the box (the foreground guard made it the foreground window).");
            checks.Record(id + ".ownerEnabledAgain", ShellRun.WaitUntil(() => run.Shell.Session.IsEnabled, TimeSpan.FromSeconds(5)), "the main window is enabled again.");
            checks.Record(id + ".noLeftover", ProcessWindows.Of(run.Shell.Automation, run.Shell.Session.ProcessId).Length == windowsBefore, "the process shows the windows it showed before: no box is left open.");
        }

        run.WriteArtifacts("gpedit-help-about.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Help_About_SnapIn()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("help", "about.snapin");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-help-about-snapin");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-help-about-snapin", sandbox);

        // MENU-036: the snap-in About entry shows the box of the snap-in, which BWC titles with its own snap-in name.
        run.GoTo(Root, Computer);
        _ = run.WaitNode(Root, Computer);
        string title = AboutProbe.TitleFor(OptionsIds.SnapInAboutName);
        string appTitle = AboutProbe.TitleFor(OptionsDialogRead.WindowTitle(run.Shell));
        string version = AboutProbe.ExpectedVersion(GuiGate.Config);
        int helps = Math.Max(0, run.Count("HelpRequests"));
        int windowsBefore = ProcessWindows.Of(run.Shell.Automation, run.Shell.Session.ProcessId).Length;
        bool invoked = run.InvokeMenuEntry(OptionsIds.HelpMenu, OptionsIds.SnapInAboutCommand);
        IntPtr box = invoked ? AboutProbe.Wait(run, title) : IntPtr.Zero;
        checks.Record("opens", box != IntPtr.Zero, $"Help > snap-in About opened the native box titled '{title}' (invoked={invoked}).");
        if (box != IntPtr.Zero)
        {
            string text = AboutProbe.TextOf(box);
            checks.Record("product", text.Contains("Better Windows Config", StringComparison.Ordinal), "the text names the product Better Windows Config.");
            checks.Record("version", version.Length > 0 && text.Contains("Version " + version, StringComparison.Ordinal), $"the text names the version '{version}': {text.Replace("\n", " | ", StringComparison.Ordinal)}.");
            checks.Record("copyright", text.Contains("Copyright", StringComparison.Ordinal), "the text carries the copyright line.");
            checks.Record("differsFromAbout", !AboutProbe.IsOpen(run, appTitle), $"the box is not the one of Help > About ('{appTitle}').");
            checks.Record("modal", !run.Shell.Session.IsEnabled, "the main window is disabled while the box is open.");
            checks.Record("notHelp", run.Count("HelpRequests") == helps, $"HelpRequests stayed at {helps}: the entry is not the Help command.");
            (bool closed, bool byEnter) = AboutProbe.Close(box, "gpedit.help.about.snapin.foreground");
            checks.Record("closes", closed, "the box closed.");
            checks.Record("enter", byEnter, "Enter closed the box.");
            checks.Record("ownerEnabledAgain", ShellRun.WaitUntil(() => run.Shell.Session.IsEnabled, TimeSpan.FromSeconds(5)), "the main window is enabled again.");
            checks.Record("noLeftover", ProcessWindows.Of(run.Shell.Automation, run.Shell.Session.ProcessId).Length == windowsBefore, "no box is left open.");
        }

        // At a node of the Administrative Templates extension the entry is not in the popup, so no box can be opened.
        run.GoTo(Root, Computer, Admin);
        _ = run.WaitNode(Root, Computer, Admin);
        bool absent = !run.InvokeMenuEntry(OptionsIds.HelpMenu, OptionsIds.SnapInAboutCommand);
        _ = MenuShape.Close(run.Shell);
        checks.Record("absentAtTemplates", absent && !AboutProbe.IsOpen(run, title), "at Administrative Templates the snap-in About entry does not exist, so nothing opens.");

        run.WriteArtifacts("gpedit-help-about-snapin.png");
        checks.AssertAllPassed();
    }

    /// <summary>
    /// Opens the Help popup, records the layout checks of one node kind under <paramref name="id"/> and closes it. Returns the
    /// listing for the measurements file.
    /// </summary>
    private static string CheckHelpPopup(GpeditChecks checks, GpeditRun run, string id, string where, bool snapIn)
    {
        List<ShapeEntry> entries = MenuShape.OpenTop(run.Shell, OptionsIds.HelpMenu);
        string listing = MenuShape.Describe(entries);
        int[] expectedIds = snapIn
            ? [OptionsIds.HelpTopicsCommand, OptionsIds.WebSiteCommand, OptionsIds.AboutCommand, OptionsIds.SnapInAboutCommand]
            : [OptionsIds.HelpTopicsCommand, OptionsIds.WebSiteCommand, OptionsIds.AboutCommand];
        checks.Record(id + ".entries", MenuShape.Ids(entries).SequenceEqual(expectedIds), $"{where}: the Help popup holds {string.Join(", ", expectedIds)} in this order; entries {listing}.");

        // Help Topics and the web site entry, a separator, About (and the snap-in About entry right after it).
        int[] expectedGroups = snapIn ? [2, 2] : [2, 1];
        checks.Record(id + ".groups", MenuShape.Groups(entries).SequenceEqual(expectedGroups), $"{where}: the separator groups are [{string.Join(",", expectedGroups)}]; entries {listing}.");
        checks.Record(id + ".enabled", entries.Count == expectedIds.Length && entries.All(e => e.Enabled), $"{where}: every entry is enabled; entries {listing}.");

        ShapeEntry? topics = MenuShape.Entry(entries, OptionsIds.HelpTopicsCommand);
        ShapeEntry? web = MenuShape.Entry(entries, OptionsIds.WebSiteCommand);
        ShapeEntry? about = MenuShape.Entry(entries, OptionsIds.AboutCommand);
        ShapeEntry? snap = MenuShape.Entry(entries, OptionsIds.SnapInAboutCommand);
        checks.Record(
            id + ".labels",
            topics is not null && topics.Name.Contains("Help Topics", StringComparison.OrdinalIgnoreCase)
                && web is not null && web.Name.Contains("web", StringComparison.OrdinalIgnoreCase)
                && about is not null && about.Name.StartsWith("About", StringComparison.Ordinal)
                && (!snapIn || (snap is not null && snap.Name.Contains(OptionsIds.SnapInAboutName, StringComparison.Ordinal))),
            $"{where}: the labels name Help Topics, the web, About and the snap-in; entries {listing}.");
        checks.Record(
            id + ".accessKeys",
            entries.All(e => e.AccessKey.Length > 0) && entries.Select(e => e.AccessKey.ToUpperInvariant()).Distinct().Count() == entries.Count,
            $"{where}: every entry has its own access key; entries {listing}.");
        _ = MenuShape.Close(run.Shell);
        return listing;
    }
}
