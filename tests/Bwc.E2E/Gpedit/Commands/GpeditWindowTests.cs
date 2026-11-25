using System.Drawing;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Gpedit.Commands;

/// <summary>
/// Window placement test of the Local Group Policy Editor (GPE-2): the saved rectangle of the settings
/// file in the sandbox data root is the first and only place at which the window is ever visible. A
/// sampler watches every visible top level window of the editor process from before the process starts,
/// so a window that is shown at the default place and moved afterwards is caught as a second rectangle.
/// The failure modes are in docs/failure-modes/gpedit-commands.md, section 8. The test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json (with every rectangle seen) and a picture, and
/// its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditWindowTests
{
    private const int SavedX = 180;
    private const int SavedY = 120;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Window_PlacementBeforeShow()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("window", "placement");
        const string name = "gpedit-window-placement";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();

        int width = Math.Min(1100, GpeditWindowTrace.ScreenWidth - 320);
        int height = Math.Min(640, GpeditWindowTrace.ScreenHeight - 260);
        var saved = new Rectangle(SavedX, SavedY, width, height);
        sandbox.WriteSettingsFile(
            $$"""
            { "WindowX": {{SavedX}}, "WindowY": {{SavedY}}, "WindowWidth": {{width}}, "WindowHeight": {{height}}, "WindowMaximized": false }
            """);

        string processName = Path.GetFileNameWithoutExtension(GuiGate.Config.GpeditExePath);
        using var trace = new GpeditWindowTrace(processName);
        using GpeditRun run = GpeditRun.Open(name, sandbox, placeWindow: false);

        // The placement that used to run after the first show runs in the first idle turns after Loaded; wait it out.
        Thread.Sleep(3000);
        Rectangle final = run.Shell.WindowRect();
        IReadOnlyList<(long Ms, Rectangle Rect)> samples = trace.Samples;

        checks.Record("traced", samples.Count >= 1, $"rectangles seen for the process '{processName}': {samples.Count}.");
        if (samples.Count >= 1)
        {
            Rectangle first = samples[0].Rect;
            checks.Record(
                "firstVisibleIsSaved",
                Near(first, saved, 12, 16),
                $"first visible rectangle={first} at {samples[0].Ms} ms, saved={saved}; the window must not open at another place.");
            checks.Record(
                "noJump",
                samples.All(s => Near(s.Rect, first, 6, 8)),
                $"rectangles seen={string.Join(" -> ", samples.Select(s => $"{s.Rect} @{s.Ms}ms"))}; one rectangle expected.");
        }

        checks.Record("finalIsSaved", Near(final, saved, 12, 16), $"final rectangle={final}, saved={saved}.");

        run.Measure("saved", saved.ToString());
        run.Measure("final", final.ToString());
        run.Measure("samples", samples.Select(s => $"{s.Ms} ms: {s.Rect}").ToArray());
        run.WriteArtifacts("gpedit-window-placement.png");
        checks.AssertAllPassed();
    }

    private static bool Near(Rectangle actual, Rectangle expected, int position, int size) =>
        Math.Abs(actual.Left - expected.Left) <= position &&
        Math.Abs(actual.Top - expected.Top) <= position &&
        Math.Abs(actual.Width - expected.Width) <= size &&
        Math.Abs(actual.Height - expected.Height) <= size;
}
