using Bwc.Core;

namespace Bwc.Gpedit;

/// <summary>
/// The single modal slot owned by the editor window (docs/failure-modes/gpedit-commands.md, section 5).
/// A command reserves it before asynchronous setup and releases it after its dialog returns. A Refresh held
/// behind the reservation starts one fresh load after the last dialog closes.
/// </summary>
public sealed partial class GpeditRootView
{
    private bool _modalBusy;

    /// <summary>Whether an entrypoint may reserve the modal owner.</summary>
    private bool ModalCommandsAllowed => !_closed && !_modalBusy && (!IsEditorHost || _model is not null);

    /// <summary>Reserves the owner for one policy, filter, template, frame or Help dialog.</summary>
    private bool TryReserveModal()
    {
        if (!ModalCommandsAllowed)
        {
            return false;
        }

        _modalBusy = true;
        return true;
    }

    /// <summary>Releases one dialog's reservation and starts any reload queued behind it.</summary>
    private void ReleaseModal()
    {
        if (!_modalBusy)
        {
            return;
        }

        _modalBusy = false;
        if (_closed)
        {
            _reloadPending = false;
            return;
        }

        if (_reloadPending && !_loadInFlight)
        {
            _reloadPending = false;
            try
            {
                _ = BeginLoad(initial: false);
                UpdateReadout();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _reloadPending = true;
                Log.Warn("The queued Local Group Policy Editor reload could not start: " + ex.Message);
            }
        }
    }
}
