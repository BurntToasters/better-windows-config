using System.Windows.Automation;
using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture;

// Reads a window of another process through UI Automation (read-only patterns). Used for the Windows print host, whose
// controls are not Win32 windows. Nothing is invoked, selected or set here. Walk limits: depth 12 and 800 elements.
internal static class UiaProbe
{
    private const int MaxDepth = 12;
    private const int MaxElements = 800;

    internal sealed record Element(int Depth, string ControlType, string Name, string AutomationId, bool Enabled, bool? Toggled, bool? Selected, string? Value);

    internal sealed record Tree(List<Element> Elements, bool Truncated);

    // Walks the control view under hwnd. A host that cannot be read yields an empty tree and Truncated false.
    internal static Tree Walk(IntPtr hwnd)
    {
        var list = new List<Element>();
        bool truncated = false;
        AutomationElement root = AutomationElement.FromHandle(hwnd);
        Visit(root, 0, list, ref truncated);
        return new Tree(list, truncated);
    }

    private static void Visit(AutomationElement element, int depth, List<Element> list, ref bool truncated)
    {
        if (list.Count >= MaxElements || depth > MaxDepth)
        {
            truncated = true;
            return;
        }

        AutomationElement.AutomationElementInformation info = element.Current;
        bool? toggled = null;
        bool? selected = null;
        string? value = null;
        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object togglePattern))
        {
            toggled = ((TogglePattern)togglePattern).Current.ToggleState == ToggleState.On;
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selectionPattern))
        {
            selected = ((SelectionItemPattern)selectionPattern).Current.IsSelected;
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern))
        {
            value = ((ValuePattern)valuePattern).Current.Value;
        }

        list.Add(new Element(depth, info.ControlType.ProgrammaticName, info.Name, info.AutomationId, info.IsEnabled, toggled, selected, value));

        Condition all = Condition.TrueCondition;
        foreach (AutomationElement child in element.FindAll(TreeScope.Children, all).Cast<AutomationElement>())
        {
            Visit(child, depth + 1, list, ref truncated);
        }
    }

    // Tracked summary: counts and booleans only. The names and values stay in the local text.
    internal static JsonObject Summary(Tree tree)
    {
        var types = new JsonObject();
        foreach (IGrouping<string, Element> group in tree.Elements.GroupBy(e => e.ControlType).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            types[group.Key.Replace("ControlType.", string.Empty)] = group.Count();
        }

        List<Element> radios = tree.Elements.Where(e => e.ControlType == "ControlType.RadioButton").ToList();
        int selectedIndex = radios.FindIndex(r => r.Selected == true);
        bool hasAll = radios.Any(r => string.Equals(r.Name.Trim(), "All", StringComparison.OrdinalIgnoreCase));
        bool hasBranch = tree.Elements.Any(e => e.Name.Contains("branch", StringComparison.OrdinalIgnoreCase));
        bool hasRange = tree.Elements.Any(e => e.Name.Contains("range", StringComparison.OrdinalIgnoreCase));
        bool hasPagesControl = tree.Elements.Any(e => e.ControlType == "ControlType.ComboBox" &&
            e.Name.Contains("page", StringComparison.OrdinalIgnoreCase));
        List<Element> printers = tree.Elements.Where(e => e.ControlType == "ControlType.ComboBox" &&
            (e.Name.Contains("printer", StringComparison.OrdinalIgnoreCase) || e.AutomationId.Contains("Printer", StringComparison.OrdinalIgnoreCase))).ToList();
        string? printerValue = printers.FirstOrDefault()?.Value;

        return new JsonObject
        {
            ["elementCount"] = tree.Elements.Count,
            ["truncated"] = tree.Truncated,
            ["controlTypeCounts"] = types,
            ["radioButtonCount"] = radios.Count,
            ["selectedRadioIndex"] = selectedIndex < 0 ? null : JsonValue.Create(selectedIndex),
            ["hasAllRadio"] = hasAll,
            ["hasBranchText"] = hasBranch,
            ["hasRangeText"] = hasRange,
            ["hasPagesControl"] = hasPagesControl,
            ["printerSelectorFound"] = printers.Count > 0,
            ["printerIsPdf"] = printerValue is not null && printerValue == PdfPrinterName,
        };
    }

    internal const string PdfPrinterName = "Microsoft Print to PDF";

    // Expands the named element when it is a collapsed ExpandCollapse item. Returns false when it is not found. The only
    // expand the print capture makes (docs/failure-modes/oracle.md, section 22).
    // Only a Hyperlink with exactly this name is affected. It is expanded when it has ExpandCollapse, else invoked (Invoke).
    internal static bool ExpandNamed(IntPtr hwnd, string name)
    {
        AutomationElement root = AutomationElement.FromHandle(hwnd);
        Condition byName = new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink));
        AutomationElement? target = root.FindFirst(TreeScope.Descendants, byName);
        if (target is null)
        {
            return false;
        }

        if (target.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object expandPattern))
        {
            var expand = (ExpandCollapsePattern)expandPattern;
            if (expand.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
            {
                expand.Expand();
            }

            return true;
        }

        if (target.TryGetCurrentPattern(InvokePattern.Pattern, out object invokePattern))
        {
            ((InvokePattern)invokePattern).Invoke();
            return true;
        }

        return false;
    }

    // Local text: every element's type, name, id and state. Microsoft strings stay out of the tracked file.
    internal static JsonArray Local(Tree tree)
    {
        var array = new JsonArray();
        foreach (Element e in tree.Elements)
        {
            array.Add(new JsonObject
            {
                ["depth"] = e.Depth,
                ["type"] = e.ControlType,
                ["name"] = e.Name,
                ["automationId"] = e.AutomationId,
                ["enabled"] = e.Enabled,
                ["toggled"] = e.Toggled is bool t ? JsonValue.Create(t) : null,
                ["selected"] = e.Selected is bool s ? JsonValue.Create(s) : null,
                ["value"] = e.Value,
            });
        }

        return array;
    }
}
