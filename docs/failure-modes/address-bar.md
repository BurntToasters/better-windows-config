# Address bar failure modes

Scope: the address bar in `src/Bwc.UI/Shell/ClassicWindowShell.xaml` and `.xaml.cs`, and the
navigation wiring in `src/Bwc.Regedit/RegeditRootView.xaml.cs`. The bar is a single line `TextBox`
with `AutomationId="AddressBar"` and `AutomationProperties.Name="Address bar"`. It stays on the
19 DIP row used by the menu bar and does not change the tree, list, or splitter layout.

The shell owns only text and key handling. On Enter it raises
`AddressBarSubmitted(AddressBarSubmittedEventArgs.Text)` and does not navigate itself. On Escape
it restores the last committed path and moves focus to the tree. `RegeditRootView` parses the text,
resolves it against `RegistryTreeSource`, and updates the selection. The bar is non-modal: a
submission runs asynchronously and the tree and list keep working while it runs.

## Invalid path

`RegistryPath.TryParse` rejects empty text, unknown roots, empty components, `.` and `..`
components, and components containing a backslash or NUL. The view trims outer whitespace and
trailing backslashes first, then reports a parse failure in the existing `ErrorDialog` with the
message from `DialogAddressBarInvalidPath`. The attempted text is not applied. The previous
address text, tree selection, list contents, and `LastKey` are unchanged. The dialog is the only
modal step and it does not block the rest of the process.

## Missing key

A syntactically valid path can name a key that no longer exists. `FindPathAsync` walks the path one
component at a time and returns null when a child is missing. The view restores the previous
address text, keeps the current selection and values, and shows `DialogAddressBarKeyNotFound` in
an `ErrorDialog`. No exception escapes the submission; it is caught inside the existing command
runner as a final backstop.

## Access denied

`RegistryReader` reports access failures as `RegistryOperationException`.
`RegistryTreeSource.FindPathAsync` catches that exception, logs the display path and Win32 error,
and returns null. The address bar therefore treats an unreadable key like a missing key: previous
selection stays, text is restored, and the not found or unreadable message is shown. Expanding the
key later still shows a node with an empty value list, as the tree does elsewhere.

## Computer

The bare text `Computer` is matched case-insensitively before parsing, including a trailing
backslash after normalization. It selects the virtual `Computer` node, sets the address bar to
`Computer`, shows an empty list, and records `LastKey` as `Computer`. `Computer\HKEY...` is handled
by `RegistryPath.TryParse`, so both the full form and the bare form reach the same node.

## Abbreviations

`RegistryPath.TryParse` accepts `HKCR`, `HKCU`, `HKLM`, `HKU`, and `HKCC` for the five predefined
roots, with or without the `Computer\` prefix. The address bar always shows the canonical display
path from the resolved node, which uses the full `HKEY_...` names and the casing returned by the
registry. Abbreviations are an input convenience only; they are never stored in `LastKey`.

## Trailing slash

Outer whitespace and all trailing backslashes are removed before parsing, so `HKCU\Software\`,
`HKCU\Software\\`, and `Computer\` are accepted. Removing the slash from a bare root is harmless;
removing it from an empty or all-slash value leaves an empty string, which is reported as an
invalid path.

## Long path

Component length is limited to 255 characters by `RegistryPath`. The full display path can still
be thousands of characters, which is longer than the visible address bar. The text box scrolls
horizontally and keeps the caret visible while editing. Resolution walks the path on the thread
pool through `FindPathAsync`; a path that exceeds the registry limit fails at some component and
returns null, so it ends in the not found dialog rather than an unhandled exception. The UI thread
is not blocked by the walk.

## Editing while a dialog is open

Navigation itself is non-modal and never disables the tree or list. Dialogs remain modal on
purpose: `ModalDialogWindow` disables the owner window while an error, prompt, or confirmation is
open, so keystrokes go to the dialog and cannot reach the address bar. If a navigation completes
while some other modal is open, the tree and address text are updated behind the dialog; the
dialog is not closed or duplicated because `RegeditRootView.ShowDialogAsync` refuses a second
modal.

## Escape behavior

Escape does not navigate and does not show a dialog. The shell sets the text back to `CurrentPath`,
the last committed display path, and moves focus to the tree. Any unsubmitted edits are discarded.
If the tree cannot take focus (for example while a flyout still owns it), focus stays where WinUI
puts it and no exception is thrown. The next Tab or click recovers normal focus navigation.

## Stale async navigation

Every submission increments a navigation generation. A pending `FindPathAsync` result is applied
only when its generation is still current. A second Enter, or a tree click, makes the older result
stale and it is dropped without changing the selection or text. Settings restore uses the same
generation check so a slow LastKey walk cannot override a path the user submitted after startup.
The generation is not incremented by the view's own `SetRoots` and `SelectNode` pair, so a
successful navigation is not cancelled by the selection event it causes.

## Focus

Enter on a successful navigation moves focus to the tree so arrow keys and type-ahead work
immediately. A failed navigation restores the text and returns focus to the address bar before the
error dialog opens, so the edit can be corrected after the dialog closes. Escape always asks the
tree for keyboard focus. The address bar itself remains a normal tab stop, and typing in it never
starts tree type-ahead.

## Case-insensitivity

Root names, `Computer`, and key components are compared case-insensitively by
`RegistryPath.TryParse` and `FindPathAsync`. A path typed as `hkcu\software\bwc-e2e` resolves to
the same node as the canonical form. The address bar shows the actual registry casing from the
resolved node, not the casing that was typed.

## Line endings and whitespace

The leading and trailing whitespace from a paste or from a keyboard is trimmed. Interior spaces
are preserved because registry key names may contain them. A single line text box has no newline
entry, but a pasted carriage return or line feed that lands inside the text stays inside the path
and makes a component unresolvable, which ends in the not found dialog. It never splits the input
into two commands. NUL characters are rejected by `RegistryPath`.

## Settings restore

`LastKey` is written only from `OnTreeSelectionChanged`, which now sets `Shell.CurrentPath`. A
successful address bar navigation changes the tree selection and therefore updates `LastKey`; a
failed or abandoned edit does not. On the next launch `ApplySettings` walks the saved path with
the same `FindPathAsync` used by the address bar. If the key was deleted, the view stays on
`Computer` and logs a warning instead of showing an error dialog at startup. The address bar
visibility and list column widths are unaffected by address bar navigation.
