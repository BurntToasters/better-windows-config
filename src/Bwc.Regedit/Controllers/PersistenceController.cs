using Bwc.Regedit.Registry;
using Bwc.UI.List;

namespace Bwc.Regedit.Controllers;

/// <summary>Applies saved view state once at startup and captures it again for saving.</summary>
internal sealed class PersistenceController
{
    private readonly RegeditContext _context;
    private readonly FindController _find;
    private readonly ViewCommands _view;
    private bool _settingsApplied;

    public PersistenceController(RegeditContext context, FindController find, ViewCommands view)
    {
        _context = context;
        _find = find;
        _view = view;
    }

    /// <summary>Applies persisted view state once. Later calls are ignored.</summary>
    public void ApplySettings(RegeditSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_settingsApplied)
        {
            return;
        }

        _settingsApplied = true;
        RegeditSettings state = _context.Settings;
        state.LastKey = string.IsNullOrWhiteSpace(settings.LastKey) ? "Computer" : settings.LastKey;
        state.AddressBarVisible = settings.AddressBarVisible;
        state.ListColumnWidths = settings.ListColumnWidths is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : new Dictionary<string, double>(settings.ListColumnWidths, StringComparer.Ordinal);
        state.FindText = settings.FindText ?? string.Empty;
        state.FindFlags = settings.FindFlags;
        state.FindQueryText = settings.FindQueryText ?? string.Empty;
        state.FindQueryFlags = settings.FindQueryFlags;
        state.FindLastHitKey = settings.FindLastHitKey;
        state.FindLastHitValue = settings.FindLastHitValue;
        state.FindLastHitKind = settings.FindLastHitKind;
        state.FindLastHitText = settings.FindLastHitText;
        _find.RestoreFindState();

        if (_context.Shell.IsAddressBarVisible != state.AddressBarVisible)
        {
            _context.Shell.ToggleAddressBar();
        }

        _view.ApplyColumnWidths(state.ListColumnWidths);

        if (!string.Equals(state.LastKey, "Computer", StringComparison.OrdinalIgnoreCase))
        {
            _ = _view.RestoreLastKeyAsync(state.LastKey);
        }
    }

    /// <summary>Copies the current key, address bar state, and column widths for saving.</summary>
    public RegeditSettings CaptureSettings()
    {
        RegeditSettings state = _context.Settings;
        var snapshot = new RegeditSettings
        {
            LastKey = state.LastKey,
            AddressBarVisible = _context.Shell.IsAddressBarVisible,
            ListColumnWidths = new Dictionary<string, double>(StringComparer.Ordinal),
            FindText = state.FindText ?? string.Empty,
            FindFlags = state.FindFlags,
            FindQueryText = state.FindQueryText ?? string.Empty,
            FindQueryFlags = state.FindQueryFlags,
            FindLastHitKey = state.FindLastHitKey,
            FindLastHitValue = state.FindLastHitValue,
            FindLastHitKind = state.FindLastHitKind,
            FindLastHitText = state.FindLastHitText,
        };

        foreach (DetailsColumn column in _context.Shell.ListContent.Columns)
        {
            snapshot.ListColumnWidths[column.Key] = column.Width;
        }

        return snapshot;
    }
}
