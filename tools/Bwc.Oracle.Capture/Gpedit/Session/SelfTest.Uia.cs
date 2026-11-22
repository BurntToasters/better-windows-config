using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Matrix;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// The decisions of failure modes 40 to 44 of the driver document, on plain values and without classic: which lookup route a list size takes, which
// windows a narrow lookup uses, how far a header scrolls, what a hidden node is, whether the keyboard focus is on a control, and how a press of OK
// that leaves the dialog open is judged. The ways each could fail are listed in the document first; these cases were written before the code
// they check.
internal static partial class SelfTest
{
    private static WinInfo Win(int handle, string className, bool visible, int width = 100, int height = 40) =>
        new(new IntPtr(handle), className, string.Empty, visible, IntPtr.Zero, new NativeRect { Left = 0, Top = 0, Right = width, Bottom = height });

    private static void UiaCases(RepoPaths repo, Action<string, Action> Case)
    {
        Case("uia scope: the route follows the row count of the largest visible list", () =>
        {
            Expect(UiaScope.Route(3400) == "hwnd", "All Settings did not take the narrow route");
            Expect(UiaScope.Route(UiaScope.LargeListRows) == "hwnd", "the threshold itself did not take the narrow route");
            Expect(UiaScope.Route(UiaScope.LargeListRows - 1) == "search", "one row below the threshold took the narrow route");
            Expect(UiaScope.Route(300) == "search", "a list of 300 rows took the narrow route");
            Expect(UiaScope.Route(0) == "search", "no rows took the narrow route");
            Expect(UiaScope.Route(-1) == "search", "an unreadable count took the narrow route");
            Expect(UiaScope.LargestVisibleList(Array.Empty<ListSize>()) == 0, "no list gave a row count");
            Expect(UiaScope.LargestVisibleList(new[] { new ListSize(true, true, 12), new ListSize(true, true, 3400), new ListSize(true, true, 40) }) == 3400, "the largest list was not taken");
            Expect(UiaScope.LargestVisibleList(new[] { new ListSize(false, true, 3400), new ListSize(true, false, 3400), new ListSize(true, true, 12) }) == 12,
                "a hidden or off-screen list was counted");
            Expect(UiaScope.LargestVisibleList(new[] { new ListSize(true, true, -1) }) == 0, "an unreadable count was taken as rows");
        });

        Case("uia scope: a narrow lookup takes only the visible windows of its class", () =>
        {
            WinInfo[] windows =
            {
                Win(1, "msctls_statusbar32", true),
                Win(2, "msctls_statusbar32", false),
                Win(3, "AMCCustomTab", true),
                Win(4, "AMCCustomTab", false),
                Win(5, "ToolbarWindow32", true),
                Win(6, "ToolbarWindow32", true),
                Win(7, "ToolbarWindow32", false),
                Win(8, "SysListView32", true),
            };
            Expect(UiaScope.VisibleOfClass(windows, UiaScope.StatusBarClass).Select(w => (int)w.Hwnd).SequenceEqual(new[] { 1 }), "the status bar windows are wrong");
            Expect(UiaScope.VisibleOfClass(windows, UiaScope.TabClass).Select(w => (int)w.Hwnd).SequenceEqual(new[] { 3 }), "the tab windows are wrong");
            Expect(UiaScope.VisibleOfClass(windows, UiaScope.ToolbarClass).Select(w => (int)w.Hwnd).SequenceEqual(new[] { 5, 6 }), "the toolbar windows are wrong");
            Expect(UiaScope.VisibleOfClass(windows, "msctls_STATUSBAR32").Count == 0, "the class match is not case sensitive");
            Expect(UiaScope.VisibleOfClass(Array.Empty<WinInfo>(), UiaScope.TabClass).Count == 0, "no window gave a window");
        });

        Case("uia scope: the route and scroll diagnostics never reach a golden", () =>
        {
            var scrub = new Scrubber(MatrixPlan.Build(repo).Fixture.AllowedStrings, Array.Empty<string>(), false);
            var data = new JsonObject
            {
                ["status"] = new JsonObject { ["text"] = "ZZ-MARKER-status", ["uiaRoute"] = "ZZ-MARKER-route", ["filtered"] = false },
                ["tabs"] = new JsonArray("Extended", "Standard"),
                ["uiaRoute"] = "ZZ-MARKER-route",
                ["scrolledBy"] = "ZZ-MARKER-scroll",
                ["scrollSteps"] = 3,
                ["column"] = "Comment",
            };
            string text = scrub.Clean(data)!.ToJsonString();
            Expect(!text.Contains("ZZ-MARKER-route", StringComparison.Ordinal) && !text.Contains("uiaRoute", StringComparison.Ordinal), "uiaRoute reached the golden data: " + text);
            Expect(!text.Contains("scrolledBy", StringComparison.Ordinal) && !text.Contains("scrollSteps", StringComparison.Ordinal), "a scroll diagnostic reached the golden data: " + text);
            Expect(!text.Contains("ZZ-MARKER-status", StringComparison.Ordinal), "the status sentence was not hashed");
            Expect(text.Contains("Comment", StringComparison.Ordinal) && text.Contains("Standard", StringComparison.Ordinal), "a kept value was lost: " + text);
        });

        Case("header scroll: the distance shows the column and is zero when it is shown", () =>
        {
            Expect(HeaderScroll.Distance(columnLeft: 700, columnWidth: 200, viewWidth: 600, position: 0) == 300, "a column right of the view needs the right edge in view");
            Expect(HeaderScroll.Distance(columnLeft: 300, columnWidth: 200, viewWidth: 600, position: 0) == 0, "a column inside the view needs no scroll");
            Expect(HeaderScroll.Distance(columnLeft: 500, columnWidth: 200, viewWidth: 600, position: 0) == 100, "a column partly right of the view needs the overhang");
            Expect(HeaderScroll.Distance(columnLeft: 100, columnWidth: 200, viewWidth: 600, position: 400) == -300, "a column left of the view needs a scroll back to its left edge");
            Expect(HeaderScroll.Distance(columnLeft: 700, columnWidth: 900, viewWidth: 600, position: 0) == 700, "a column wider than the view needs its left edge at the left");
            Expect(HeaderScroll.Distance(columnLeft: 0, columnWidth: 200, viewWidth: 600, position: 0) == 0, "the first column needs no scroll at the start");
            Expect(HeaderScroll.Distance(columnLeft: 700, columnWidth: 200, viewWidth: 600, position: 300) == 0, "a column that is in view after a scroll needs no more");
            Expect(HeaderScroll.Distance(columnLeft: 700, columnWidth: 200, viewWidth: 0, position: 0) == 0, "an empty view gave a distance");
        });

        Case("header scroll: a header is clickable only with a finite rectangle whose centre is in the list", () =>
        {
            var list = new NativeRect { Left = 100, Top = 50, Right = 500, Bottom = 400 };
            Expect(HeaderScroll.Clickable(new System.Windows.Rect(150, 60, 80, 20), list), "a header inside the list was not clickable");
            Expect(!HeaderScroll.Clickable(System.Windows.Rect.Empty, list), "an empty rectangle was clickable");
            Expect(!HeaderScroll.Clickable(new System.Windows.Rect(double.PositiveInfinity, double.PositiveInfinity, 0, 0), list), "an infinite rectangle was clickable");
            Expect(!HeaderScroll.Clickable(new System.Windows.Rect(150, 60, 0, 20), list), "a rectangle without width was clickable");
            Expect(!HeaderScroll.Clickable(new System.Windows.Rect(480, 60, 80, 20), list), "a header whose centre is right of the list was clickable");
            Expect(!HeaderScroll.Clickable(new System.Windows.Rect(20, 60, 40, 20), list), "a header left of the list was clickable");
            Expect(HeaderScroll.Clickable(new System.Windows.Rect(460, 60, 80, 20), list) == false, "a header whose centre is on the right edge was clickable");
            Expect(HeaderScroll.Clickable(new System.Windows.Rect(420, 60, 80, 20), list), "a header whose centre is just inside the list was not clickable");
        });

        Case("hidden node: the description holds the missing segment, the parent's children and the selection", () =>
        {
            var gap = new TreeGap("Sort cases", new[] { "Administrative Templates", "BWC Oracle" }, new[] { "Basics" });
            JsonObject json = HiddenNode.Describe(gap, new[] { "Computer Configuration", "Administrative Templates", "BWC Oracle" });
            Expect(json["nodeHidden"]!.GetValue<bool>(), "nodeHidden is not true");
            Expect(json["filterOn"]!.GetValue<bool>(), "filterOn is not true");
            Expect(json["segment"]!.GetValue<string>() == "Sort cases", "the missing segment is wrong");
            Expect(string.Join("/", json["parentPath"]!.AsArray().Select(n => n!.GetValue<string>())) == "Administrative Templates/BWC Oracle", "the parent path is wrong");
            Expect(json["parentChildren"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { "Basics" }), "the parent's children are wrong");
            Expect(json["parentChildCount"]!.GetValue<int>() == 1, "the child count is wrong");
            Expect(json["selectedPath"]!.AsArray().Count == 3, "the selected path is wrong");
            Expect(HiddenNode.Describe(gap, null)["selectedPath"] is null, "no selection did not give null");
            var ancestors = HiddenNode.AncestorsNearestFirst(gap).Select(a => string.Join("/", a)).ToList();
            Expect(ancestors.SequenceEqual(new[] { "Administrative Templates/BWC Oracle", "Administrative Templates", string.Empty }), "the ancestors are not nearest first up to the root: " + string.Join(" | ", ancestors));
            var top = new TreeGap("Software Settings", Array.Empty<string>(), new[] { "Windows Settings" });
            Expect(HiddenNode.AncestorsNearestFirst(top).Select(a => a.Count).SequenceEqual(new[] { 0 }), "a gap directly under the root has one ancestor, the root");
        });

        Case("hidden node: only a gap under a parent with the filter known to be on is a hidden node", () =>
        {
            var gap = new TreeGap("Sort cases", new[] { "Administrative Templates", "BWC Oracle" }, new[] { "Basics" });
            Expect(HiddenNode.IsHidden(gap, true), "a gap with the filter on was not hidden");
            Expect(!HiddenNode.IsHidden(gap, false), "a gap with the filter off was taken as hidden");
            Expect(!HiddenNode.IsHidden(gap, null), "a gap with an unreadable filter was taken as hidden");
            Expect(!HiddenNode.IsHidden(null, true), "a failure without a gap was taken as hidden");
        });

        Case("hidden node: the golden keeps the fixture names and hashes any other", () =>
        {
            var scrub = new Scrubber(MatrixPlan.Build(repo).Fixture.AllowedStrings, Array.Empty<string>(), false);
            var gap = new TreeGap("Sort cases", new[] { "Administrative Templates", "BWC Oracle" }, new[] { "Basics", "ZZ-MARKER-machine-node" });
            JsonObject json = HiddenNode.Describe(gap, new[] { "Computer Configuration", "ZZ-MARKER-selected" });
            json["selectedInstead"] = new JsonArray("Administrative Templates", "BWC Oracle");
            string text = scrub.Clean(json)!.ToJsonString();
            Expect(!text.Contains("ZZ-MARKER", StringComparison.Ordinal), "a name that is not BWC text reached the golden data: " + text);
            Expect(text.Contains("Sort cases", StringComparison.Ordinal) && text.Contains("Basics", StringComparison.Ordinal) && text.Contains("BWC Oracle", StringComparison.Ordinal),
                "a fixture name was lost: " + text);
            Expect(text.Contains("nodeHidden", StringComparison.Ordinal) && text.Contains("selectedInstead", StringComparison.Ordinal), "a key was lost: " + text);
        });

        Case("typing focus: the control, a child of it and the old reading are accepted, the host and nothing are not", () =>
        {
            var control = new IntPtr(0x2F306F4);
            var host = new IntPtr(0x2F306F0);
            var none = IntPtr.Zero;
            Expect(TypingFocus.IsOnControl(0, control, control, false), "the keyboard focus on the control was refused");
            Expect(TypingFocus.IsOnControl(0, new IntPtr(0x99), control, true), "the keyboard focus on a child of the control was refused");
            Expect(TypingFocus.IsOnControl((int)control, none, control, false), "the old UI Automation reading alone was refused");
            Expect(TypingFocus.IsOnControl((int)control, host, control, false), "the old UI Automation reading was overruled by another focus window");
            Expect(!TypingFocus.IsOnControl((int)host, host, control, false), "the focus on the host of the control was accepted");
            Expect(!TypingFocus.IsOnControl(0, none, control, false), "no reading at all was accepted");
            Expect(!TypingFocus.IsOnControl(0, new IntPtr(0x77), control, false), "the focus on another window was accepted");
            Expect(!TypingFocus.IsOnControl(0, none, control, true), "a child flag without a focus window was accepted");
        });

        Case("press outcome: OK that stays open with no box and no file change is an outcome, any other stay is a failure", () =>
        {
            Expect(PressOutcome.Judge("OK", closed: true, boxes: 0, filesChanged: false) == PressVerdict.Closed, "a closed OK is not Closed");
            Expect(PressOutcome.Judge("OK", closed: true, boxes: 0, filesChanged: true) == PressVerdict.Closed, "a closed OK that wrote is not Closed");
            Expect(PressOutcome.Judge("OK", closed: false, boxes: 1, filesChanged: false) == PressVerdict.MessageBox, "OK with a box is not MessageBox");
            Expect(PressOutcome.Judge("OK", closed: false, boxes: 1, filesChanged: true) == PressVerdict.MessageBox, "OK with a box and a write is not MessageBox");
            Expect(PressOutcome.Judge("OK", closed: false, boxes: 0, filesChanged: false) == PressVerdict.StayedOpen, "OK that stayed open with no box and no write is not StayedOpen");
            Expect(PressOutcome.Judge("OK", closed: false, boxes: 0, filesChanged: true) == PressVerdict.Failed, "OK that stayed open and wrote was not a failure");
            Expect(PressOutcome.Judge("Cancel", closed: false, boxes: 0, filesChanged: false) == PressVerdict.Failed, "a Cancel that did not close was not a failure");
            Expect(PressOutcome.Judge("Cancel", closed: true, boxes: 0, filesChanged: false) == PressVerdict.Closed, "a closed Cancel is not Closed");
            foreach (string button in new[] { "Apply", "Previous", "Next" })
            {
                Expect(PressOutcome.Judge(button, closed: false, boxes: 0, filesChanged: false) == PressVerdict.NotJudged, button + " was judged");
                Expect(PressOutcome.Judge(button, closed: false, boxes: 0, filesChanged: true) == PressVerdict.NotJudged, button + " with a write was judged");
            }
        });

        Case("press outcome: stayedOpen reaches the golden and the screenshot does not", () =>
        {
            var scrub = new Scrubber(MatrixPlan.Build(repo).Fixture.AllowedStrings, Array.Empty<string>(), false);
            var press = new JsonObject
            {
                ["button"] = "OK",
                ["closed"] = false,
                ["stayedOpen"] = true,
                ["messageBoxes"] = new JsonArray(),
                ["screenshot"] = "ZZ-MARKER-stuck-OK.png",
            };
            JsonObject cleaned = scrub.Clean(press)!.AsObject();
            Expect(cleaned["stayedOpen"]!.GetValue<bool>(), "stayedOpen was lost from the golden data");
            Expect(cleaned["closed"]!.GetValue<bool>() == false, "closed was lost from the golden data");
            Expect(cleaned["messageBoxes"]!.AsArray().Count == 0, "the empty message box list was lost");
            Expect(!cleaned.ToJsonString().Contains("ZZ-MARKER", StringComparison.Ordinal), "the screenshot name reached the golden data");
        });
    }
}
