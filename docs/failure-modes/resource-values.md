# Resource value failure modes

Scope: the binary parser in `src/Bwc.Registry/Resources` for the three hardware resource value types,
`REG_RESOURCE_LIST` (8), `REG_FULL_RESOURCE_DESCRIPTOR` (9), and `REG_RESOURCE_REQUIREMENTS_LIST`
(10), plus the read-only viewer dialogs in `src/Bwc.UI/Dialogs` that show their parse result. The
parser is isolated code: it takes a byte array and returns a result. It never reads the registry,
never writes, and never throws for input data. This document was written before the parser. Each
failure mode names the guard and the test that proves it.

The layouts used here are the 64-bit kernel layouts that Windows writes into the registry. The
partial descriptor is 20 bytes (type, share disposition, flags, then a 16-byte union whose
`Interrupt` member holds an 8-byte affinity). The IO requirements descriptor is 32 bytes. The
measured stride of the real values is checked in the engine tests against the bytes read from
`HKLM\HARDWARE`.

## 1. Length and count bounds

### 1.1 Empty value

A zero-length value has no count field. `REG_RESOURCE_LIST` needs at least 4 bytes for `Count`, and
the other two types need their fixed headers. The parser returns `Truncated` with the offset 0 and
the required length. The viewer shows the same message as a malformed value (section 5).

Guard: every read goes through a bounds-checked cursor. Test: `Resource_EmptyValue_IsTruncated`.

### 1.2 Count of zero

`Count = 0` is valid. The list has no full descriptors and the viewer shows zero rows. This is not
an error, and it is not the same as an empty value. Test: `Resource_ZeroCount_IsEmptyList`.

### 1.3 Forged count larger than the data

A count of `0xFFFFFFFF` or any count whose descriptors cannot fit in the remaining bytes returns
`CountExceedsData`. The parser checks `count <= remaining / minimumElementSize` before it allocates a
list or a descriptor array. No array is sized from the count alone. Test:
`Resource_ForgedCount_RejectedBeforeAllocation`.

### 1.4 Multiplication overflow

`count * 20` and `count * 32` can overflow a 32-bit integer for counts near `2^27`. Every size is
computed in 64-bit arithmetic and compared with the remaining length. A product above `int.MaxValue`
is treated as `CountExceedsData`. Test: `Resource_CountTimesStride_DoesNotOverflow`.

### 1.5 Partial count that does not fit in the full descriptor

A full descriptor whose partial count exceeds the bytes left in the value returns `CountExceedsData`.
The parser does not clamp the count to the bytes available, because a clamped list would show rows
that do not exist in the data. Test: `Resource_PartialCountOverrun_IsRejected`.

### 1.6 Trailing bytes

Bytes after the last declared descriptor are not part of the list. The parser reports the number of
trailing bytes in the result and still returns the declared rows. The viewer does not show them.
Whether classic shows trailing bytes is recorded in `docs/parity/oracle/regedit-resource-viewers.json`
and matched by the viewer. Test: `Resource_TrailingBytes_AreReportedNotShown`.

## 2. Descriptor fields

### 2.1 Unknown partial descriptor type

Partial types are `0` (null), `1` (port), `2` (interrupt), `3` (memory), `4` (DMA), `5` (device
specific), `6` (bus number), and `7` (large memory). Any other value returns `UnknownDescriptorType`
with the offset and the raw type. The parser does not guess a size for an unknown type, because the
next descriptor's offset would be wrong. Test: `Resource_UnknownPartialType_StopsWithOffset`.

### 2.2 Device specific data longer than the value

A device specific descriptor carries `DataSize` bytes after the descriptor. If `DataSize` is larger
than the remaining bytes the result is `Truncated` with the offset of the data. The data bytes
are never copied past the end of the array. Test: `Resource_DeviceDataOverrun_IsRejected`.

### 2.3 Device specific data size is huge

A `DataSize` of `0xFFFFFFFF` is rejected by the same check. The sum `offset + DataSize` is computed in
64-bit arithmetic so that it cannot wrap. Test: `Resource_DeviceDataSizeWraps_IsRejected`.

### 2.4 Unknown interface type and bus number

`INTERFACE_TYPE` values outside the known range (0 to 15 and the -1 sentinel) and any bus number are
kept as raw numbers. The name shown is the number in decimal. A bad interface type is not an error:
classic shows the number. Test: `Resource_UnknownInterfaceType_KeepsRawNumber`.

### 2.5 Flags with unknown bits

Partial descriptor flags and memory and port flag bits that have no name are kept as the raw value.
The viewer shows the raw hexadecimal word for them. Test: `Resource_UnknownFlagBits_AreKept`.

### 2.6 Large memory descriptor

Type 7 uses a 64-bit length selected by its flags (40, 48, or 64 bit lengths). A flag combination that
is not one of the three known sizes returns `UnknownDescriptorType` with the flags, not a guessed
length. Test: `Resource_LargeMemoryUnknownLength_IsRejected`.

## 3. Requirements list specifics

### 3.1 ListSize disagrees with the value length

`IO_RESOURCE_REQUIREMENTS_LIST.ListSize` is the size of the whole structure. If it is larger than the
value, the result is `SizeMismatch` and no rows are shown. If it is smaller than the header and the
lists it declares, the result is `SizeMismatch` too. Test: `Requirements_ListSizeTooLarge_IsRejected`.

### 3.2 Alternative list count

`AlternativeLists` is the number of `IO_RESOURCE_LIST` entries that follow. A forged count is checked
against the remaining bytes with the same rule as section 1.3. Test:
`Requirements_ForgedAlternativeCount_IsRejected`.

### 3.3 Descriptor count in a list

Each `IO_RESOURCE_LIST` has its own `Count`. Versions and revisions are kept as numbers and not
validated, because classic accepts any value. Test: `Requirements_ListCountOverrun_IsRejected`.

### 3.4 Option bits

The `Option` byte marks preferred, alternative, and required descriptors. Unknown option values are
kept as raw numbers. The viewer shows the Preferred and Alternative captions only from the template,
not from the option byte, until the measured behavior in the oracle json says otherwise. Test:
`Requirements_UnknownOption_IsKept`.

## 4. Crafted and real bytes

### 4.1 Real bytes are the reference

Each layout is tested with bytes read from real values under `HKLM\HARDWARE` (read only): the
`.Translated` value under `HARDWARE\RESOURCEMAP`, `HARDWARE\DESCRIPTION\System` configuration data,
and a `BasicConfigVector` under `SYSTEM\CurrentControlSet\Enum` when readable. The expected rows are
written beside the test. Test: `Resource_RealHardwareBytes_MatchRecordedRows`.

### 4.2 Crafted bytes

Every failure mode above has crafted bytes in the test, built with a small writer that uses the same
layout constants as the parser. A test that builds its input with the parser's own constants can only
prove the parser is self-consistent, so the layout constants are also checked against the real bytes
in 4.1.

### 4.3 Random bytes

A fixed-seed random run (deterministic, 20000 inputs of length 0 to 256) must return a result for
every input and never throw. Test: `Resource_RandomBytes_NeverThrow`.

## 5. Viewer behavior on malformed data

### 5.1 Malformed value opens the viewer with a message

A value that fails to parse still opens the viewer read only. The viewer shows the raw bytes through
the hex viewer path and a single line naming the error kind. It never opens an editor for the value.
Classic behavior for each malformed sample is recorded from the oracle run before the viewer is
implemented; the viewer follows that measurement.

### 5.2 Size of the viewer for many rows

A valid value with thousands of descriptors is shown in a list that virtualizes rows. The count is
capped only by the value size, which is capped by the registry value limit.

### 5.3 Writes

The viewers never write the value. Modify, Enter, and double-click open the viewer, and the registry
is not touched. Test: the E2E suite checks the value bytes before and after the viewer closes.

## 6. Engine tests and guards

| Failure mode | Guard | Test |
| --- | --- | --- |
| 1.1 empty value | cursor bounds | `Resource_EmptyValue_IsTruncated` |
| 1.2 zero count | count of zero is a valid empty list | `Resource_ZeroCount_IsEmptyList` |
| 1.3 forged count | count checked against remaining bytes before allocation | `Resource_ForgedCount_RejectedBeforeAllocation` |
| 1.4 multiplication overflow | 64-bit sizes | `Resource_CountTimesStride_DoesNotOverflow` |
| 1.5 partial overrun | count checked per full descriptor | `Resource_PartialCountOverrun_IsRejected` |
| 1.6 trailing bytes | reported, not shown | `Resource_TrailingBytes_AreReportedNotShown` |
| 2.1 unknown partial type | stop with offset | `Resource_UnknownPartialType_StopsWithOffset` |
| 2.2 device data overrun | 64-bit end check | `Resource_DeviceDataOverrun_IsRejected` |
| 2.3 device data size wraps | 64-bit end check | `Resource_DeviceDataSizeWraps_IsRejected` |
| 2.4 unknown interface type | raw number kept | `Resource_UnknownInterfaceType_KeepsRawNumber` |
| 2.5 unknown flag bits | raw value kept | `Resource_UnknownFlagBits_AreKept` |
| 2.6 large memory length | unknown flags rejected | `Resource_LargeMemoryUnknownLength_IsRejected` |
| 3.1 ListSize mismatch | size checked against value | `Requirements_ListSizeTooLarge_IsRejected` |
| 3.2 alternative count | count checked | `Requirements_ForgedAlternativeCount_IsRejected` |
| 3.3 list count overrun | count checked | `Requirements_ListCountOverrun_IsRejected` |
| 3.4 unknown option | raw value kept | `Requirements_UnknownOption_IsKept` |
| 4.1 real bytes | recorded rows | `Resource_RealHardwareBytes_MatchRecordedRows` |
| 4.3 random bytes | no throw | `Resource_RandomBytes_NeverThrow` |

## 7. Viewer failure modes

These rows cover the read-only viewers in `src/Bwc.UI/Dialogs` (`ResourceListDialog`, `ResourceDescriptorDialog`,
`ResourceRequirementsDialog`, `ResourceDetailDialog`, `ClassicColumnList`), the mapping in
`src/Bwc.Regedit/Controllers/ResourceViewerFactory.cs`, and the resource branch of `ValueCommands`. The oracle is
`docs/parity/oracle/regedit-resource-viewers.json`. Its text and the local rows are untracked.

Note on order: the parser section above was written before the parser. This section was written after the viewer code
and before the E2E run. It is listed here because it is the record of what the viewer does.

### 7.1 Malformed data: deliberate difference

Classic regedit does not handle malformed resource data. In the oracle run, Modify on the seeded malformed
`Resource8` value (REG_RESOURCE_LIST, 16 bytes) and `Req10` (REG_RESOURCE_REQUIREMENTS_LIST) made regedit exit with
access violation 0xC0000005 in one run and open no viewer in another. The outcome changed between runs, so classic
has no stable behavior to copy.

BWC never crashes on this data. It opens the viewer with the rows that parsed first (`CmResourceParser` and
`IoRequirementsParser` return `PartialValue`), then shows the value error (`ShowValueReadErrorAsync`, the existing
"Cannot display {0}: Error reading the value's contents." message) owned by that viewer, once the viewer is active. An
error shown first let Windows hand the foreground to another app in the gap, and the viewer then opened behind it. The
rows stay visible behind the error. A value with no readable header opens the viewer with empty fields. This is a deliberate difference, recorded in the tracked oracle json as a notMeasured entry with a fixed reason.
Check: `ResourceViewers_OpenWithEnterAndShowTheClassicLayout` (malformed step) in
`tests/Bwc.E2E/Dialogs/ResourceViewerTests.cs`.

### 7.2 Display... and double-click enable rule (BWC design, not measured)

Display... (1203 in Resource Lists, 1506 in Requirements List) is disabled until a row is selected and enabled when one
is. A double-click on a row, or Enter on a selected row, does the same as Display.... The classic enable rule after a
row selection was not measured: the oracle selects rows with list messages, which do not send LBN_SELCHANGE, so the
buttons stayed disabled. This is a capture limit, not classic behavior. The template's style is WS_DISABLED for both
buttons, so BWC's rule is the coordinator's decision, not a copy of classic.

### 7.3 Data... enable rule (BWC design, not measured)

Data... (1204) is enabled only when a Device Specific row with data is selected. Pressing it opens the row's bytes in the
binary viewer under the caption "Binary Data" (inferred). The enable rule and the caption were not measured.

### 7.4 Requirement detail viewers (from templates, not measured)

No sample of REG_RESOURCE_REQUIREMENTS_LIST exists in HKLM on the measured machine, and Display... stayed disabled in the
capture, so the detail viewers 1601 (Memory), 1700 (Port), 1800 (Interrupt), and 1900 (DMA) were built from their
templates. Display... on a row of another type (bus number, configuration data, or unknown) opens nothing.
The Preferred and Alternative captions and the Share Disposition statics are template-disabled, so they are shown disabled.

### 7.5 Cell text formats (inferred)

These formats were not measured. They are inferred from the classic string table and the template captions:

| Field | Format | Source |
| --- | --- | --- |
| Bus number, slot number | signed decimal (0xFFFFFFFF shows -1) | measured: Configuration Data shows "-1" |
| Version, revision, channel, vector, level, reserved, data size | unsigned decimal | Resources statics show decimal |
| Addresses, lengths, alignment | `0x` plus at least 8 lowercase hex digits | RT_STRING 29 and 32 use `0x%08x` |
| Affinity | `0x` plus at least 8 lowercase hex digits | same |
| Interface type | RT_STRING 7500 to 7511 by value, otherwise "Invalid" (7542) | measured: -1 shows "Invalid" |
| Share disposition | RT_STRING 7580 to 7583 by value (Undetermined, Device Exclusive, Driver Exclusive, Shared) | template order |
| Interrupt type | "Latched" (7521) when flag bit 0 is set, otherwise "Level Sensitive" (7520) | flag name |
| Memory access | RT_STRING 7530 to 7532 from flag bits 0 and 1 | flag names |
| Port or memory type | "Port" (7541) when flag bit 0 is set, otherwise "Memory" (7540) | flag name |
| Requirements Alternative List | IO_RESOURCE_LIST index | inferred |
| Requirements Resource List | always 0 (one list per value) | inferred |
| Requirements Device Type | RT_STRING 7560 to 7564 by type 1 to 5, otherwise "Invalid" | type numbers |

Memory lengths use the 40-bit large layout (length times 256) only in the parser. The 48-bit and 64-bit large layouts and
the 256-byte unit were not checked against a classic viewer.

### 7.6 Column lists and rows

The classic column list is an owner-drawn `ColumnListBox`. In the oracle its list box messages returned zero rows, so the
row counts came from UI Automation only, and the item names were garbled. The cell texts were therefore not measured.
BWC draws each row as a Grid of text cells under a header row of equal-width columns.

### 7.7 Nested viewers and owners

A child viewer (Resources from Resource Lists, or a detail viewer from Requirements List) is opened with the parent viewer
as its owner, through `ShowAsync(parent HWND)`. The parent is disabled while the child is open and enabled again when the
child closes (`ModalDialogWindow`). `RegeditContext.ShowDialogAsync` does not open the child, because it refuses a second
modal. Escape closes the child and leaves the parent open.

### 7.8 Read-only

No viewer writes the value. Modify, Enter, double-click, and Modify Binary Data on these types open the same viewer, read
only. Display Binary Data still uses the hex viewer for every type (see `hex-viewer.md`).

### 7.9 Template statics and reserved fields

The partial descriptor keeps its two reserved fields, so the Resources device specific row shows the bytes that were read.
Partial descriptors of type 6 (bus number) have no list in the template and are not shown in Resources.

### 7.10 Checks

| Failure mode | Check |
| --- | --- |
| 7.1 malformed data | `ResourceViewers_OpenWithEnterAndShowTheClassicLayout`, malformed step |
| 7.2 Display... enable rule | same test, Display enabled after a row is selected |
| 7.3 Data... enable rule | same test, Data disabled before a row with data is selected |
| 7.4 requirement details | same test, Memory detail from the requirements list |
| 7.5 cell formats | same test, Resources and Requirements rows and fields |
| 7.7 nested owner | same test, the list stays open after Display... child closes |
| 7.8 read-only | same test, values are not written (the sandbox import is the only write) |

### 7.11 Inferred or not measured (complete list)

Every item below is a BWC decision or an inference, not a measured classic behavior. Each one is also covered by the
E2E check named in 7.10 where the check can see it. A change to any item needs a new oracle measurement first.

1. Cell formats for every column of Resource Lists, Resources, and Requirements List (table in 7.5). Only the signed bus
   number and the "Invalid" interface type were seen in classic output.
2. Address, length, alignment, and affinity format: `0x` plus at least 8 lowercase hex digits (from RT_STRING 29 and 32).
3. Decimal format for channel, vector, level, reserved fields, data size, version, revision, and requirement minimum and
   maximum channel and vector.
4. Display... enable rule: disabled until a row is selected, enabled when one is. Classic's rule after selection was not
   measured (capture limit, see 7.2). Enter and double-click do the same as Display....
5. Data... enable rule: enabled only for a Device Specific row with data (see 7.3). Not measured in classic.
6. The Data... viewer caption ("Binary Data") and its header ("Device Specific Data"). Not measured.
7. Display... on Resource Lists opens the Resources viewer for the selected row. Not measured; the template only gives the
   child id.
8. Requirements Alternative List column: the IO_RESOURCE_LIST index. Not measured.
9. Requirements Resource List column: always 0, since one value holds one list. Not measured.
10. Requirements Device Type column: the type name from RT_STRING 7560 to 7564, and "Invalid" for other types. Not measured.
11. Requirement detail viewers (Memory, Port, Interrupt, DMA): built from their templates only (see 7.4). No sample was
    reachable in the oracle.
12. Display... on a requirement of another type (bus number, configuration data, unknown) opens nothing. Not measured.
13. Memory and port large-length units: the 40-bit large memory length is Length40 times 256 bytes. Checked only against
    the real Physical Memory bytes read from HKLM, not against a classic viewer. The 48-bit and 64-bit large layouts
    (length units 64 KiB and 4 GiB) are from the kernel layout, not verified.
14. Interrupt type from flag bit 0 (latched) and port type from flag bit 0 (I/O). Flag names, not measured.
15. Memory access from flag bits 0 and 1 (read/write, read only, write only). Flag names, not measured.
16. Share disposition names in index order: Undetermined 0, Device Exclusive 1, Driver Exclusive 2, Shared 3. The order of
    RT_STRING 7580 to 7583 is assumed to match the enum.
17. Malformed data: the value error text (`ShowValueReadErrorAsync`) followed by the rows that parsed. This is a BWC
    decision (7.1); classic crashed or opened nothing.
18. Empty header fields for a malformed full descriptor with no readable header. BWC decision.
19. The requirement IO descriptor stride of 32 bytes. Not checked against a real sample (none was readable).
20. The Preferred and Alternative captions are shown disabled. The template says so; classic's state for each one was not
    measured.
21. The ColumnListBox cell text was never read from classic (7.6). Column titles and widths come from the templates.

### 7.12 Viewer rows or fields invisible to UI Automation

A column list is a Grid, and a Grid has no automation peer, so the AutomationId on it never reaches UI Automation. The
rows of Resource Lists, Resources, and Requirements List then read as empty to the E2E checks even though they are drawn.
Guard: the column list's AutomationId is set on its inner ListView, the control with a peer, and each ListItem Name
carries its cells joined with "|". A static field (Bus Number, Interface Type, Version, Revision, Access, Minimum Address)
must read its filled text as its UI Automation Name, so ClassicControlFactory.SetStaticText sets the Name with the text.
A static that the template disables (Share Disposition) must report disabled too. A TextBlock is sealed and has no IsEnabled, so the static is hosted in a disabled ClassicDisabledText, which UI
Automation reports disabled. Open, not fixed: after a nested viewer closes, the keyboard input target stays on the main
window, so Escape does not close the list that opened it (ModalDialogWindow.RestoreOwner activates the owner but does not restore its key focus).
Check: `ResourceViewers_OpenWithEnterAndShowTheClassicLayout`, the `resources.*.rows`, `resources.descriptor.bus` and
`resources.requirements.access` checks. Select on a ListItem must raise the list's SelectionChanged (`resources.list.display-enabled`).

## 8. Name tables kept equal across Bwc.Registry and Bwc.UI

The resource names are kept twice: `ResourceNames` in `src/Bwc.Registry/Resources/ResourceNames.cs` (English
literals, used by the text export) and the `.resw` entries that `ResourceText` reads in `src/Bwc.UI`. Bwc.UI has no
reference to Bwc.Registry, so nothing in the build links the two. Drift fails silently: the export and the viewers
would print different words for the same value.

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| 8.1 | Bus name edited in one table only | Export and viewer disagree | Each INTERFACE_TYPE 0 to 11 and the out-of-range value match the `ResourceBusType<n>` and `ResourceInvalid` entries | `InterfaceTypeNamesMatchTheUiResources` |
| 8.2 | Share disposition, partial type, memory access, interrupt, or port name edited in one table | Same | Each value named in the engine table equals the `.resw` text for its key, and each out-of-range value equals `ResourceInvalid` | `DispositionAndDescriptorNamesMatchTheUiResources` |
| 8.3 | A key the UI reads is missing from the `.resw` | Viewer shows the key or an empty string | Every key the test names exists in `src/Bwc.UI/Strings/en-US/Resources.resw` | Both tests above (a missing key fails the lookup) |

The test reads the `.resw` by path from the repository root, found by walking up to `BetterWindowsConfig.slnx`. It
runs in the registry test step of the quality gate, so a drift fails the build's test step, not a later release.

## 9. Detail viewers, DMA and device-specific labels (F2b)

Scope: the four requirement detail viewers (Memory 1601, Port 1700, Interrupt 1800, DMA 1900, rows DLG-042, DLG-043,
DLG-046, DLG-047), and the DMA and device-specific labels that the viewers and the text export share (MSG-259 to
MSG-263, MSG-275, MSG-276). The classic dump (`docs/parity/oracle/regedit-resources.json`) is the oracle for the
static labels, control ids and the disabled state. Classic was not measured for the values (OQ-16), so each value
format is BWC's own (7.5) and is checked as BWC's format only. This section was written before the E2E checks and
before the change to `ResourceTextFormatter`.

### 9.1 A static label differs from the classic dialog

A template label with a typo, a missing colon, or another word would show a wrong caption next to a correct value.
Guard: none in the product. The check reads the dump's static controls (class Static, id 65535) of dialog 1601,
1700, 1800, and 1900 and requires each text as a Text element in the open viewer. Check:
`ResourceViewers_DetailViewersMatchClassicLabelsValuesAndReadOnly`, `resources.detail.<kind>.labels`.

### 9.2 A value field is wired to the wrong classic control id

A value that shows in the place of another field is not caught by 9.1. Guard: none in the product. The check maps each
value automation id to its classic id (table in the test) and requires the dump to hold a Static control with that id.
Check: `resources.detail.<kind>.ids`.

### 9.3 A template-disabled caption is enabled

Preferred (2001) and Alternative (2002) are WS_DISABLED in every detail dialog (style 0x58020000). The viewer must show
both disabled, and the dump style must carry the disabled bit (0x08000000). Guard: `ClassicDisabledText`. Check:
`resources.detail.<kind>.template-disabled`.

### 9.4 A requirement row opens the wrong viewer

Type 1 opens Port, 2 opens Interrupt, 3 opens Memory, 4 opens DMA (`ResourceRequirementsDialog.OpenSelectedAsync`). A
swapped mapping shows another field set. Check: `resources.detail.<kind>.opened`, with one seeded row per type in
the order Port, Interrupt, DMA, Memory.

### 9.5 A field decodes from the wrong bytes

Length, alignment, minimum and maximum come from the IO descriptor union (Length 0, Alignment 4, Minimum 8, Maximum 16
for port and memory; Minimum vector 0, maximum vector 4 for interrupt; minimum channel 0, maximum channel 4 for DMA).
The seed uses distinct values in each field, so a swapped offset shows as a wrong text. Check:
`resources.detail.<kind>.values`, which compares each field with the value the seed encodes.

### 9.6 A field is editable

The viewer is read only (classic: "Read-only edits: none", regedit-audit DLG-042). A text box in the viewer would let a
user change what the viewer shows, and Modify would then look like a write. Check: `resources.detail.<kind>.read-only`
(no Edit control in the viewer, and the value elements are not editable).

### 9.7 Escape on the detail closes the list

Escape closes the detail and leaves the Requirements List open, so the next row can be opened. A detail that closes
the list too would hide the next row. Check: `resources.detail.<kind>.closed` and `resources.detail.list-open`.

### 9.8 Column titles of the Resources viewer

The Resources viewer (1305) shows classic column titles from the dump: DMA "Channel", "Port" (1306), Interrupt "Vector",
"Level", "Affinity", "Type" (1307), Memory "Physical Address", "Length", "Access" (1308), Port "Physical Address",
"Length", "Type" (1309), and Device specific "Reserved1", "Reserved2", "Data Size" (1304). A title that differs
from the dump is a label drift. Guard: none in the product. Check: `resources.descriptor.headers`. The titles are
Text elements of the viewer, so UI Automation reads them as names.

### 9.9 A DMA or device-specific label in the text export drifts from the classic string

The labels `Channel:`, `Port:`, `Reserved1:`, `Reserved2:`, `Data:`, `Minimum Channel:`, and `Maximum Channel:` are
RT_STRING 8168 to 8172, 8191, and 8192 of the dump. The export must print each one at its classic text (padding
included), and it must not print the retired `Data Size:` label (string 8107, which contradicts the golden). Check:
`ResourceTextExport_DmaAndDeviceSpecificLabelsAndLayout` (see `docs/failure-modes/export.md` section 2.1).

### 9.10 Layout is extrapolated

The label text of 9.9 is classic. The position of each line, the value format of channel and port, and the
device-specific data line are not measured, because no classic text export holds these descriptors (export.md
section 2, open point 3). The check asserts BWC's layout and labels the layout extrapolated.

### 9.11 Not measured and not checked

- Classic's value text in the detail viewers and in the Resources columns (OQ-16). The values are BWC's formats (7.5).
- The position and size of each static control (DLU). The check does not read control rectangles of the template.
- The viewer caption (Memory, Port, Interrupt, DMA) is from the template; the check does not read it.
- Requirement rows of type 6 (bus number) and unknown types open nothing (7.4); no check seeds them.

### 9.12 Checks

| Failure mode | Check |
| --- | --- |
| 9.1 static label | `resources.detail.<kind>.labels` |
| 9.2 control id | `resources.detail.<kind>.ids` |
| 9.3 template-disabled caption | `resources.detail.<kind>.template-disabled` |
| 9.4 row to viewer mapping | `resources.detail.<kind>.opened` |
| 9.5 decode offsets | `resources.detail.<kind>.values` |
| 9.6 editable field | `resources.detail.<kind>.read-only` |
| 9.7 Escape | `resources.detail.<kind>.closed`, `resources.detail.list-open` |
| 9.8 column titles | `resources.descriptor.headers` |
| 9.9 export labels | `text.dma.labels` and `text.device.labels` (export.md 2.1) |
