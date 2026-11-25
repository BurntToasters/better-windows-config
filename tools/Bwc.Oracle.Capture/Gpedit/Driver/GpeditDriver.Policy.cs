using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Opening a policy by its exact display name and running one edit session in its dialog.
// Failure modes 3, 4, 5, 7 and 13 of the driver document.
internal sealed partial class GpeditDriver
{
    // The policy row whose name equals the display name exactly (ordinal) in the visible list; more than one needs an occurrence.
    internal AutomationElement FindPolicyRow(string policy, int? occurrence)
    {
        DateTime started = DateTime.UtcNow;
        AutomationElement[] matches;
        while (true)
        {
            ctx.ThrowIfCancelled();
            ThrowIfDead();
            ListCandidate? list = VisibleList();
            matches = Array.Empty<AutomationElement>();
            if (list is not null)
            {
                matches = list.List.FindAll(
                    TreeScope.Children,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.NameProperty, policy))).Cast<AutomationElement>().ToArray();
                if (matches.Length > 0)
                {
                    break;
                }
            }

            if ((DateTime.UtcNow - started).TotalSeconds >= 20)
            {
                throw new DriverException($"no row named exactly '{policy}' in the visible list. Similar rows: " + SimilarRows(list, policy), evidence: new JsonObject { ["lists"] = DescribeLists() });
            }

            Thread.Sleep(500);
        }

        if (matches.Length > 1 && occurrence is null)
        {
            throw new DriverException($"{matches.Length} rows are named exactly '{policy}'; give 'occurrence' (zero based, list order)");
        }

        int index = occurrence ?? 0;
        if (index >= matches.Length)
        {
            throw new DriverException($"occurrence {index} of '{policy}' does not exist; there are {matches.Length} rows with that name");
        }

        return matches[index];
    }

    // Select the row, check that the focused element is that row, press Enter, wait for a new setting dialog, check its title.
    internal PolicyDialog OpenPolicy(string policy, int? occurrence)
    {
        JsonArray leftOver = Phases.Run("closeLeftoverDialogs", EnsureNoDialogs);
        if (leftOver.Count > 0)
        {
            Note("dialogs closed before opening the policy: " + leftOver.ToJsonString());
        }

        AutomationElement row = Phases.Run("findPolicyRow", () => FindPolicyRow(policy, occurrence));
        var known = Dialogs().Select(w => w.Hwnd).ToHashSet();
        string method = "SelectionItem";
        using (Phases.Begin("rowSelectAndFocus"))
        {
            ProbeInput.Foreground(main);
            if (!ProbeUia.Select(row))
            {
                ProbeUia.Click(row, doubleClick: false);
                method = "click";
            }

            ProbeUia.Focus(row);
            Thread.Sleep(300);
            string focused = ProbeUia.NameOf(AutomationElement.FocusedElement ?? row);
            if (!string.Equals(focused, policy, StringComparison.Ordinal))
            {
                ProbeUia.Focus(row);
                Thread.Sleep(300);
                focused = ProbeUia.NameOf(AutomationElement.FocusedElement ?? row);
            }

            Note($"focused element before Enter: '{focused}'");
            if (!string.Equals(focused, policy, StringComparison.Ordinal))
            {
                throw new DriverException($"the focused element is '{focused}', not '{policy}'. Enter was not sent.");
            }
        }

        using (Phases.Begin("sendEnter"))
        {
            DriverInput.RequireForeground(main, Pid);
            DriverInput.Key(ProbeInput.VkReturn);
        }

        method += "+Enter";
        WinInfo? dialog = Phases.Run("waitSettingDialog", () => WaitSettingDialog(known, TimeSpan.FromSeconds(10), policy));
        if (dialog is null)
        {
            using (Phases.Begin("doubleClickFallback"))
            {
                try
                {
                    ProbeUia.Click(row, doubleClick: true);
                    method += ", then double click";
                }
                catch (InvalidOperationException ex)
                {
                    Note("double click not made: " + ex.Message);
                }

                dialog = WaitSettingDialog(known, TimeSpan.FromSeconds(10), policy);
            }
        }

        if (dialog is null)
        {
            throw new DriverException($"no setting dialog opened for '{policy}' (tried {method})");
        }

        Phases.Run("settleAfterDialogOpen", () => Thread.Sleep(600));
        var result = new PolicyDialog(this, dialog.Hwnd, policy, method);
        Phases.Run("verifyTitle", result.VerifyTitle);
        return result;
    }

    private WinInfo? WaitSettingDialog(HashSet<IntPtr> known, TimeSpan limit, string policy)
    {
        DateTime end = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < end)
        {
            ctx.ThrowIfCancelled();
            ThrowIfDead();
            foreach (WinInfo window in NewDialogs(known))
            {
                DialogKind kind = Classify(window);
                if (kind == DialogKind.Setting)
                {
                    return window;
                }

                if (kind == DialogKind.MessageBox)
                {
                    JsonObject box = DismissMessageBox(window, null);
                    throw new DriverException($"opening '{policy}' showed a message box", evidence: box);
                }
            }

            Thread.Sleep(150);
        }

        return null;
    }

    private static string SimilarRows(ListCandidate? list, string policy)
    {
        if (list is null)
        {
            return "(no visible list)";
        }

        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        cache.Add(AutomationElement.NameProperty);
        using (cache.Activate())
        {
            IEnumerable<string> names = list.List.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().Select(r => r.Cached.Name);
            string[] similar = names.Where(n => n.Contains(policy, StringComparison.OrdinalIgnoreCase) || policy.Contains(n, StringComparison.OrdinalIgnoreCase)).Take(12).ToArray();
            return similar.Length == 0 ? "(none)" : string.Join(" | ", similar.Select(n => "'" + n + "'"));
        }
    }

    // One edit session: open, read, set state, comment and options, read again, press the buttons in order, and leave nothing open.
    internal JsonObject EditPolicy(GpeditOp op)
    {
        bool readLists = op.ReadLists ?? true;
        PolicyDialog dialog = Phases.Run("openPolicy", () => OpenPolicy(op.Policy!, op.Occurrence));
        var data = new JsonObject
        {
            ["policy"] = op.Policy,
            ["opened"] = new JsonObject { ["method"] = dialog.OpenMethod, ["titleMatch"] = dialog.TitleMatch, ["listHwnd"] = "0x" + lastListHwnd.ToString("X"), ["notes"] = TakeNotes() },
        };
        try
        {
            data["before"] = Phases.Run("readBefore", () => dialog.Read(readLists));
            bool anySet = op.State is not null || op.Comment is not null || op.Options is { Count: > 0 };
            var sets = new JsonArray();
            if (op.State is not null)
            {
                sets.Add(Phases.Run("setState", () => dialog.SetState(op.State)));
            }

            if (op.Comment is not null)
            {
                sets.Add(Phases.Run("setComment", () => dialog.SetComment(op.Comment)));
            }

            int optionNumber = 0;
            foreach (OptionSpec option in op.Options ?? new List<OptionSpec>())
            {
                sets.Add(Phases.Run("setOption " + optionNumber++, () => dialog.SetOption(option)));
            }

            data["sets"] = sets;
            if (anySet)
            {
                // After a set, Show Contents is not opened again: a Cancel there seems to lose the list in classic (section 39).
                data["afterSet"] = Phases.Run("readAfter", () => dialog.Read(readLists: false));
            }

            if (op.Screenshot is true)
            {
                data["screenshotDialog"] = Phases.Run("screenshotDialog", () => Shot(dialog.Hwnd, "dialog-" + SafeName(op.Policy!)));
            }

            var presses = new JsonArray();
            bool anyBlocked = false;
            foreach (string button in op.EffectivePress())
            {
                JsonObject press = Phases.Run("press " + button, () => dialog.Press(button, op.MessageBoxAnswer));
                presses.Add(press);
                if (press["blockedByMessageBox"]?.GetValue<bool>() == true)
                {
                    anyBlocked = true;
                    break;
                }

                if (button is "OK" or "Cancel")
                {
                    break;
                }
            }

            data["presses"] = presses;
            if (anyBlocked)
            {
                data["blockedByMessageBox"] = true;
            }

            if (dialog.IsOpen)
            {
                data["dialogStillOpen"] = Phases.Run("readThird", () => dialog.Read(readLists: false));
                JsonObject cancel = Phases.Run("press Cancel at end", () => dialog.Press("Cancel", null));
                data["cancelledAtEnd"] = cancel;
            }

            data["finalTitle"] = dialog.LastTitle;
            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            data["partial"] = true;
            throw new PartialResultException(data, ex);
        }
    }

    internal static string SafeName(string text) => string.Concat(text.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
}

// An operation that failed after producing some data: the runner records the data with the error.
internal sealed class PartialResultException : Exception
{
    internal PartialResultException(JsonObject data, Exception inner)
        : base(inner.Message, inner)
    {
        Partial = data;
    }

    internal JsonObject Partial { get; }
}
