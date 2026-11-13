# Icon and row failure modes

Scope: `IconProvider` and its helpers in `src/Bwc.UI/Icons/`, the expander and icon placement in
`TreeRowElement` (`src/Bwc.UI/Tree/`), the value icon in `DetailsRowElement`
(`src/Bwc.UI/List/`), and the subkey count used for the expander in
`RegistryTreeSource` (`src/Bwc.Regedit/Registry/`). Classic reference: `regedit.exe` on this
machine, measured in `docs/parity/oracle/regedit-row-metrics.json`.

The icons are loaded at runtime. Nothing from Microsoft is copied into the repository, and no icon
bytes are committed. The sources are the stock shell icons (`SHGetStockIconInfo`) and the icon
resources of `C:\Windows\regedit.exe`, read as a data file.

## Icon loading

### regedit.exe is missing or unreadable

`LoadLibraryEx` on `C:\Windows\regedit.exe` can fail (missing file, policy block, a
Windows image that was moved). The provider then uses a BWC fallback glyph for the string and binary
value icons and the key icons. The tree and list keep their layout, so the density never depends on
the load succeeding. The failure is logged once per process.

### Resource id is different on another build

The group icon ids (`RT_GROUP_ICON` 100, 101, 102, 201, 203, 205, 206 in
`docs/parity/oracle/regedit-resources.md`) are the ids of this build (10.0.26100.8457). A different
regedit build can renumber them. The provider does not trust a fixed id blindly: it looks up the
id, checks that the group exists, and falls back to the fallback glyph when it does not. The E2E test
compares the rendered pixels with the classic capture, so a wrong icon fails the check instead of
shipping silently.

### Icon size does not exist in the group

A group can hold only some sizes (for example 16 and 32 but not 24). `LoadImage` with the exact
requested size picks the closest image and scales it. The provider requests `16 * dpiScale` (so 32 at
200 percent, 24 at 150 percent) and never uses a size larger than the group offers without scaling.
Scaling blurs the icon, so the E2E pixel check uses a tolerance and not an exact match.

### DPI changes while the window is open

The rasterization scale of an element can change (monitor move, Settings change). The cache is keyed
by pixel size, not by a global "current DPI", so a 100 percent icon and a 200 percent icon can exist
at once. A row reads the scale from its own `XamlRoot` each time it is attached. A stale icon is never
used for a different size, and an unused size is kept only until the cache is cleared at shutdown.

### Handle leaks

`GetIconInfo` returns two GDI bitmaps that the caller must delete. `SHGetStockIconInfo` and
`LoadImage` return icons that the caller must destroy. The conversion to pixels uses a screen DC
that must be released. Each path releases its handles in a `finally`, including the failure path
after a partial load. The regedit module is opened and freed inside each icon load, so no module
handle outlives a cache miss. The E2E test does not measure the GDI object count; a leak is not
covered by an automated check yet.

### ImageSource created off the UI thread

`WriteableBitmap` and `ImageSource` are UI objects. Icon decoding can run on the thread pool, but the
`ImageSource` is created on the UI thread (the row attach path), so a background decode never touches
XAML. The decoded pixel buffer is an immutable `byte[]` that the UI thread copies into the bitmap.

### Premultiplied alpha

Icons with an alpha channel store straight alpha, while XAML writes premultiplied BGRA. The
conversion premultiplies every pixel. Legacy icons with no alpha byte take their alpha from the AND
mask. Without this, edges show a dark halo on the selection color.

### Alpha byte is zero everywhere

Some icons carry a 32 bpp color bitmap whose alpha byte is always zero, meaning the mask defines the
shape. The conversion detects a color bitmap with no non-zero alpha and reads the mask instead, so the
icon is not drawn fully transparent.

## Tree rows

### Expander on a key with no subkeys

Classic regedit shows the expander only when the key has subkeys. `RegistryTreeSource` reads the
child count with `RegistryReader.GetKeyInfo` for each enumerated child (`RegQueryInfoKey`) and sets
`HasChildren` to `SubKeyCount > 0`. A key with no subkeys keeps no expander and cannot be expanded
from the keyboard, the chevron, or UIA.

### Expander on a key that cannot be opened

`GetKeyInfo` can throw `RegistryOperationException` for an access-denied key (for example under
`HKLM\SAM`). Such a key cannot be enumerated either. Classic regedit keeps the expander on those keys
and expanding them yields nothing, so the BWC node keeps `HasChildren = true` on that exception. The
expander stays and the expansion is empty, which matches the classic behavior checked on this machine.

### Expander removed after the node is shown

A key can gain its first subkey (or lose its last one) after the parent was enumerated. The parent
reads its children once, so a child's expander can be stale until the parent is re-enumerated. This is
accepted: classic regedit has the same staleness, and the next enumeration (F5, or re-selecting the
parent) corrects it. A stale expander never crashes a row, because expansion of an empty child just
loads nothing.

### Root nodes

The five predefined roots and the virtual Computer node are created without a registry read, so they
keep `hasChildren: true`. A predefined root always has subkeys in practice, and reading it on the UI
path would delay startup.

### Computer icon versus folder icon

The root uses the computer icon. Every key uses the closed folder icon. Classic regedit showed the
closed folder for the selected key on this build, and no open-folder group exists in regedit.exe, so
the icon does not change with expansion or selection.

### Text offset

The row offsets are measured from classic regedit and recorded in
`docs/parity/oracle/regedit-row-metrics.json`: the expander is centered at 16 DIP per level plus
10.5, the icon starts at 16 DIP per level plus 18, and the first text ink is at 16 DIP per level plus
37.5. The layout origin is 37 DIP because the glyph bearing adds about half a DIP. These are fixed
DIP values; they are not scaled by font size, which matches how `DenseMetrics` treats the indent.

## List rows

### Icon kind for an unknown type

The string icon is used for `REG_SZ`, `REG_EXPAND_SZ`, and `REG_MULTI_SZ`. The binary icon is used
for every other type. An unknown type string falls back to the binary icon, which matches classic for
types it does not name.

### Icon and name cell overlap

The name cell starts after the icon. A long name is trimmed inside the cell, so it never draws over
the icon or the next column. The list row keeps its existing column widths.

## Test-only failure modes

### Classic captures missing

The E2E test writes the BWC capture and a metrics summary to `artifacts/e2e/visual/icons/`. It then
checks that the two classic captures (`classic-initial-window.png` and
`classic-hkcu-expanded-window.png`) sit beside it, and records a failed check if either is missing.
The E2E test does not start classic regedit itself. The classic captures were taken by hand from a
read-only script, which started regedit with `__COMPAT_LAYER=RunAsInvoker` on the child process only,
so a UAC prompt never appeared.

### Classic regedit already running

A capture script that finds another classic regedit window refuses to launch or stop it. The
measurement in this session was made from a fresh launch, not from an attached window.
