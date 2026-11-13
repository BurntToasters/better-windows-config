using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Bwc.UI.Theme;

/// <summary>
/// The theme for the classic window. The palette follows Windows (light, dark, or high contrast)
/// unless the app sets an override. Every brush goes through <see cref="BwcBrushes"/>, so tree,
/// list, shell, address bar, and splitter switch together without a restart.
/// </summary>
/// <remarks>
/// The override is not persisted here. The app stores the value it chooses and calls
/// <see cref="SetOverride"/> at startup. Until then the override is System.
/// </remarks>
public static class BwcTheme
{
    private static BwcThemeOverride _override = BwcThemeOverride.System;
#if BWC_E2E
    /// <summary>The E2E-only launch override. It wins over the app override, so a persisted choice cannot replace it.</summary>
    private static BwcThemeOverride? _e2eOverride;
#endif
    private static BwcPalette _current;
    private static bool _initialized;
    private static DispatcherQueue? _queue;
    private static UISettings? _uiSettings;
    private static AccessibilitySettings? _accessibility;
    private static BwcPaneState _tree;
    private static BwcPaneState _list;

    /// <summary>Windows default accent, used when the settings object cannot be read.</summary>
    private static readonly Color DefaultAccent = Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);

    /// <summary>The palette in effect. Reading it initializes the theme on the calling UI thread.</summary>
    public static BwcPalette Current
    {
        get
        {
            EnsureInitialized();
            return _current;
        }
    }

    /// <summary>The app override. Persistence belongs to the app.</summary>
    public static BwcThemeOverride Override
    {
        get
        {
            EnsureInitialized();
            return _override;
        }
    }

    /// <summary>
    /// Raised after the palette changes: an override change, a system theme change, or a high
    /// contrast change.
    /// </summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Sets the app override and applies the palette at once. Call on the UI thread. Setting the
    /// value already in effect does nothing.
    /// </summary>
    public static void SetOverride(BwcThemeOverride value)
    {
        EnsureInitialized();
        if (_override == value)
        {
            return;
        }

        _override = value;
        ApplyPalette(raise: true);
    }

    /// <summary>
    /// Initializes the theme once on the UI thread: reads the system palette, subscribes to the
    /// system theme and high contrast changes, and applies the palette. Safe to call repeatedly.
    /// </summary>
    internal static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _queue = DispatcherQueue.GetForCurrentThread();

        // An unpackaged app can be refused a WinRT event subscription (COM error 0x80070490, element
        // not found). The settings objects are still kept, and the shell's ActualThemeChanged handler
        // calls RefreshSystem, so a refused subscription costs only the immediate refresh. The
        // subscription is attempted once, here, and never retried.
        try
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += (_, _) => OnSystemChanged();
        }
        catch (COMException)
        {
            // Keep the defaults. The palette falls back to the light system colors and default accent.
        }

        try
        {
            _accessibility = new AccessibilitySettings();
            _accessibility.HighContrastChanged += (_, _) => OnSystemChanged();
        }
        catch (COMException)
        {
            // Without the object, high contrast is reported as off until a refresh.
        }

#if BWC_E2E
        // E2E-only override. The variable is read at launch, never in a ship build.
        _e2eOverride = ParseE2EOverride(Environment.GetEnvironmentVariable("BWC_E2E_THEME"));
#endif

        ApplyPalette(raise: false);
    }

    /// <summary>Records which pane has focus and whether its focused row shows a focus rectangle.</summary>
    internal static void SetPaneState(BwcPane pane, bool hasFocus, bool showFocusRect)
    {
        EnsureInitialized();
        var state = new BwcPaneState(hasFocus, showFocusRect);
        if (pane == BwcPane.Tree)
        {
            if (_tree == state)
            {
                return;
            }

            _tree = state;
        }
        else
        {
            if (_list == state)
            {
                return;
            }

            _list = state;
        }

        BwcBrushes.ApplyPane(_current, pane, state);
    }

    /// <summary>
    /// Re-reads the system state. The shell calls this from its ActualThemeChanged handler, which is
    /// the fallback when the WinRT settings events were refused. A call with no change does nothing.
    /// </summary>
    internal static void RefreshSystem()
    {
        EnsureInitialized();
        OnSystemChanged();
    }

    private static void OnSystemChanged()
    {
        if (_queue is null || _queue.HasThreadAccess)
        {
            RefreshIfChanged();
            return;
        }

        _ = _queue.TryEnqueue(RefreshIfChanged);
    }

    private static void RefreshIfChanged()
    {
        if (Resolve(EffectiveOverride(), IsHighContrast()) == _current)
        {
            return;
        }

        ApplyPalette(raise: true);
    }

    /// <summary>The override that picks the palette: the E2E launch override when set, otherwise the app override.</summary>
    private static BwcThemeOverride EffectiveOverride()
    {
#if BWC_E2E
        if (_e2eOverride is { } forced)
        {
            return forced;
        }
#endif
        return _override;
    }

    private static void ApplyPalette(bool raise)
    {
        BwcPalette palette = Resolve(EffectiveOverride(), IsHighContrast());
        _current = palette;
        BwcBrushes.Apply(palette, _tree, _list);

        if (raise)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Chooses the palette for an override, or for the system when high contrast is on.</summary>
    private static BwcPalette Resolve(BwcThemeOverride value, bool highContrast)
    {
        if (highContrast)
        {
            return BwcSystemColors.SystemPalette(BwcThemeKind.HighContrast);
        }

        return value switch
        {
            BwcThemeOverride.Light => BwcSystemColors.SystemPalette(BwcThemeKind.Light),
            BwcThemeOverride.Dark => BwcSystemColors.DarkPalette(AccentColor()),
            _ => SystemAppsAreDark()
                ? BwcSystemColors.DarkPalette(AccentColor())
                : BwcSystemColors.SystemPalette(BwcThemeKind.Light),
        };
    }

    private static bool IsHighContrast()
    {
        try
        {
            return _accessibility?.HighContrast == true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool SystemAppsAreDark()
    {
        try
        {
            return _uiSettings is not null && BwcSystemColors.SystemAppsAreDark(_uiSettings);
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static Color AccentColor()
    {
        try
        {
            return _uiSettings is null ? DefaultAccent : BwcSystemColors.Accent(_uiSettings);
        }
        catch (COMException)
        {
            return DefaultAccent;
        }
    }

    /// <summary>
    /// The element theme for a palette. Shells set it on their root so Fluent controls inside them
    /// (menus, flyouts, text boxes) follow the palette. The application theme is not changed:
    /// setting it after the window exists faults inside Microsoft.UI.Xaml, and other windows keep
    /// following Windows. A high contrast palette follows the luminance of its window color.
    /// </summary>
    public static ElementTheme ElementThemeFor(BwcPalette palette)
    {
        bool dark = palette.Kind == BwcThemeKind.Dark
            || (palette.Kind == BwcThemeKind.HighContrast && BwcSystemColors.Luminance(palette.Window) < 0.5);
        return dark ? ElementTheme.Dark : ElementTheme.Light;
    }

#if BWC_E2E
    private static BwcThemeOverride? ParseE2EOverride(string? value) => value switch
    {
        "Light" => BwcThemeOverride.Light,
        "Dark" => BwcThemeOverride.Dark,
        _ => null,
    };
#endif
}
