using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Bwc.Oracle.Capture;

// `export-text`: classic File > Export with the "Text Files (*.txt)" filter, Selected branch, on BWC-Oracle.
// Status: NOT MEASURED. Classic wrote no file for this filter (see regedit-export-text.json and the notes there).
// When a file is written, the step keeps its bytes (UTF-16LE with BOM, CRLF), masks only the Last Write Time values
// in the tracked golden, derives the time format from each key's last-write time, and checks the data against the seed.
// Filter names and box texts are Microsoft text and go to local/export-text-text.json (untracked).
internal static class ExportTextCapture
{
    private const string ClassicOracleKey = @"HKEY_CURRENT_USER\Software\BWC-Oracle";
    private const string OracleLastKey = @"Computer\HKEY_CURRENT_USER\Software\BWC-Oracle";
    private const string TimeToken = "<LAST-WRITE-TIME>";
    private const string KeyNameLabel = "Key Name:";
    private const string LastWriteLabel = "Last Write Time:";
    private const string NameLabel = "  Name:";
    private const string DataSizeLabel = "  Data Size:";
    private const string DataLabel = "  Data:";
    private const string ValuePrefix = "Value ";
    private const string NoName = "<NO NAME>";

    // The label column is 19 characters (RT_STRING 8101 to 8108). Continuation lines use the same 19 spaces (8111).
    private const int LabelWidth = 19;
    private const string ContinuationIndent = "                   ";

    private const int AllId = 1280;
    private const int SelectedBranchId = 1281;
    private const int BranchEditId = 1282;
    private const int OkId = 1;
    private const int FileTypeComboId = 1136;
    private const int FileNameComboId = 1148;
    private const int FileNameEditId = 1152;
    private const uint CbGetCurSel = 0x0147;
    private const uint CbGetCount = 0x0146;
    private const uint CbGetLbText = 0x0148;
    private const uint WmCommand = 0x0111;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const int BstChecked = 1;
    private const string RegTextFilter = "Text Files";
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(120);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryInfoKeyW")]
    private static extern int RegQueryInfoKeyW(
        SafeRegistryHandle key,
        IntPtr className,
        IntPtr classNameLength,
        IntPtr reserved,
        IntPtr subKeys,
        IntPtr maxSubKeyLength,
        IntPtr maxClassLength,
        IntPtr values,
        IntPtr maxValueNameLength,
        IntPtr maxValueLength,
        IntPtr securityDescriptor,
        out long lastWriteTime);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageBuffer(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);

    // Expands the file type combo and selects the named item through its UI Automation provider. Nothing is typed, no
    // foreground change and no SendInput. Throws when the combo or the item cannot be reached.
    private static void SelectListItemByAutomation(IntPtr combo, string itemName)
    {
        AutomationElement comboElement = AutomationElement.FromHandle(combo);
        if (comboElement.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object expand))
        {
            ((ExpandCollapsePattern)expand).Expand();
        }

        AutomationElement? item = null;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (item is null && DateTime.UtcNow < deadline)
        {
            item = AutomationElement.RootElement.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, itemName),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)));
            if (item is null)
            {
                Thread.Sleep(200);
            }
        }

        if (item is null)
        {
            throw new InvalidOperationException($"export-text: the list item '{itemName}' was not found through UI Automation.");
        }

        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Thread.Sleep(400);
        if (comboElement.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object collapse))
        {
            var pattern = (ExpandCollapsePattern)collapse;
            Console.WriteLine($"export-text: combo state before collapse {pattern.Current.ExpandCollapseState}");
            if (pattern.Current.ExpandCollapseState != ExpandCollapseState.Collapsed)
            {
                pattern.Collapse();
                Thread.Sleep(400);
            }
        }
    }

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);

        string saveDir = Path.Combine(paths.Root, "artifacts", "oracle", "export-text");
        Directory.CreateDirectory(saveDir);
        string savePath = Path.Combine(saveDir, "bwc-oracle-text.txt");
        if (File.Exists(savePath))
        {
            File.Delete(savePath);
        }

        RegistryGuard.SetAppletValue(string.Empty, "LastKey", RegType.Sz, SeedSpec.Sz(OracleLastKey));
        using RegeditWindow w = RegeditWindow.Start();
        var before = new HashSet<IntPtr>(w.OwnedDialogs());
        w.PostMenuCommand("&File", "&Export...");
        IntPtr dlg = w.WaitForNewDialog(before, OpenTimeout);
        Thread.Sleep(800);
        uint dialogDpi = Win32.GetDpiForWindow(dlg);

        IntPtr all = FindById(w, dlg, AllId);
        IntPtr branch = FindById(w, dlg, SelectedBranchId);
        IntPtr edit = FindById(w, dlg, BranchEditId);
        IntPtr typeCombo = FindById(w, dlg, FileTypeComboId);
        IntPtr fileEdit = FindById(w, dlg, FileNameEditId);
        IntPtr fileName = fileEdit != IntPtr.Zero ? fileEdit : FindById(w, dlg, FileNameComboId);
        IntPtr okButton = FindById(w, dlg, OkId);
        if (all == IntPtr.Zero || branch == IntPtr.Zero || edit == IntPtr.Zero || typeCombo == IntPtr.Zero ||
            fileName == IntPtr.Zero || okButton == IntPtr.Zero)
        {
            throw new InvalidOperationException("export-text: a control of the classic Export dialog was not found.");
        }

        List<string> filters = ComboItems(w, typeCombo);
        int textIndex = filters.FindIndex(f => f.StartsWith(RegTextFilter, StringComparison.OrdinalIgnoreCase));
        if (textIndex < 0)
        {
            throw new InvalidOperationException("export-text: no 'Text Files' item in the file type combo.");
        }

        var steps = new JsonArray { Snapshot(w, dlg, "initial") };

        Click(branch);
        Thread.Sleep(600);
        steps.Add(Snapshot(w, dlg, "after-selected-branch"));

        // Selecting the item through UI Automation is what an accessibility client does. CB_SETCURSEL plus a synthetic
        // CBN_SELCHANGE, and arrow keys posted to the combo, did not change the format classic saves with.
        int startSelection = w.Send(typeCombo, CbGetCurSel, IntPtr.Zero, IntPtr.Zero).ToInt32();
        Console.WriteLine($"export-text: selection before the change {startSelection}");
        SelectListItemByAutomation(typeCombo, filters[textIndex]);
        Thread.Sleep(600);
        Console.WriteLine(
            $"export-text: dialog {dlg:X} open {Win32Ext.IsWindow(dlg)}; dialogs now " +
            string.Join(",", RegeditWindow.TopLevel(w.ProcessId, RegeditWindow.DialogClass).Select(h => $"{h:X}")));
        int filterAfter = w.Send(typeCombo, CbGetCurSel, IntPtr.Zero, IntPtr.Zero).ToInt32();
        Console.WriteLine(
            $"export-text: filter items {filters.Count}, text item {textIndex}, combo selection after the change {filterAfter}");
        Console.WriteLine("export-text: items " + string.Join(" | ", filters));
        steps.Add(Snapshot(w, dlg, "after-text-filter"));

        Win32Ext.SendMessageText(edit, Wm.WM_SETTEXT, IntPtr.Zero, ClassicOracleKey);
        Win32Ext.SendMessageText(fileName, Wm.WM_SETTEXT, IntPtr.Zero, savePath);
        Thread.Sleep(300);
        string branchBack = ReadEdit(w, edit);
        string fileBack = ReadEdit(w, fileName);
        Console.WriteLine($"export-text: edits before save branch '{branchBack}' file '{fileBack}'");

        // The range panel just before OK: radio 1281 checked, edit 1282 holds the oracle key, file name read back.
        bool branchChecked = IsChecked(w, branch);
        bool allChecked = IsChecked(w, all);
        string panelEdit = ReadEdit(w, edit);
        string panelFile = ReadEdit(w, fileName);
        int panelFilter = w.Send(typeCombo, CbGetCurSel, IntPtr.Zero, IntPtr.Zero).ToInt32();
        Log(
            $"before OK: branch checked {branchChecked}, all checked {allChecked}, branch edit '{panelEdit}', " +
            $"file name '{panelFile}', filter selection {panelFilter} '{filters[textIndex]}'");

        // Every dialog of the process that exists now is excluded. A new one after OK is a message box.
        var dialogsBeforeSave = new HashSet<IntPtr>(RegeditWindow.TopLevel(w.ProcessId, RegeditWindow.DialogClass));
        Dictionary<IntPtr, string> known = WindowsOf(w.ProcessId);
        Log("windows before OK: " + string.Join(" | ", known.Values));

        // Check 3: poll for up to 120 s after OK, logging every window of the process that appears, changes or goes.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool saveSent = Win32.PostMessageW(dlg, WmCommand, new IntPtr(OkId), okButton);
        Log($"IDOK posted {saveSent}");

        IntPtr box = IntPtr.Zero;
        string boxCaption = string.Empty;
        string boxText = string.Empty;
        bool fileSeen = false;
        long fileSeenMs = 0;
        bool lastDialogOpen = true;
        bool lastMainEnabled = Win32Ext.IsWindowEnabled(w.Main);
        var newClasses = new List<string>();
        bool mainDisabledSeen = false;
        while (clock.Elapsed < SaveTimeout)
        {
            Dictionary<IntPtr, string> now = WindowsOf(w.ProcessId);
            long ms = clock.ElapsedMilliseconds;
            foreach ((IntPtr h, string description) in now)
            {
                if (!known.TryGetValue(h, out string? previous))
                {
                    newClasses.Add(RegeditWindow.ClassOf(h));
                    Log($"{ms} ms: new window {h:X} {description}");
                }
                else if (previous != description)
                {
                    Log($"{ms} ms: window {h:X} changed to {description}");
                }
            }

            foreach ((IntPtr h, string description) in known)
            {
                if (!now.ContainsKey(h))
                {
                    Log($"{ms} ms: window {h:X} gone ({description})");
                }
            }

            known = now;
            if (box == IntPtr.Zero)
            {
                IntPtr fresh = NewDialog(w, dialogsBeforeSave, dlg);
                if (fresh != IntPtr.Zero)
                {
                    box = fresh;
                    boxCaption = WindowText(box);
                    boxText = StaticText(w, box);
                    Log($"{ms} ms: message box '{boxCaption}': '{boxText}'");
                }
            }

            bool exists = File.Exists(savePath);
            if (exists && !fileSeen)
            {
                fileSeen = true;
                fileSeenMs = ms;
                Log($"{ms} ms: file exists, {new FileInfo(savePath).Length} bytes");
            }

            bool dialogOpen = Win32Ext.IsWindow(dlg);
            if (dialogOpen != lastDialogOpen)
            {
                Log($"{ms} ms: export dialog open {dialogOpen}");
                lastDialogOpen = dialogOpen;
            }

            bool mainEnabled = Win32Ext.IsWindowEnabled(w.Main);
            if (mainEnabled != lastMainEnabled)
            {
                Log($"{ms} ms: regedit main window enabled {mainEnabled}");
                lastMainEnabled = mainEnabled;
                mainDisabledSeen |= !mainEnabled;
            }

            if (fileSeen && !dialogOpen && box == IntPtr.Zero && ms - fileSeenMs > 3000)
            {
                break;
            }

            Thread.Sleep(250);
        }

        Thread.Sleep(500);
        if (box != IntPtr.Zero)
        {
            w.CloseDialog(box);
            Thread.Sleep(500);
        }

        bool dialogOpenAfterSave = Win32Ext.IsWindow(dlg);
        string dialogCaption = WindowText(dlg);
        Log(
            $"end at {clock.ElapsedMilliseconds} ms: file {File.Exists(savePath)} dialog open {dialogOpenAfterSave} " +
            $"box {box != IntPtr.Zero} regedit exited {w.HasExited} code {(w.HasExited ? w.ExitCode : 0)}");
        if (dialogOpenAfterSave)
        {
            w.CloseDialog(dlg);
        }

        bool fileWritten = File.Exists(savePath);
        if (!fileWritten)
        {
            // Not measured: classic wrote no file for this filter. The step records what it saw and passes, so the
            // oracle run is not blocked. No golden is written, and the BWC text formatter has no classic reference yet.
            var notMeasured = new JsonObject
            {
                ["schema"] = 1,
                ["generator"] = "Bwc.Oracle.Capture export-text",
                ["os"] = Report.OsInfo(),
                ["regedit"] = Report.RegeditInfo(),
                ["dpi"] = Report.DpiInfo(systemDpi, dialogDpi, allowDpi),
                ["measured"] = false,
                ["filter"] = new JsonObject
                {
                    ["itemCount"] = filters.Count,
                    ["textItemIndex"] = textIndex,
                    ["selectedBeforeOk"] = panelFilter,
                    ["pattern"] = "*.txt",
                },
                ["panelBeforeOk"] = new JsonObject
                {
                    ["selectedBranchChecked"] = branchChecked,
                    ["allChecked"] = allChecked,
                    ["branchEdit"] = panelEdit,
                    ["fileNameReadBack"] = panelFile.Replace(paths.Root, "<repo>", StringComparison.OrdinalIgnoreCase),
                },
                ["okPosted"] = saveSent,
                ["waitSeconds"] = (int)SaveTimeout.TotalSeconds,
                ["newWindowClasses"] = new JsonArray(newClasses.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                ["mainWindowDisabledSeen"] = mainDisabledSeen,
                ["exportDialogOpenAfterWait"] = dialogOpenAfterSave,
                ["fileWritten"] = false,
                ["notes"] = "Not measured. Classic File > Export with the Text Files (*.txt) item selected, Selected " +
                    "branch on BWC-Oracle and a .txt name, wrote no file and showed no new window or message box within " +
                    "the wait. The same route with the Win9x/NT4 item (index 3) wrote REGEDIT4 at once in a diagnostic " +
                    "run, so the selection route is not the cause. The text writer's trigger is still unknown.",
                ["steps"] = steps,
            };
            Report.WriteJson(Path.Combine(paths.OracleDocs, "regedit-export-text.json"), notMeasured);
            Report.WriteJson(paths.LocalText("export-text-text.json"), new JsonObject
            {
                ["filterItems"] = new JsonArray(filters.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                ["dialogCaption"] = dialogCaption,
                ["messageCaption"] = boxCaption,
                ["messageText"] = boxText,
            });
            return;
        }

        byte[] raw = File.ReadAllBytes(savePath);
        Log($"file header bytes {Convert.ToHexString(raw.AsSpan(0, Math.Min(16, raw.Length)))} length {raw.Length}");
        if (raw.Length < 2 || raw[0] != 0xFF || raw[1] != 0xFE)
        {
            throw new InvalidDataException("export-text: the file does not start with the UTF-16LE byte-order mark.");
        }

        string text = Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        bool roundTrip = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).SequenceEqual(raw);
        int crlf = CountOccurrences(text, "\r\n");
        int lfOnly = text.Count(c => c == '\n') - crlf;
        int crOnly = text.Count(c => c == '\r') - crlf;
        string[] lines = text.Split("\r\n");

        Analysis analysis = Analyze(lines);
        (string format, bool utc, string culture, int matches, int keysChecked) = MatchTimeFormat(analysis.KeyTimes);

        var masked = new string[lines.Length];
        int maskedValues = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith(LastWriteLabel, StringComparison.Ordinal))
            {
                masked[i] = line[..LabelWidth] + TimeToken;
                maskedValues += 1;
            }
            else
            {
                masked[i] = line;
            }
        }

        string goldenText = string.Join("\r\n", masked);
        byte[] golden = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(goldenText)).ToArray();
        File.WriteAllBytes(Path.Combine(paths.ExportsDir, "bwc-oracle-text.txt"), golden);

        DataCheck data = CheckData(analysis);
        var root = new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture export-text",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, dialogDpi, allowDpi),
            ["controls"] = new JsonObject
            {
                ["all"] = AllId,
                ["selectedBranch"] = SelectedBranchId,
                ["branchEdit"] = BranchEditId,
                ["fileTypeCombo"] = FileTypeComboId,
                ["fileName"] = fileEdit != IntPtr.Zero ? FileNameEditId : FileNameComboId,
                ["ok"] = OkId,
            },
            ["fileType"] = new JsonObject
            {
                ["itemCount"] = filters.Count,
                ["textItemIndex"] = textIndex,
                ["selectedAfterChange"] = filterAfter,
                ["pattern"] = "*.txt",
            },
            ["root"] = @"HKEY_CURRENT_USER\Software\BWC-Oracle",
            ["encoding"] = "UTF-16LE",
            ["byteOrderMark"] = "FFFE",
            ["lineEnd"] = "CRLF",
            ["lineEndCounts"] = new JsonObject { ["crlf"] = crlf, ["lfOnly"] = lfOnly, ["crOnly"] = crOnly },
            ["endsWithLineEnd"] = text.EndsWith("\r\n", StringComparison.Ordinal),
            ["roundTripBytesEqual"] = roundTrip,
            ["labelWidth"] = LabelWidth,
            ["continuationIndent"] = ContinuationIndent.Length,
            ["masking"] = new JsonObject
            {
                ["token"] = TimeToken,
                ["label"] = LastWriteLabel,
                ["valuesMasked"] = maskedValues,
                ["otherBytesChanged"] = false,
            },
            ["lastWriteTime"] = new JsonObject
            {
                ["format"] = format,
                ["timeZone"] = utc ? "utc" : "local",
                ["culture"] = culture,
                ["keysChecked"] = keysChecked,
                ["keysMatched"] = matches,
            },
            ["keysExported"] = analysis.KeyCount,
            ["valuesExported"] = analysis.ValueCount,
            ["dataCheck"] = data.ToJson(),
            ["steps"] = steps,
            ["filter"] = new JsonObject
            {
                ["branchEditReadBack"] = branchBack,
                ["fileNameReadBack"] = fileBack,
                ["saveMessagePosted"] = saveSent,
            },
            ["box"] = new JsonObject { ["shown"] = box != IntPtr.Zero },
            ["dialogOpenAfterSave"] = dialogOpenAfterSave,
            ["notes"] = "Classic regedit writes the file; the tracked copy masks only the Last Write Time values. " +
                "The format is the first candidate from the current culture that matches every key's last-write time.",
        };

        Report.WriteJson(Path.Combine(paths.OracleDocs, "regedit-export-text.json"), root);
        Report.WriteJson(paths.LocalText("export-text-text.json"), new JsonObject
        {
            ["filterItems"] = new JsonArray(filters.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["dialogCaption"] = dialogCaption,
            ["messageCaption"] = boxCaption,
            ["messageText"] = boxText,
            ["sampleLastWriteTime"] = analysis.KeyTimes.Count > 0 ? analysis.KeyTimes[0].Observed : string.Empty,
        });
        Console.WriteLine($"export-text: wrote {Path.Combine(paths.ExportsDir, "bwc-oracle-text.txt")}");
    }

    private sealed record KeyTime(string KeyPath, string Observed);

    private sealed record Analysis(
        IReadOnlyList<KeyTime> KeyTimes,
        IReadOnlyList<ValueBlock> Values,
        int KeyCount,
        int ValueCount);

    private sealed record ValueBlock(
        string KeyRelative,
        string Name,
        string DataSize,
        string Data,
        int ContinuationLines,
        int FirstDataChars);

    // Parses the classic text as written. Labels are 19 characters wide; anything else is refused.
    private static Analysis Analyze(string[] lines)
    {
        var keyTimes = new List<KeyTime>();
        var values = new List<ValueBlock>();
        string keyPath = string.Empty;
        int keyCount = 0;
        int valueCount = 0;

        int index = 0;
        while (index < lines.Length)
        {
            string line = lines[index];
            if (line.StartsWith(KeyNameLabel, StringComparison.Ordinal))
            {
                keyPath = ValueOf(line, KeyNameLabel);
                keyCount += 1;
                index += 1;
                continue;
            }

            if (line.StartsWith(LastWriteLabel, StringComparison.Ordinal))
            {
                keyTimes.Add(new KeyTime(keyPath, ValueOf(line, LastWriteLabel)));
                index += 1;
                continue;
            }

            if (line.StartsWith(ValuePrefix, StringComparison.Ordinal) && line.Length > ValuePrefix.Length &&
                char.IsDigit(line[ValuePrefix.Length]))
            {
                valueCount += 1;
                string name = string.Empty;
                string dataSize = string.Empty;
                string data = string.Empty;
                int continuation = 0;
                int firstDataChars = 0;
                index += 1;
                while (index < lines.Length && lines[index].Length > 0 && !lines[index].StartsWith(ValuePrefix, StringComparison.Ordinal) &&
                       !lines[index].StartsWith(KeyNameLabel, StringComparison.Ordinal))
                {
                    string field = lines[index];
                    if (field.StartsWith(NameLabel, StringComparison.Ordinal))
                    {
                        name = ValueOf(field, NameLabel);
                    }
                    else if (field.StartsWith(DataSizeLabel, StringComparison.Ordinal))
                    {
                        dataSize = ValueOf(field, DataSizeLabel);
                    }
                    else if (field.StartsWith(DataLabel, StringComparison.Ordinal))
                    {
                        data = ValueOf(field, DataLabel);
                        firstDataChars = data.Length;
                        index += 1;
                        while (index < lines.Length && lines[index].StartsWith(ContinuationIndent, StringComparison.Ordinal))
                        {
                            data += lines[index][LabelWidth..];
                            continuation += 1;
                            index += 1;
                        }

                        continue;
                    }

                    index += 1;
                }

                values.Add(new ValueBlock(
                    RelativeOf(keyPath),
                    name == NoName ? string.Empty : name,
                    dataSize,
                    data,
                    continuation,
                    firstDataChars));
                continue;
            }

            index += 1;
        }

        return new Analysis(keyTimes, values, keyCount, valueCount);
    }

    private static string ValueOf(string line, string label)
    {
        if (line.Length < LabelWidth || line[label.Length..LabelWidth].Any(c => c != ' '))
        {
            throw new InvalidDataException($"export-text: label '{label}' is not padded to {LabelWidth} characters: '{line}'.");
        }

        return line[LabelWidth..];
    }

    // Key Name lines are HKEY_CURRENT_USER\Software\BWC-Oracle[\sub]; the seed keys are relative to the oracle root.
    private static string RelativeOf(string keyPath)
    {
        string root = ClassicOracleKey;
        if (keyPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return keyPath.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase) ? keyPath[(root.Length + 1)..] : keyPath;
    }

    private static (string Format, bool Utc, string Culture, int Matches, int KeysChecked) MatchTimeFormat(IReadOnlyList<KeyTime> keyTimes)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (keyTimes.Count == 0)
        {
            throw new InvalidDataException("export-text: no Last Write Time line was found.");
        }

        var remaining = new List<(string Pattern, bool Utc)>();
        foreach (string pattern in TimeCandidates(culture))
        {
            remaining.Add((pattern, false));
            remaining.Add((pattern, true));
        }

        int candidates = remaining.Count;
        foreach (KeyTime keyTime in keyTimes)
        {
            string relative = RelativeOf(keyTime.KeyPath);
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(relative, writable: false)
                ?? throw new InvalidOperationException($"export-text: the key '{relative}' is not in the registry.");
            DateTime stamp = DateTime.FromFileTimeUtc(LastWriteFileTime(key));
            remaining = remaining
                .Where(c => (c.Utc ? stamp : stamp.ToLocalTime()).ToString(c.Pattern, culture) == keyTime.Observed)
                .ToList();
        }

        if (remaining.Count == 0)
        {
            string sample = keyTimes[0].Observed;
            throw new InvalidDataException(
                $"export-text: no time format of culture '{culture.Name}' matches the classic value '{sample}' ({candidates} candidates).");
        }

        (string chosenPattern, bool chosenUtc) = remaining[0];
        return (chosenPattern, chosenUtc, culture.Name, keyTimes.Count, keyTimes.Count);
    }

    private static IEnumerable<string> TimeCandidates(CultureInfo culture)
    {
        DateTimeFormatInfo f = culture.DateTimeFormat;
        foreach (string date in new[] { f.ShortDatePattern, f.LongDatePattern })
        {
            foreach (string time in new[] { f.ShortTimePattern, f.LongTimePattern })
            {
                foreach (string join in new[] { " ", " - ", ", " })
                {
                    yield return date + join + time;
                }
            }
        }

        yield return "G";
        yield return "g";
        yield return "F";
        yield return "f";
    }

    private static long LastWriteFileTime(RegistryKey key)
    {
        int rc = RegQueryInfoKeyW(
            key.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            out long lastWrite);
        if (rc != 0)
        {
            throw new InvalidOperationException($"RegQueryInfoKeyW failed with {rc}.");
        }

        return lastWrite;
    }

    private sealed record DataCheck(
        int Compared,
        int Complete,
        IReadOnlyList<string> Cut,
        int DataSizeCompared,
        int DataSizeEqualsBytes,
        string LongestName,
        int LongestBytes,
        bool LongestComplete,
        int BinaryWrapLines,
        int BinaryWrapFirstLineChars)
    {
        public JsonObject ToJson() => new()
        {
            ["valuesCompared"] = Compared,
            ["valuesWithCompleteData"] = Complete,
            ["cutValues"] = new JsonArray(Cut.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["dataSizeCompared"] = DataSizeCompared,
            ["dataSizeEqualsBytes"] = DataSizeEqualsBytes,
            ["longestValue"] = new JsonObject
            {
                ["name"] = LongestName,
                ["bytes"] = LongestBytes,
                ["complete"] = LongestComplete,
            },
            ["binaryWrap"] = new JsonObject
            {
                ["continuationLines"] = BinaryWrapLines,
                ["firstLineChars"] = BinaryWrapFirstLineChars,
            },
        };
    }

    // Compares every REG_SZ, REG_EXPAND_SZ and REG_BINARY seed value with its classic block. Whitespace is ignored,
    // so a wrapped line still counts as complete. A value whose text is shorter than the seed is listed as cut.
    private static DataCheck CheckData(Analysis analysis)
    {
        var seedByKey = new Dictionary<string, IReadOnlyList<SeedValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (SeedKey key in SeedSpec.Keys)
        {
            seedByKey[key.Relative] = key.Values;
        }

        int compared = 0;
        int complete = 0;
        var cut = new List<string>();
        int sizeCompared = 0;
        int sizeEquals = 0;
        string longestName = string.Empty;
        int longestBytes = 0;
        bool longestComplete = false;
        int wrapBytes = 0;
        int wrapLines = 0;
        int wrapFirst = 0;

        foreach (ValueBlock block in analysis.Values)
        {
            if (!seedByKey.TryGetValue(block.KeyRelative, out IReadOnlyList<SeedValue>? seedValues))
            {
                continue;
            }

            SeedValue? seed = seedValues.FirstOrDefault(v => string.Equals(v.Name, block.Name, StringComparison.Ordinal));
            if (seed is null)
            {
                continue;
            }

            if (block.DataSize.Length > 0 && int.TryParse(block.DataSize, CultureInfo.InvariantCulture, out int size))
            {
                sizeCompared += 1;
                if (size == seed.Data.Length)
                {
                    sizeEquals += 1;
                }
            }

            if (seed.Type is not (1 or 2 or 3) || seed.Data.Length == 0)
            {
                continue;
            }

            string expected = seed.Type == 3
                ? Util.Hex(seed.Data)
                : Encoding.Unicode.GetString(seed.Data).TrimEnd('\0');
            string actual = Squash(block.Data);
            bool isComplete = seed.Type == 3
                ? actual.Contains(expected, StringComparison.Ordinal)
                : actual.Contains(Squash(expected), StringComparison.Ordinal);
            compared += 1;
            if (isComplete)
            {
                complete += 1;
            }
            else
            {
                cut.Add((block.KeyRelative.Length == 0 ? "(root)" : block.KeyRelative) + @"\" + seed.Name);
            }

            if (seed.Data.Length > longestBytes)
            {
                longestBytes = seed.Data.Length;
                longestName = (block.KeyRelative.Length == 0 ? "(root)" : block.KeyRelative) + @"\" + seed.Name;
                longestComplete = isComplete;
            }

            if (seed.Type == 3 && seed.Data.Length > wrapBytes)
            {
                wrapBytes = seed.Data.Length;
                wrapLines = block.ContinuationLines;
                wrapFirst = block.FirstDataChars;
            }
        }

        return new DataCheck(compared, complete, cut, sizeCompared, sizeEquals, longestName, longestBytes, longestComplete, wrapLines, wrapFirst);
    }

    private static string Squash(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count += 1;
            at += needle.Length;
        }

        return count;
    }

    private static JsonObject Snapshot(RegeditWindow w, IntPtr dlg, string name)
    {
        IntPtr all = FindById(w, dlg, AllId);
        IntPtr branch = FindById(w, dlg, SelectedBranchId);
        IntPtr edit = FindById(w, dlg, BranchEditId);
        return new JsonObject
        {
            ["name"] = name,
            ["all"] = new JsonObject { ["present"] = all != IntPtr.Zero, ["checked"] = IsChecked(w, all) },
            ["selectedBranch"] = new JsonObject { ["present"] = branch != IntPtr.Zero, ["checked"] = IsChecked(w, branch) },
            ["branchEdit"] = new JsonObject
            {
                ["present"] = edit != IntPtr.Zero,
                ["enabled"] = edit != IntPtr.Zero && Win32Ext.IsWindowEnabled(edit),
            },
        };
    }

    private static List<string> ComboItems(RegeditWindow w, IntPtr combo)
    {
        int count = w.Send(combo, CbGetCount, IntPtr.Zero, IntPtr.Zero).ToInt32();
        var items = new List<string>();
        for (int i = 0; i < count; i++)
        {
            // CB_GETLBTEXT copies into this process's buffer through cross-process message marshalling.
            IntPtr local = Marshal.AllocHGlobal(1024);
            try
            {
                int length = w.Send(combo, CbGetLbText, new IntPtr(i), local).ToInt32();
                items.Add(length <= 0 ? string.Empty : Marshal.PtrToStringUni(local, Math.Min(length, 511)) ?? string.Empty);
            }
            finally
            {
                Marshal.FreeHGlobal(local);
            }
        }

        return items;
    }

    private static bool IsChecked(RegeditWindow w, IntPtr radio) =>
        radio != IntPtr.Zero && w.Send(radio, Wm.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32() == BstChecked;

    private static void Click(IntPtr control)
    {
        if (control != IntPtr.Zero)
        {
            Win32.PostMessageW(control, Wm.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // Reads an edit's text in this process's address space through WM_GETTEXT, which works across processes.
    private static string ReadEdit(RegeditWindow w, IntPtr edit)
    {
        if (edit == IntPtr.Zero)
        {
            return string.Empty;
        }

        int length = (int)w.Send(edit, WmGetTextLength, IntPtr.Zero, IntPtr.Zero).ToInt64();
        var sb = new StringBuilder(length + 1);
        _ = SendMessageBuffer(edit, WmGetText, new IntPtr(sb.Capacity), sb);
        return sb.ToString();
    }

    // The template's child controls are children of a child window of the common dialog, so the search goes down.
    private static IntPtr FindById(RegeditWindow w, IntPtr parent, int id)
    {
        foreach (IntPtr child in w.ChildrenOf(parent))
        {
            if (Win32.GetDlgCtrlID(child) == id)
            {
                return child;
            }

            IntPtr nested = FindById(w, child, id);
            if (nested != IntPtr.Zero)
            {
                return nested;
            }
        }

        return IntPtr.Zero;
    }

    // A dialog of the process that was not on screen before Save. The owner's own dialog is not a candidate.
    private static IntPtr NewDialog(RegeditWindow w, HashSet<IntPtr> existing, IntPtr owner)
    {
        foreach (IntPtr h in RegeditWindow.TopLevel(w.ProcessId, RegeditWindow.DialogClass))
        {
            if (h != owner && !existing.Contains(h))
            {
                return h;
            }
        }

        return IntPtr.Zero;
    }

    // Every static control of the box, at any depth, read with WM_GETTEXT.
    private static string StaticText(RegeditWindow w, IntPtr box)
    {
        var parts = new List<string>();
        CollectStatics(w, box, parts);
        return string.Join("\n", parts.Where(p => p.Length > 0));
    }

    private static void CollectStatics(RegeditWindow w, IntPtr parent, List<string> parts)
    {
        foreach (IntPtr child in w.ChildrenOf(parent))
        {
            if (string.Equals(RegeditWindow.ClassOf(child), "Static", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(ReadEdit(w, child));
            }

            CollectStatics(w, child, parts);
        }
    }

    // Every top-level window of the process, visible or not, with class, caption, visibility, enabled state and owner.
    private static Dictionary<IntPtr, string> WindowsOf(int pid)
    {
        var map = new Dictionary<IntPtr, string>();
        Win32.EnumWindows((h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out uint owner);
            if (owner == (uint)pid)
            {
                map[h] = $"class '{RegeditWindow.ClassOf(h)}' caption '{WindowText(h)}' visible {Win32.IsWindowVisible(h)} " +
                    $"enabled {Win32Ext.IsWindowEnabled(h)} owner {Win32Ext.GetWindow(h, Wm.GW_OWNER):X}";
            }

            return true;
        }, IntPtr.Zero);
        return map;
    }

    // One timestamped diagnostic line on the console. The tracked outputs never carry these lines.
    private static void Log(string message)
    {
        Console.WriteLine("export-text: " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message);
    }

    private static string WindowText(IntPtr h)
    {
        var sb = new StringBuilder(1024);
        _ = Win32.GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
