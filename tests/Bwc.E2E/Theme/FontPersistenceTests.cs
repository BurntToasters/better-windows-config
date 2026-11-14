using System.Text.Json;
using Bwc.E2E.AppServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Theme;

/// <summary>
/// Chooses a face, bold italic style, and size with the real ChooseFont dialog from View > Font, closes the
/// app, starts it again on the same data root, and checks that the saved settings hold the choice and that
/// the next Font dialog opens on it. The dialog reads the live DenseMetrics face, so a restored choice shows.
/// </summary>
[TestClass]
public sealed class FontPersistenceTests
{
    private const int MenuView = 514;
    private const int CommandFont = 699;
    private const string NativeDialogClass = "#32770";
    private const string FontDialogTitle = "Font";
    private const int FaceComboId = 1136;
    private const int StyleComboId = 1137;
    private const int SizeComboId = 1138;
    private const int DialogOkId = 1;
    private const int DialogCancelId = 2;
    private const string ChosenFace = "Courier New";
    private const string ChosenStyle = "Bold Italic";
    private const string ChosenSize = "14";
    private const int ChosenWeight = 700;
    private const double ChosenSizeDip = 14 * 96.0 / 72.0;
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Theme")]
    [TestCategory("E2EOnly")]
    public void Font_FaceStyleAndSizePersistAcrossRestart()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("font.persist.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        string dataRoot = ClassicDialogTests.FreshDataRoot(config, "fontpersist");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
        };

        try
        {
            using (var session = new AppSession(config, config.RegeditExePath, environment: environment))
            {
                bool dialogShown = OpenFontDialog(session, out IntPtr dialog);
                Record("font.persist.open", dialogShown, $"View > Font opened the ChooseFont dialog={dialogShown}.");
                if (!dialogShown)
                {
                    FailIfNeeded();
                    return;
                }

                bool typed = SetComboText(dialog, FaceComboId, ChosenFace) &&
                    SetComboText(dialog, StyleComboId, ChosenStyle) &&
                    SetComboText(dialog, SizeComboId, ChosenSize);
                bool okClicked = typed && ClickControl(dialog, DialogOkId);
                bool closed = okClicked && AppServicesSupport.WaitUntil(
                    () => !AppServicesSupport.IsWindow(dialog),
                    DialogTimeout);
                bool enabled = closed && AppServicesSupport.WaitUntil(
                    () => session.IsEnabled,
                    DialogTimeout);
                Record(
                    "font.persist.choose",
                    typed && okClicked && closed && enabled,
                    $"typed={typed}, OK={okClicked}, dialog closed={closed}, owner enabled={enabled}.");
            }

            bool saved = ReadSavedFont(dataRoot, out SavedFont saw);
            bool savedMatches = saved &&
                string.Equals(saw.Face, ChosenFace, StringComparison.Ordinal) &&
                saw.Weight == ChosenWeight &&
                saw.Italic &&
                Math.Abs(saw.SizeDip - ChosenSizeDip) < 0.05;
            Record(
                "font.persist.saved",
                savedMatches,
                $"settings file found={saved}, face='{saw.Face}', weight={saw.Weight}, italic={saw.Italic}, size={saw.SizeDip:0.###} DIP.");

            using (var session = new AppSession(config, config.RegeditExePath, environment: environment))
            {
                bool dialogShown = OpenFontDialog(session, out IntPtr dialog);
                string face = dialogShown ? ComboText(dialog, FaceComboId) : "(no dialog)";
                string style = dialogShown ? ComboText(dialog, StyleComboId) : "(no dialog)";
                string size = dialogShown ? ComboText(dialog, SizeComboId) : "(no dialog)";
                bool restored = dialogShown &&
                    string.Equals(face, ChosenFace, StringComparison.Ordinal) &&
                    string.Equals(style, ChosenStyle, StringComparison.Ordinal) &&
                    string.Equals(size, ChosenSize, StringComparison.Ordinal);
                Record(
                    "font.persist.restored",
                    restored,
                    $"after restart the Font dialog shows face='{face}', style='{style}', size='{size}'.");

                if (dialogShown)
                {
                    _ = ClickControl(dialog, DialogCancelId);
                    _ = AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsWindow(dialog), DialogTimeout);
                }
            }
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        FailIfNeeded();
    }

    /// <summary>Alt+V opens View, then the Font item is clicked with the mouse. Returns the dialog handle.</summary>
    private static bool OpenFontDialog(AppSession session, out IntPtr dialog)
    {
        dialog = IntPtr.Zero;
        // The View menu is open when its Font item is on screen. A closed menu keeps the item off screen.
        bool itemShown = false;
        for (int attempt = 0; attempt < 2 && !itemShown; attempt++)
        {
            ControlTestSupport.SendKeys(session, "ALT+KEY_V");
            itemShown = ControlTestSupport.WaitUntil(
                () => session.Find("MenuCommand_" + CommandFont) is { } item && !item.Properties.IsOffscreen.ValueOrDefault,
                TimeSpan.FromSeconds(3));
        }

        if (!itemShown)
        {
            return false;
        }

        session.Find("MenuCommand_" + CommandFont)?.Click();
        dialog = AppServicesSupport.WaitForOwnedWindow(session.MainHwnd, NativeDialogClass, FontDialogTitle, DialogTimeout);
        return dialog != IntPtr.Zero;
    }

    /// <summary>
    /// The text in a combo box is held by its edit child. The combo window itself has no text. A combo with no
    /// edit child (drop-down list) returns the combo's own text, which may be empty.
    /// </summary>
    private static string ComboText(IntPtr dialog, int controlId)
    {
        IntPtr combo = AppServicesSupport.ChildOf(dialog, controlId);
        if (combo == IntPtr.Zero)
        {
            return string.Empty;
        }

        // The combo's text is in its edit part. UI Automation reports that edit's value.
        using var automation = new UIA3Automation();
        AutomationElement? edit = automation.FromHandle(combo)
            .FindFirstDescendant(condition => condition.ByControlType(ControlType.Edit));
        return edit is null ? AppServicesSupport.TitleOf(combo) : edit.AsTextBox().Text ?? string.Empty;
    }

    /// <summary>Clicks a combo box, selects its text, and types the new value with the keyboard.</summary>
    private static bool SetComboText(IntPtr dialog, int controlId, string text)
    {
        IntPtr combo = AppServicesSupport.ChildOf(dialog, controlId);
        if (combo == IntPtr.Zero)
        {
            return false;
        }

        var center = AppServicesSupport.CenterOf(combo);
        AppServicesSupport.Click(center.X, center.Y);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(text);
        return true;
    }

    private static bool ClickControl(IntPtr dialog, int controlId)
    {
        IntPtr control = AppServicesSupport.ChildOf(dialog, controlId);
        if (control == IntPtr.Zero)
        {
            return false;
        }

        var center = AppServicesSupport.CenterOf(control);
        AppServicesSupport.Click(center.X, center.Y);
        return true;
    }

    /// <summary>Finds the settings file under the data root and reads the font fields, matching names without case.</summary>
    private static bool ReadSavedFont(string dataRoot, out SavedFont saved)
    {
        saved = new SavedFont(string.Empty, 0, false, 0);
        string? file = Directory.Exists(dataRoot)
            ? Directory.EnumerateFiles(dataRoot, "settings.json", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (file is null)
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
        JsonElement root = document.RootElement;
        string face = StringOf(root, "FontFaceName");
        int weight = IntOf(root, "FontWeight");
        bool italic = BoolOf(root, "FontItalic");
        double size = DoubleOf(root, "FontSize");
        saved = new SavedFont(face, weight, italic, size);
        return true;
    }

    private static JsonElement? PropertyOf(JsonElement root, string name)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string StringOf(JsonElement root, string name) =>
        PropertyOf(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : string.Empty;

    private static int IntOf(JsonElement root, string name) =>
        PropertyOf(root, name) is { ValueKind: JsonValueKind.Number } value ? value.GetInt32() : 0;

    private static double DoubleOf(JsonElement root, string name) =>
        PropertyOf(root, name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : 0;

    private static bool BoolOf(JsonElement root, string name) =>
        PropertyOf(root, name) is { ValueKind: JsonValueKind.True };

    private static void Record(string id, bool pass, string notes) =>
        _ = E2EResults.Record(id, pass, notes);

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Font checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private readonly record struct SavedFont(string Face, int Weight, bool Italic, double SizeDip);
}
