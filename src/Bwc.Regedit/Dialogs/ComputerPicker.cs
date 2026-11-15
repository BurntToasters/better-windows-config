using System.Runtime.InteropServices;
using Bwc.Core;

namespace Bwc.Regedit.Dialogs;

/// <summary>
/// The File > Connect Network Registry picker: the system object picker (IDsObjectPicker), computers only, single
/// select, owned by the main window. Section 17.1 of docs/failure-modes/remote-registry.md is the spec. Every failure
/// returns Unavailable and is logged, so the caller falls back to the text prompt without an error box.
/// </summary>
internal static partial class ComputerPicker
{
    private static readonly Guid ObjectPickerClsid = new("17d6ccd8-3b7b-11d2-b9e0-00c04fd8dbf7");

    // Values from objsel.h.
    private const uint ScopeTypeWorkgroup = 0x00000080;
    private const uint ScopeTypeUplevelJoinedDomain = 0x00000002;
    private const uint ScopeTypeDownlevelJoinedDomain = 0x00000004;
    private const uint ScopeTypeEnterpriseDomain = 0x00000008;
    private const uint ScopeTypeGlobalCatalog = 0x00000010;
    private const uint ScopeTypeExternalUplevelDomain = 0x00000020;
    private const uint ScopeTypeExternalDownlevelDomain = 0x00000040;
    private const uint ScopeTypeUserEnteredUplevelScope = 0x00000100;
    private const uint ScopeTypeUserEnteredDownlevelScope = 0x00000200;
    private const uint ScopeFlagStartingScope = 0x00000001;
    private const uint FilterComputersUplevel = 0x00000800;
    private const uint FilterComputersDownlevel = 0x80000008;
    private const int SFalse = 1;
    private const int ScopeInitInfoSize = 56;
    private const int InitInfoSize = 48;
    private const string SelectionListFormat = "CFSTR_DSOP_DS_SELECTION_LIST";

    internal enum Outcome
    {
        /// <summary>A computer was chosen. The name is returned.</summary>
        Selected,

        /// <summary>The user cancelled, or the picker returned no selection.</summary>
        Cancelled,

        /// <summary>The picker could not be created, initialized, or shown. The text prompt takes over.</summary>
        Unavailable,
    }

    /// <summary>Shows the picker modally, owned by <paramref name="ownerHwnd"/>. Runs on the UI thread.</summary>
    internal static Outcome Show(nint ownerHwnd, out string? computerName)
    {
        computerName = null;
        IDsObjectPicker picker;
        try
        {
            Type? type = Type.GetTypeFromCLSID(ObjectPickerClsid);
            if (type is null)
            {
                Log.Warn("Registry Editor could not find the computer picker class.");
                return Outcome.Unavailable;
            }

            picker = (IDsObjectPicker)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not create the computer picker: " + ex.Message);
            return Outcome.Unavailable;
        }

        nint scopes = nint.Zero;
        nint init = nint.Zero;
        try
        {
            int scopeSize = Marshal.SizeOf<DsopScopeInitInfo>();
            int initSize = Marshal.SizeOf<DsopInitInfo>();
#if BWC_E2E
            if (scopeSize != ScopeInitInfoSize || initSize != InitInfoSize)
            {
                throw new InvalidOperationException(
                    $"Picker struct sizes are {scopeSize} and {initSize}, expected {ScopeInitInfoSize} and {InitInfoSize}.");
            }
#endif

            DsopScopeInitInfo[] specs = ScopeSpecs();
            scopes = Marshal.AllocHGlobal(scopeSize * specs.Length);
            for (int index = 0; index < specs.Length; index++)
            {
                Marshal.StructureToPtr(specs[index], nint.Add(scopes, index * scopeSize), fDeleteOld: false);
            }

            var initInfo = new DsopInitInfo
            {
                CbSize = (uint)initSize,
                TargetComputer = nint.Zero,
                ScopeCount = (uint)specs.Length,
                ScopeInfos = scopes,
                Options = 0,
                AttributeCount = 0,
                AttributeNames = nint.Zero,
            };
            init = Marshal.AllocHGlobal(initSize);
            Marshal.StructureToPtr(initInfo, init, fDeleteOld: false);

            int hr = picker.Initialize(init);
            if (hr < 0)
            {
                Log.Warn($"Registry Editor could not initialize the computer picker: HRESULT 0x{hr:X8}.");
                return Outcome.Unavailable;
            }

            return Invoke(picker, ownerHwnd, out computerName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ArgumentException)
        {
            Log.Warn("Registry Editor could not show the computer picker: " + ex.Message);
            return Outcome.Unavailable;
        }
        finally
        {
            // Initialize copies the scope list, but the memory is kept until the dialog ends.
            if (init != nint.Zero)
            {
                Marshal.FreeHGlobal(init);
            }

            if (scopes != nint.Zero)
            {
                Marshal.FreeHGlobal(scopes);
            }

            _ = Marshal.ReleaseComObject(picker);
        }
    }

    private static Outcome Invoke(IDsObjectPicker picker, nint ownerHwnd, out string? computerName)
    {
        computerName = null;
        int hr = picker.InvokeDialog(ownerHwnd, out System.Runtime.InteropServices.ComTypes.IDataObject? selections);
        if (hr < 0)
        {
            Log.Warn($"Registry Editor could not show the computer picker: HRESULT 0x{hr:X8}.");
            return Outcome.Unavailable;
        }

        if (hr == SFalse || selections is null)
        {
            return Outcome.Cancelled;
        }

        try
        {
            computerName = ReadFirstName(selections);
            return computerName is null ? Outcome.Cancelled : Outcome.Selected;
        }
        catch (COMException ex)
        {
            Log.Warn("Registry Editor could not read the computer the picker returned: " + ex.Message);
            return Outcome.Unavailable;
        }
        finally
        {
            _ = Marshal.ReleaseComObject(selections);
        }
    }

    /// <summary>
    /// Reads pwzName of the first DS_SELECTION in the CFSTR_DSOP_DS_SELECTION_LIST medium. The list starts with cItems
    /// and cFetchedCount (8 bytes), then the selections. The first pointer of a selection is the name.
    /// </summary>
    private static string? ReadFirstName(System.Runtime.InteropServices.ComTypes.IDataObject selections)
    {
        var format = new System.Runtime.InteropServices.ComTypes.FORMATETC
        {
            cfFormat = unchecked((short)RegisterClipboardFormat(SelectionListFormat)),
            dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
        };

        selections.GetData(ref format, out System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);
        try
        {
            nint hGlobal = medium.unionmember;
            nint block = GlobalLock(hGlobal);
            if (block == nint.Zero)
            {
                return null;
            }

            try
            {
                if (Marshal.ReadInt32(block) == 0)
                {
                    return null;
                }

                nint namePointer = Marshal.ReadIntPtr(block, 8);
                return namePointer == nint.Zero ? null : Marshal.PtrToStringUni(namePointer);
            }
            finally
            {
                _ = GlobalUnlock(hGlobal);
            }
        }
        finally
        {
            ReleaseMedium(ref medium);
        }
    }

    /// <summary>A medium with no owner is the caller's to free. Otherwise its owner releases it.</summary>
    private static void ReleaseMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium)
    {
        if (medium.pUnkForRelease is not null)
        {
            if (Marshal.IsComObject(medium.pUnkForRelease))
            {
                _ = Marshal.ReleaseComObject(medium.pUnkForRelease);
            }

            return;
        }

        if (medium.unionmember != nint.Zero)
        {
            _ = GlobalFree(medium.unionmember);
        }
    }

    /// <summary>
    /// Two scopes. The first is the downlevel scope (workgroup, downlevel domains, user-entered downlevel scope). The
    /// second is the uplevel scope (uplevel and enterprise domains, global catalog). Each lists computers only.
    /// </summary>
    private static DsopScopeInitInfo[] ScopeSpecs() =>
    [
        Scope(
            ScopeTypeWorkgroup | ScopeTypeDownlevelJoinedDomain | ScopeTypeUserEnteredDownlevelScope |
                ScopeTypeExternalDownlevelDomain,
            ScopeFlagStartingScope,
            uplevelBoth: 0,
            downlevel: FilterComputersDownlevel),
        Scope(
            ScopeTypeUplevelJoinedDomain | ScopeTypeEnterpriseDomain | ScopeTypeGlobalCatalog |
                ScopeTypeExternalUplevelDomain | ScopeTypeUserEnteredUplevelScope,
            0,
            uplevelBoth: FilterComputersUplevel,
            downlevel: FilterComputersDownlevel),
    ];

    private static DsopScopeInitInfo Scope(uint type, uint flags, uint uplevelBoth, uint downlevel) => new()
    {
        CbSize = (uint)ScopeInitInfoSize,
        ScopeType = type,
        ScopeFlags = flags,
        Filter = new DsopFilterFlags
        {
            UplevelBothModes = uplevelBoth,
            UplevelMixedModeOnly = 0,
            UplevelNativeModeOnly = 0,
            Downlevel = downlevel,
        },
        DcName = nint.Zero,
        AdsPath = nint.Zero,
        Hr = 0,
    };

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormat(string format);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint hMem);

    /// <summary>IDsObjectPicker. Its two methods follow IUnknown. HRESULTs are returned, not thrown.</summary>
    [ComImport]
    [Guid("0c87e64e-3b7a-11d2-b9e0-00c04fd8dbf7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDsObjectPicker
    {
        [PreserveSig]
        int Initialize(nint initInfo);

        [PreserveSig]
        int InvokeDialog(
            nint hwndParent,
            [MarshalAs(UnmanagedType.Interface)] out System.Runtime.InteropServices.ComTypes.IDataObject? selections);
    }

    /// <summary>DSOP_FILTER_FLAGS: the uplevel flags come first, then the downlevel flags (objsel.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DsopFilterFlags
    {
        public uint UplevelBothModes;
        public uint UplevelMixedModeOnly;
        public uint UplevelNativeModeOnly;
        public uint Downlevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DsopScopeInitInfo
    {
        public uint CbSize;
        public uint ScopeType;
        public uint ScopeFlags;
        public DsopFilterFlags Filter;
        public nint DcName;
        public nint AdsPath;
        public int Hr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DsopInitInfo
    {
        public uint CbSize;
        public nint TargetComputer;
        public uint ScopeCount;
        public nint ScopeInfos;
        public uint Options;
        public uint AttributeCount;
        public nint AttributeNames;
    }
}
