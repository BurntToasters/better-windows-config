using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace Bwc.Oracle.Capture.Gpedit.Probe;

// UI Automation helpers for the probe: element description, tree dump with a bounded size, lookups, and the few actions it takes.
internal static class ProbeUia
{
    private const int MaxText = 400;

    private static readonly AutomationProperty[] CachedProperties =
    {
        AutomationElement.NameProperty,
        AutomationElement.AutomationIdProperty,
        AutomationElement.ClassNameProperty,
        AutomationElement.ControlTypeProperty,
        AutomationElement.FrameworkIdProperty,
        AutomationElement.BoundingRectangleProperty,
        AutomationElement.IsKeyboardFocusableProperty,
        AutomationElement.IsEnabledProperty,
        AutomationElement.IsOffscreenProperty,
        AutomationElement.NativeWindowHandleProperty,
        AutomationElement.LocalizedControlTypeProperty,
        AutomationElement.AcceleratorKeyProperty,
        AutomationElement.AccessKeyProperty,
        AutomationElement.HasKeyboardFocusProperty,
    };

    private sealed class Budget
    {
        internal int Remaining;
        internal bool Truncated;
    }

    internal static string Trim(string? text)
    {
        string value = text ?? string.Empty;
        return value.Length <= MaxText ? value : value[..MaxText] + "...";
    }

    private static CacheRequest NewCache()
    {
        var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        foreach (AutomationProperty property in CachedProperties)
        {
            request.Add(property);
        }

        return request;
    }

    internal static JsonArray? Rect(System.Windows.Rect rect)
    {
        if (rect.IsEmpty || double.IsInfinity(rect.X) || double.IsInfinity(rect.Y))
        {
            return null;
        }

        return new JsonArray((int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));
    }

    // One element: type, name, ids, class, framework, rectangle, patterns, focusability, and read-only pattern values.
    internal static JsonObject Describe(AutomationElement element) => DescribeCached(element.GetUpdatedCache(NewCache()));

    private static JsonObject DescribeCached(AutomationElement element)
    {
        AutomationElement.AutomationElementInformation cached = element.Cached;
        var patterns = new JsonArray();
        AutomationPattern[] supported = element.GetSupportedPatterns();
        foreach (AutomationPattern pattern in supported)
        {
            patterns.Add(pattern.ProgrammaticName.Replace("PatternIdentifiers.Pattern", string.Empty, StringComparison.Ordinal));
        }

        var node = new JsonObject
        {
            ["type"] = cached.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal),
            ["name"] = Trim(cached.Name),
            ["automationId"] = cached.AutomationId,
            ["className"] = cached.ClassName,
            ["framework"] = cached.FrameworkId,
            ["rect"] = Rect(cached.BoundingRectangle),
            ["patterns"] = patterns,
            ["focusable"] = cached.IsKeyboardFocusable,
            ["enabled"] = cached.IsEnabled,
            ["offscreen"] = cached.IsOffscreen,
            ["hasFocus"] = cached.HasKeyboardFocus,
            ["hwnd"] = cached.NativeWindowHandle == 0 ? null : JsonValue.Create("0x" + cached.NativeWindowHandle.ToString("X")),
            ["localizedType"] = cached.LocalizedControlType,
            ["accelerator"] = string.IsNullOrEmpty(cached.AcceleratorKey) ? null : JsonValue.Create(cached.AcceleratorKey),
            ["accessKey"] = string.IsNullOrEmpty(cached.AccessKey) ? null : JsonValue.Create(cached.AccessKey),
        };

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? value))
        {
            node["value"] = Trim(((ValuePattern)value).Current.Value);
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object? toggle))
        {
            node["toggle"] = ((TogglePattern)toggle).Current.ToggleState.ToString();
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? selection))
        {
            node["selected"] = ((SelectionItemPattern)selection).Current.IsSelected;
        }

        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? expand))
        {
            node["expandState"] = ((ExpandCollapsePattern)expand).Current.ExpandCollapseState.ToString();
        }

        return node;
    }

    // The element and its descendants as nested JSON, at most maxElements elements and maxDepth levels below the root.
    internal static JsonObject Tree(AutomationElement root, int maxDepth, int maxElements, bool rawView)
    {
        CacheRequest cache = NewCache();
        TreeWalker walker = rawView ? TreeWalker.RawViewWalker : TreeWalker.ControlViewWalker;
        var budget = new Budget { Remaining = maxElements };
        JsonObject node = Visit(root.GetUpdatedCache(cache), walker, cache, 0, maxDepth, budget);
        node["view"] = rawView ? "raw" : "control";
        node["truncated"] = budget.Truncated;
        return node;
    }

    private static JsonObject Visit(AutomationElement element, TreeWalker walker, CacheRequest cache, int depth, int maxDepth, Budget budget)
    {
        budget.Remaining--;
        JsonObject node;
        try
        {
            node = DescribeCached(element);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            return new JsonObject { ["error"] = ex.Message };
        }

        AutomationElement? child = walker.GetFirstChild(element, cache);
        if (child is not null && depth >= maxDepth)
        {
            budget.Truncated = true;
            node["childrenCutAtDepth"] = true;
            return node;
        }

        var children = new JsonArray();
        while (child is not null)
        {
            if (budget.Remaining <= 0)
            {
                budget.Truncated = true;
                break;
            }

            children.Add(Visit(child, walker, cache, depth + 1, maxDepth, budget));
            child = walker.GetNextSibling(child, cache);
        }

        if (children.Count > 0)
        {
            node["children"] = children;
        }

        return node;
    }

    internal static string NameOf(AutomationElement element)
    {
        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    internal static List<AutomationElement> Find(AutomationElement root, TreeScope scope, Condition condition) =>
        root.FindAll(scope, condition).Cast<AutomationElement>().ToList();

    internal static List<AutomationElement> OfType(AutomationElement root, ControlType type, TreeScope scope = TreeScope.Descendants) =>
        Find(root, scope, new PropertyCondition(AutomationElement.ControlTypeProperty, type));

    internal static List<AutomationElement> OfClass(AutomationElement root, string className, TreeScope scope = TreeScope.Descendants) =>
        Find(root, scope, new PropertyCondition(AutomationElement.ClassNameProperty, className));

    // An element of the given type whose name equals name, or starts with it when prefix is true (compared ignoring case).
    internal static AutomationElement? ByName(AutomationElement root, ControlType type, string name, bool prefix, TreeScope scope = TreeScope.Descendants)
    {
        foreach (AutomationElement candidate in OfType(root, type, scope))
        {
            string actual = Clean(NameOf(candidate));
            if (prefix ? actual.StartsWith(name, StringComparison.OrdinalIgnoreCase) : string.Equals(actual, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    // Names can carry the access key marker.
    internal static string Clean(string name) => name.Replace("&", string.Empty, StringComparison.Ordinal).Trim();

    internal static bool Select(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? selection))
        {
            ((SelectionItemPattern)selection).Select();
            return true;
        }

        return false;
    }

    // Pattern calls into a modal loop (a menu, a message box) can block until it ends, so they run on a worker thread. After
    // timeoutMs the call is treated as started and the caller goes on to look at the window it opened.
    internal static bool Invoke(AutomationElement element, int timeoutMs = 4000)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object? invoke))
        {
            RunBounded(() => ((InvokePattern)invoke).Invoke(), timeoutMs);
            return true;
        }

        return false;
    }

    internal static bool Expand(AutomationElement element, int timeoutMs = 4000)
    {
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? expand))
        {
            var pattern = (ExpandCollapsePattern)expand;
            if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
            {
                RunBounded(() => pattern.Expand(), timeoutMs);
            }

            return true;
        }

        return false;
    }

    private static void RunBounded(Action action, int timeoutMs)
    {
        Task task = Task.Run(action);
        if (task.Wait(timeoutMs) is false)
        {
            return;
        }

        if (task.IsFaulted)
        {
            throw task.Exception!.InnerException ?? task.Exception;
        }
    }

    internal static bool Collapse(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? expand))
        {
            ((ExpandCollapsePattern)expand).Collapse();
            return true;
        }

        return false;
    }

    internal static void Click(AutomationElement element, bool doubleClick)
    {
        System.Windows.Rect rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty || double.IsInfinity(rect.X))
        {
            throw new InvalidOperationException($"'{NameOf(element)}' has no bounding rectangle to click.");
        }

        ProbeInput.Click((int)(rect.X + (rect.Width / 2)), (int)(rect.Y + (rect.Height / 2)), doubleClick);
    }

    internal static void Focus(AutomationElement element)
    {
        try
        {
            element.SetFocus();
        }
        catch (InvalidOperationException)
        {
            // Not focusable: the caller falls back to the window.
        }
    }

    // Poll until the lookup returns something, up to timeout.
    internal static T? Wait<T>(Func<T?> lookup, TimeSpan timeout)
        where T : class
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (true)
        {
            T? found = lookup();
            if (found is not null || DateTime.UtcNow >= end)
            {
                return found;
            }

            Thread.Sleep(300);
        }
    }

    internal static string NamesOf(IEnumerable<AutomationElement> elements)
    {
        var sb = new StringBuilder();
        foreach (AutomationElement e in elements)
        {
            sb.Append('[').Append(NameOf(e)).Append(']');
        }

        return sb.ToString();
    }
}
