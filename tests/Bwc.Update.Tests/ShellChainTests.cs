using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// The elevated --launch-hub path (update-elevation.md HN9, HN10): IShellWindows -> IServiceProvider -> IShellBrowser ->
// IShellView -> IShellFolderViewDual -> IShellDispatch2.ShellExecute, as typed vtable calls. The objects here are fakes
// of native memory. The slot numbers and ids below are written out from the Windows SDK headers (ShlDisp.h, ExDisp.h,
// servprov.h, ShObjIdl_core.h), not read from the code, so a wrong constant in the code fails the call.
[TestClass]
public sealed unsafe class ShellChainTests
{
    private const int EFail = unchecked((int)0x80004005);
    private const int SFalse = 1;

    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");
    private static readonly Guid IidShellWindows = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    private static readonly Guid IidServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidShellView = new("000214E3-0000-0000-C000-000000000046");
    private static readonly Guid IidFolderViewDual = new("E7A1AF80-4D96-11CF-960C-0080C7F4EE85");
    private static readonly Guid IidShellDispatch2 = new("A4C6892C-3BA9-11D2-9DEA-00C04FB16162");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    private const string HubExe = @"C:\Program Files\Better Windows Config\BetterWindowsConfig.exe";
    private const string HubDir = @"C:\Program Files\Better Windows Config";

    // HN9
    [TestMethod]
    public void Shell_Chain_Slots_Match_Sdk()
    {
        // Slot numbers from the SDK vtables (IShellDispatch2Vtbl and the others), in the order of the names.
        CollectionAssert.AreEqual(
            new[] { 0, 2, 3, 15, 15, 15, 7, 31, 5, 6, 5, 6, 7, 20 },
            new[]
            {
                ComSlots.QueryInterface,
                ComSlots.Release,
                ComSlots.ServiceProviderQueryService,
                ComSlots.ShellWindowsFindWindowSW,
                ComSlots.ShellBrowserQueryActiveShellView,
                ComSlots.ShellViewGetItemObject,
                ComSlots.FolderViewDualGetApplication,
                ComSlots.ShellDispatch2ShellExecute,
                ComSlots.PersistFileLoad,
                ComSlots.PersistFileSave,
                ComSlots.PropertyStoreGetValue,
                ComSlots.PropertyStoreSetValue,
                ComSlots.PropertyStoreCommit,
                ComSlots.ShellLinkSetPath,
            });
        Assert.AreEqual(IidDispatch, ComIds.Dispatch);
        Assert.AreEqual(IidShellWindows, ComIds.ShellWindows);
        Assert.AreEqual(IidServiceProvider, ComIds.ServiceProvider);
        Assert.AreEqual(IidShellBrowser, ComIds.ShellBrowser);
        Assert.AreEqual(IidFolderViewDual, ComIds.ShellFolderViewDual);
        Assert.AreEqual(IidShellDispatch2, ComIds.ShellDispatch2);
        Assert.AreEqual(SidTopLevelBrowser, ComIds.TopLevelBrowserService);
        Assert.AreEqual(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), ComIds.ShellWindowsClass);
        Assert.AreEqual(new Guid("00021401-0000-0000-C000-000000000046"), ComIds.ShellLinkClass);
        Assert.AreEqual(new Guid("0000010B-0000-0000-C000-000000000046"), ComIds.PersistFile);
        Assert.AreEqual(new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), ComIds.PropertyStore);
        CollectionAssert.AreEqual(
            new[] { 8, 1, 0 },
            new[] { ShellChain.SwcDesktop, ShellChain.SwfoNeedDispatch, (int)ShellChain.SvgioBackground });
        Assert.AreEqual(24, sizeof(ComVariant), "a VARIANT is 24 bytes on 64-bit Windows");
    }

    // HN9
    [TestMethod]
    public void Shell_Chain_Calls_Are_Exact()
    {
        using var graph = new Graph();

        bool started = graph.Run(["--update-result", "InstallerFailed"]);

        Assert.IsTrue(started, graph.Describe());
        CollectionAssert.AreEqual(
            new[]
            {
                "FindWindowSW location=3:0 root=0 class=8 options=1",
                "QueryService service=4c96be40-915c-11cf-99d3-00aa004ae837 iid=000214e2-0000-0000-c000-000000000046",
                "QueryActiveShellView",
                "GetItemObject item=0 iid=00020400-0000-0000-c000-000000000046",
                "get_Application",
                "ShellExecute file=" + HubExe + " args=8:--update-result InstallerFailed dir=8:" + HubDir + " operation=8:open show=3:1",
            },
            graph.Calls,
            graph.Describe());
    }

    // HN9
    [TestMethod]
    public void Shell_Chain_No_Arguments_Passes_An_Empty_String()
    {
        using var graph = new Graph();

        Assert.IsTrue(graph.Run([]), graph.Describe());

        StringAssert.Contains(graph.Calls[^1], " args=8: dir=", StringComparison.Ordinal);
    }

    // HN8
    [TestMethod]
    public void Shell_Chain_Releases_Every_Pointer()
    {
        using var graph = new Graph();

        Assert.IsTrue(graph.Run(["--update-result", "InstallerFailed"]), graph.Describe());

        Assert.IsTrue(graph.AllBalanced, graph.Describe());
        Assert.IsTrue(graph.Windows.Releases >= 1, "the windows object the test handed out is the caller's to release");
    }

    // HN10
    [TestMethod]
    [DataRow("FindWindowSW.hr")]
    [DataRow("FindWindowSW.hrWithPointer")]
    [DataRow("FindWindowSW.sFalseNoPointer")]
    [DataRow("FindWindowSW.nullPointer")]
    [DataRow("Desktop.noServiceProvider")]
    [DataRow("QueryService.hr")]
    [DataRow("QueryService.hrWithPointer")]
    [DataRow("QueryService.nullPointer")]
    [DataRow("QueryActiveShellView.hr")]
    [DataRow("QueryActiveShellView.hrWithPointer")]
    [DataRow("QueryActiveShellView.nullPointer")]
    [DataRow("GetItemObject.hr")]
    [DataRow("GetItemObject.hrWithPointer")]
    [DataRow("GetItemObject.nullPointer")]
    [DataRow("Background.noFolderViewDual")]
    [DataRow("get_Application.hr")]
    [DataRow("get_Application.hrWithPointer")]
    [DataRow("get_Application.nullPointer")]
    [DataRow("App.noShellDispatch2")]
    [DataRow("ShellExecute.hr")]
    public void Shell_Chain_Failure_Starts_Nothing(string failure)
    {
        using var graph = new Graph { Failure = failure };

        bool started = graph.Run(["--update-result", "InstallerFailed"]);

        Assert.IsFalse(started, failure + ": " + graph.Describe());
        Assert.IsFalse(
            graph.Calls.Any(call => call.StartsWith("ShellExecute", StringComparison.Ordinal)) && failure != "ShellExecute.hr",
            failure + ": ShellExecute was reached");
        Assert.IsTrue(graph.AllBalanced, failure + ": " + graph.Describe());
    }

    // HN10: a step later than the failing one is never reached.
    [TestMethod]
    public void Shell_Chain_Stops_At_The_First_Failure()
    {
        using var graph = new Graph { Failure = "QueryActiveShellView.hr" };

        Assert.IsFalse(graph.Run([]));

        Assert.AreEqual(3, graph.Calls.Count, graph.Describe());
        Assert.AreEqual("QueryActiveShellView", graph.Calls[^1]);
    }

    private sealed class Graph : IDisposable
    {
        public Graph()
        {
            Windows = new FakeCom("windows", IidShellWindows, IidDispatch);
            Desktop = new FakeCom("desktop", IidDispatch, IidServiceProvider);
            Browser = new FakeCom("browser", IidShellBrowser);
            View = new FakeCom("view", IidShellView);
            Background = new FakeCom("background", IidDispatch, IidFolderViewDual);
            App = new FakeCom("app", IidDispatch, IidShellDispatch2);
        }

        public string? Failure { get; init; }

        public FakeCom Windows { get; }

        public FakeCom Desktop { get; private set; }

        public FakeCom Browser { get; }

        public FakeCom View { get; }

        public FakeCom Background { get; private set; }

        public FakeCom App { get; private set; }

        public List<string> Calls { get; } = [];

        public bool AllBalanced => All.All(fake => fake.IsBalanced);

        private IEnumerable<FakeCom> All => [Windows, Desktop, Browser, View, Background, App];

        public bool Run(string[] arguments)
        {
            Wire();
            bool started;
            using (ComObject windows = ComObject.Own(Windows.HandOut())!)
            {
                started = ShellChain.Run(windows, HubExe, arguments, HubDir);
            }

            return started;
        }

        public string Describe() => string.Join(" | ", All.Select(fake => fake.Balance())) + " || " + string.Join(" ; ", Calls);

        public void Dispose()
        {
            foreach (FakeCom fake in All)
            {
                fake.Dispose();
            }
        }

        private bool Is(string name) => string.Equals(Failure, name, StringComparison.Ordinal);

        private void Wire()
        {
            if (Is("Desktop.noServiceProvider"))
            {
                Desktop.Dispose();
                Desktop = new FakeCom("desktop", IidDispatch);
            }

            if (Is("Background.noFolderViewDual"))
            {
                Background.Dispose();
                Background = new FakeCom("background", IidDispatch);
            }

            if (Is("App.noShellDispatch2"))
            {
                App.Dispose();
                App = new FakeCom("app", IidDispatch);
            }

            Windows.Set(15, new FindWindowSwFn(FindWindow));
            Desktop.Set(3, new QueryServiceFn(QueryService));
            Browser.Set(15, new OutPointerFn(QueryActiveShellView));
            View.Set(15, new GetItemObjectFn(GetItemObject));
            Background.Set(7, new OutPointerFn(GetApplication));
            App.Set(31, new ShellExecuteFn(ShellExecute));
        }

        // An out pointer that a failing call still sets is the "hrWithPointer" case: the code must release it.
        private int Answer(string step, FakeCom next, nint* result)
        {
            if (Is(step + ".hr"))
            {
                *result = 0;
                return EFail;
            }

            if (Is(step + ".hrWithPointer"))
            {
                *result = next.HandOut();
                return EFail;
            }

            if (Is(step + ".nullPointer"))
            {
                *result = 0;
                return 0;
            }

            if (Is(step + ".sFalseNoPointer"))
            {
                *result = 0;
                return SFalse;
            }

            *result = next.HandOut();
            return 0;
        }

        private static string Text(ComVariant* variant) =>
            variant->Vt == 8 ? "8:" + Marshal.PtrToStringBSTR(variant->Value) : variant->Vt + ":" + variant->Value;

        private int FindWindow(nint self, ComVariant* location, ComVariant* root, int windowClass, int* handle, int options, nint* dispatch)
        {
            Calls.Add($"FindWindowSW location={location->Vt}:{location->Value} root={root->Vt} class={windowClass} options={options}");
            *handle = 0;
            return Answer("FindWindowSW", Desktop, dispatch);
        }

        private int QueryService(nint self, Guid* service, Guid* iid, nint* result)
        {
            Calls.Add($"QueryService service={service->ToString("D")} iid={iid->ToString("D")}");
            return Answer("QueryService", Browser, result);
        }

        private int QueryActiveShellView(nint self, nint* result)
        {
            Calls.Add("QueryActiveShellView");
            return Answer("QueryActiveShellView", View, result);
        }

        private int GetItemObject(nint self, uint item, Guid* iid, nint* result)
        {
            Calls.Add($"GetItemObject item={item} iid={iid->ToString("D")}");
            return Answer("GetItemObject", Background, result);
        }

        private int GetApplication(nint self, nint* result)
        {
            Calls.Add("get_Application");
            return Answer("get_Application", App, result);
        }

        private int ShellExecute(nint self, nint file, ComVariant* args, ComVariant* directory, ComVariant* operation, ComVariant* show)
        {
            Calls.Add(
                $"ShellExecute file={Marshal.PtrToStringBSTR(file)} args={Text(args)} dir={Text(directory)} operation={Text(operation)} show={Text(show)}");
            return Is("ShellExecute.hr") ? EFail : 0;
        }
    }
}
