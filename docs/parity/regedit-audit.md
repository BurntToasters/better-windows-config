# Better Windows Config: regedit parity audit

## Method

This file is the first full audit of `docs/parity/regedit-inventory.md` (release 0.1.2, branch `next-0.1.2`,
base `ed0c508`). Each of the 535 inventory rows has one line below. The Status column of the inventory is
kept equal to this file. Refreshed at 7d3f8f2 for the commits after 28d165c.

- `pass`: a named test or golden proves the row. E2E tests are named by method (`tests/Bwc.E2E`), engine
  tests by class (`tests/Bwc.Registry.Tests`). Informational rows that state no behavior say so.
- `declared-difference`: BWC differs on purpose. The reason is in the row.
- `gap`: not proven or not built. The Piece column names the 0.1.2 work piece that closes it (see the plan
  pieces: O1 oracle measurements, V1 value identity and display, K1 keyboard, menus and small dialogs,
  P1 Print, D1 resource viewers, R2 remote UI, S1 Permissions, S1b Permissions prompts, X1 accessibility, F2 audit leftovers; Elevated labels the rows that need the user's elevated runner).

MSG rows were sorted by a scan of the BWC sources for each RT_STRING text from
`docs/parity/oracle/regedit-resources.json`: text equal in a resource file or source literal is `pass`
through the string parity check (committed by O1), text that differs or is missing is a `gap` unless a
reason is given. Strings that the scan could not find but a test shows on screen are re-checked in F2.

When a piece closes a gap, it changes the row here and in the inventory to `pass` with its test, or to
`declared-difference` with the reason.

## Totals

| Section | pass | declared-difference | gap |
| --- | --- | --- | --- |
| MENU | 35 | 0 | 2 |
| CTX | 41 | 0 | 0 |
| DLG | 42 | 7 | 2 |
| ACC | 28 | 0 | 0 |
| MSG | 221 | 54 | 1 |
| VIEW | 19 | 2 | 0 |
| BEH | 43 | 6 | 0 |
| EXP | 32 | 0 | 0 |
| All (535) | 461 | 69 | 5 |

## Gaps by piece

| Piece | Rows |
| --- | --- |
| S1b | MSG-175 |
| Elevated | MENU-005, MENU-006, DLG-007, DLG-008 |

## Rows

### MENU

| ID | Status | Evidence or reason | Piece |
| --- | --- | --- | --- |
| MENU-001 | pass | Menus_PopupItemHeightsAndSeparatorLinesMatchMeasurement; Shell_MenuTableMatchesCommandStates |  |
| MENU-002 | pass | Shell_MenuTableMatchesCommandStates; ImportExport_UsesOwnedDialogsAndRefreshesTheSandbox; Hives_LoadDialogIsOwnedAndCancelLeavesNoMountedKey; AppServices_HiveRulesEnableLoadAndUnloadByRoot |  |
| MENU-003 | pass | Shell_MenuTableMatchesCommandStates; ImportExport_UsesOwnedDialogsAndRefreshesTheSandbox; Hives_LoadDialogIsOwnedAndCancelLeavesNoMountedKey; AppServices_HiveRulesEnableLoadAndUnloadByRoot |  |
| MENU-004 | pass | Shell_MenuTableMatchesCommandStates; ImportExport_UsesOwnedDialogsAndRefreshesTheSandbox; Hives_LoadDialogIsOwnedAndCancelLeavesNoMountedKey; AppServices_HiveRulesEnableLoadAndUnloadByRoot |  |
| MENU-005 | gap | Owned Load Hive file dialog opens only with SeRestorePrivilege. Load is enabled only at HKLM and HKU (AppServices_HiveRulesEnableLoadAndUnloadByRoot); Hives_LoadHiveRefusesWithoutPrivilegeBeforeAskingForFile shows the refusal. Hives_ElevatedLoadAndUnloadRoundTripThroughTheMenu opens it at HKEY_USERS. Elevated, pending the user's elevated run (not run here) | Elevated |
| MENU-006 | gap | Hives_ElevatedLoadAndUnloadRoundTripThroughTheMenu invokes Unload Hive on the mount and confirms it (hives.md section 14). Enable rule: AppServices_HiveRulesEnableLoadAndUnloadByRoot. Elevated, pending the user's elevated run (not run here) | Elevated |
| MENU-007 | pass | Shell_MenuTableMatchesCommandStates; Remote_ConnectDialogIsOwnedAndCancelLeavesNoConnection (dialog content is DLG-031, R2) |  |
| MENU-008 | pass | Remote_DisconnectDialogRemovesComputer (loopback connection to 127.0.0.1); Shell_MenuTableMatchesCommandStates |  |
| MENU-009 | pass | Print_OwnedDialogCancelsAndCommandStateFollowsSelection (Print enabled at Computer, a hive and a key) |  |
| MENU-010 | pass | Shell_MenuTableMatchesCommandStates; Shell_CloseLeavesNoTestProcess |  |
| MENU-011 | pass | Shell_MenuTableMatchesCommandStates (New children); Editing_CreatesRenamesAndDeletesRegistryObjects |  |
| MENU-012 | pass | Shell_MenuTableMatchesCommandStates (New children); Editing_CreatesRenamesAndDeletesRegistryObjects |  |
| MENU-013 | pass | Shell_MenuTableMatchesCommandStates (New children); Editing_CreatesRenamesAndDeletesRegistryObjects |  |
| MENU-014 | pass | Shell_MenuTableMatchesCommandStates (New children); Editing_CreatesRenamesAndDeletesRegistryObjects |  |
| MENU-015 | pass | Shell_MenuTableMatchesCommandStates (New children); Editing_CreatesRenamesAndDeletesRegistryObjects |  |
| MENU-016 | pass | Shell_MenuTableMatchesCommandStates (New children); Editing_CreatesRenamesAndDeletesRegistryObjects |  |
| MENU-017 | pass | Shell_MenuTableMatchesCommandStates (New children); Editing_CreatesRenamesAndDeletesRegistryObjects |  |
| MENU-018 | pass | Shell_MenuTableMatchesCommandStates (New children); Editing_CreatesRenamesAndDeletesRegistryObjects |  |
| MENU-019 | pass | Shell_MenuTableMatchesCommandStates (New children); Editing_CreatesRenamesAndDeletesRegistryObjects |  |
| MENU-020 | pass | Permissions_EditMenuOpensOwnedAcluiDialogAndCancelRestoresMainWindow |  |
| MENU-021 | pass | Basics_DeleteWithTreeFocusDeletesTheKey; Basics_MultiDeleteTextAndDefaultRow |  |
| MENU-022 | pass | Tree_RenameCommitsOnEnterAndFocusLossAndCancelsOnEscape; Editing_CreatesRenamesAndDeletesRegistryObjects |  |
| MENU-023 | pass | Basics_CopyKeyNameAndF5KeepTheView |  |
| MENU-024 | pass | Find_EditMenuFindRunsFromTheKeyboard; Find_FollowsTheClassicTwoDialogSequence |  |
| MENU-025 | pass | Find_EditMenuFindRunsFromTheKeyboard; Find_FollowsTheClassicTwoDialogSequence |  |
| MENU-026 | pass | Shell_MenuTableMatchesCommandStates; Menus_ViewAddressBarCheckMarkMatchesVisibility; Shell_ViewSplitReturnsFocusAndEscapeRestoresWidth |  |
| MENU-027 | pass | Shell_MenuTableMatchesCommandStates; Menus_ViewAddressBarCheckMarkMatchesVisibility; Shell_ViewSplitReturnsFocusAndEscapeRestoresWidth |  |
| MENU-028 | pass | Shell_MenuTableMatchesCommandStates; Menus_ViewAddressBarCheckMarkMatchesVisibility; Shell_ViewSplitReturnsFocusAndEscapeRestoresWidth |  |
| MENU-029 | pass | Shell_MenuTableMatchesCommandStates; BinaryViewer_FollowsTheClassicLayoutAndFormats |  |
| MENU-030 | pass | Basics_CopyKeyNameAndF5KeepTheView |  |
| MENU-031 | pass | AppServices_FontDialogChangesRowHeightAndPersists; Font_FaceStyleAndSizePersistAcrossRestart |  |
| MENU-032 | pass | Favorites_AddRemoveNavigateAndReportMissingPath; FavoritesFixes_DefaultValueWhitespaceAndCulture (Add dialog layout is DLG-024, K1) |  |
| MENU-033 | pass | Favorites_AddRemoveNavigateAndReportMissingPath; FavoritesFixes_DefaultValueWhitespaceAndCulture (Add dialog layout is DLG-024, K1) |  |
| MENU-034 | pass | Favorites_AddRemoveNavigateAndReportMissingPath; FavoritesFixes_DefaultValueWhitespaceAndCulture (Add dialog layout is DLG-024, K1) |  |
| MENU-035 | pass | Favorites_AddRemoveNavigateAndReportMissingPath; FavoritesFixes_DefaultValueWhitespaceAndCulture (Add dialog layout is DLG-024, K1) |  |
| MENU-036 | pass | Shell_MenuTableMatchesCommandStates; AppServices_AboutShowsProductVersionAndCopyright |  |
| MENU-037 | pass | Shell_MenuTableMatchesCommandStates; AppServices_AboutShowsProductVersionAndCopyright |  |

### CTX

| ID | Status | Evidence or reason | Piece |
| --- | --- | --- | --- |
| CTX-001 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-002 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-003 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-004 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-005 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-006 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-007 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-008 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-009 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-010 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-011 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-012 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-013 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-014 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-015 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-016 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ExpandedKeyMenuShowsCollapse; Permissions_TreeContextMenuShowsEnabledPermissions (Computer-root menu measured by O1) |  |
| CTX-017 | pass | Classic removes item 918 from the shown hive and key menus (regedit-context.json); BWC matches (ContextMenus_KeyMenuHasNoGoToAndDeleteRenameDisabledOnHive). Ctrl+T still navigates (Parity_CtrlT_TogglesBetweenLocalMachineAndCurrentUser) |  |
| CTX-018 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ValueMenuDeleteRemovesOnlyTheValue; Basics_EditMenuModifyItemsFollowTheValueSelection |  |
| CTX-019 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ValueMenuDeleteRemovesOnlyTheValue; Basics_EditMenuModifyItemsFollowTheValueSelection |  |
| CTX-020 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ValueMenuDeleteRemovesOnlyTheValue; Basics_EditMenuModifyItemsFollowTheValueSelection |  |
| CTX-021 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ValueMenuDeleteRemovesOnlyTheValue; Basics_EditMenuModifyItemsFollowTheValueSelection |  |
| CTX-022 | pass | ContextMenus_OpenAndInvokeCommands; Robustness_ValueMenuDeleteRemovesOnlyTheValue; Basics_EditMenuModifyItemsFollowTheValueSelection |  |
| CTX-023 | pass | ContextMenus_OpenAndInvokeCommands (contextmenu.background.open); List_MouseSelectionAndEmptyArea |  |
| CTX-024 | pass | ContextMenus_OpenAndInvokeCommands (contextmenu.background.open); List_MouseSelectionAndEmptyArea |  |
| CTX-025 | pass | ContextMenus_OpenAndInvokeCommands (contextmenu.background.open); List_MouseSelectionAndEmptyArea |  |
| CTX-026 | pass | ContextMenus_OpenAndInvokeCommands (contextmenu.background.open); List_MouseSelectionAndEmptyArea |  |
| CTX-027 | pass | ContextMenus_OpenAndInvokeCommands (contextmenu.background.open); List_MouseSelectionAndEmptyArea |  |
| CTX-028 | pass | ContextMenus_OpenAndInvokeCommands (contextmenu.background.open); List_MouseSelectionAndEmptyArea |  |
| CTX-029 | pass | ContextMenus_OpenAndInvokeCommands (contextmenu.background.open); List_MouseSelectionAndEmptyArea |  |
| CTX-030 | pass | ContextMenus_OpenAndInvokeCommands (contextmenu.background.open); List_MouseSelectionAndEmptyArea |  |
| CTX-031 | pass | ContextMenus_OpenAndInvokeCommands (contextmenu.background.open); List_MouseSelectionAndEmptyArea |  |
| CTX-032 | pass | Remote_LoopbackConnectShowsRemoteRootsAndMenus: the remote computer node shows RT_MENU 107 (Export and Disconnect enabled), remote hives and keys show the key menu; the local Computer root shows the same RT_MENU 107 as measured (regedit-context.json) |  |
| CTX-033 | pass | Remote_LoopbackConnectShowsRemoteRootsAndMenus: the remote computer node shows RT_MENU 107 (Export and Disconnect enabled), remote hives and keys show the key menu; the local Computer root shows the same RT_MENU 107 as measured (regedit-context.json) |  |
| CTX-034 | pass | Remote_LoopbackConnectShowsRemoteRootsAndMenus: the remote computer node shows RT_MENU 107 (Export and Disconnect enabled), remote hives and keys show the key menu; the local Computer root shows the same RT_MENU 107 as measured (regedit-context.json) |  |
| CTX-035 | pass | Remote_LoopbackConnectShowsRemoteRootsAndMenus: the remote computer node shows RT_MENU 107 (Export and Disconnect enabled), remote hives and keys show the key menu; the local Computer root shows the same RT_MENU 107 as measured (regedit-context.json) |  |
| CTX-036 | pass | HexContextMenu_HasTheRt108ItemsAndStates (hex area of Edit Binary Value, regedit-context.json); text boxes show the system edit menu (ContextMenus_AddressBarHasTheSystemEditMenu) |  |
| CTX-037 | pass | HexContextMenu_HasTheRt108ItemsAndStates (hex area of Edit Binary Value, regedit-context.json); text boxes show the system edit menu (ContextMenus_AddressBarHasTheSystemEditMenu) |  |
| CTX-038 | pass | HexContextMenu_HasTheRt108ItemsAndStates (hex area of Edit Binary Value, regedit-context.json); text boxes show the system edit menu (ContextMenus_AddressBarHasTheSystemEditMenu) |  |
| CTX-039 | pass | HexContextMenu_HasTheRt108ItemsAndStates (hex area of Edit Binary Value, regedit-context.json); text boxes show the system edit menu (ContextMenus_AddressBarHasTheSystemEditMenu) |  |
| CTX-040 | pass | HexContextMenu_HasTheRt108ItemsAndStates (hex area of Edit Binary Value, regedit-context.json); text boxes show the system edit menu (ContextMenus_AddressBarHasTheSystemEditMenu) |  |
| CTX-041 | pass | HexContextMenu_HasTheRt108ItemsAndStates (hex area of Edit Binary Value, regedit-context.json); text boxes show the system edit menu (ContextMenus_AddressBarHasTheSystemEditMenu) |  |

### DLG

| ID | Status | Evidence or reason | Piece |
| --- | --- | --- | --- |
| DLG-001 | declared-difference | BWC hosts the Export range group in the system item dialog (IFileDialogCustomize) instead of the old common dialog child template, with the same controls: All, Selected branch and the branch edit 1282 (ExportDialogs_RangePanelWritesAllBranchTextAndRegedit4) |  |
| DLG-002 | pass | ExportDialogs_BranchPathEditRefusesMissingKey; ExportDialogs_BranchPathEditExportsTheTypedKey (regedit-export-range.json: 1282 stays enabled for both radios and starts with the selected key) |  |
| DLG-003 | pass | ClassicDialogs_EditStringAndDwordFollowTheTemplate; ClassicValueEditors_MultiStringWarningAndBinaryFollowTheTemplates |  |
| DLG-004 | pass | ClassicDialogs_EditStringAndDwordFollowTheTemplate; ClassicValueEditors_MultiStringWarningAndBinaryFollowTheTemplates |  |
| DLG-005 | pass | ClassicValueEditors_MultiStringWarningAndBinaryFollowTheTemplates (caption Edit Binary Value); HexContextMenu_HasTheRt108ItemsAndStates |  |
| DLG-006 | pass | HexEditing_EditsCancelsAndCopies; HexEditingFixes_TypeBackspaceAndPasteUseRealKeys |  |
| DLG-007 | gap | Hives_ElevatedLoadAndUnloadRoundTripThroughTheMenu checks caption, label, tab order and the 252 x 53 DLU geometry against RT_DIALOG 104. The label was 'Key name under HKEY_LOCAL_MACHINE:' and is now the classic '&Key Name:'. Elevated, pending the user's elevated run (not run here) | Elevated |
| DLG-008 | gap | Hives_ElevatedLoadAndUnloadRoundTripThroughTheMenu types the key name into control 1001, reads it back, and OK loads the hive under HKEY_USERS. Elevated, pending the user's elevated run (not run here) | Elevated |
| DLG-009 | pass | Print_ProgressCancelStopsJobAndLeavesNoOutput (progress text, Cancel, main window disabled while it runs; records not run without Microsoft Print to PDF); built from template 105 (ClassicDialogTemplates.cs) |  |
| DLG-010 | pass | Find_FollowsTheClassicTwoDialogSequence; find-sequence.json |  |
| DLG-011 | pass | Find_FollowsTheClassicTwoDialogSequence; find-sequence.json |  |
| DLG-012 | pass | Find_FollowsTheClassicTwoDialogSequence; find-sequence.json |  |
| DLG-013 | pass | Remote_DisconnectDialogRemovesComputer (dialog 107, multi-select list 1175 as in the template style) |  |
| DLG-014 | pass | Remote_DisconnectDialogRemovesComputer (dialog 107, multi-select list 1175 as in the template style) |  |
| DLG-015 | declared-difference | PrintDlgExW has no template handle, so template 108 cannot be passed (docs/failure-modes/print.md, Print dialog section). Classic opens the system print host, which shows no range radios (regedit-print.json, OQ-44). BWC prints the selected branch at a key and All at Computer. E2E: Print_DialogUiaMatchesOracle (no radio buttons in the host) |  |
| DLG-016 | declared-difference | PrintDlgExW has no template handle, so template 108 cannot be passed (docs/failure-modes/print.md, Print dialog section). Classic opens the system print host, which shows no range radios (regedit-print.json, OQ-44). BWC prints the selected branch at a key and All at Computer. E2E: Print_DialogUiaMatchesOracle (no radio buttons in the host) |  |
| DLG-017 | pass | Find_FollowsTheClassicTwoDialogSequence |  |
| DLG-018 | pass | Find_FollowsTheClassicTwoDialogSequence |  |
| DLG-019 | pass | ImportProgress_ShowsClassicProgressWithoutCancel; ImportProgress_DarkPaletteCaptionAndFace |  |
| DLG-020 | pass | ImportProgress_ShowsClassicProgressWithoutCancel; ImportProgress_DarkPaletteCaptionAndFace |  |
| DLG-021 | pass | ClassicDialogs_EditStringAndDwordFollowTheTemplate |  |
| DLG-022 | pass | ClassicDialogs_EditStringAndDwordFollowTheTemplate |  |
| DLG-023 | pass | NumberOverflow_DwordDecimalAnswers (prompt text of string 119, caption Overflow, No keeps the editor, Yes stores 4294967295); NumberOverflow_HexadecimalKeepsInputError. Caption and truncation rule are the spec from the text (classic-dialog.md 13) |  |
| DLG-024 | pass | AddFavoriteDialog_ClassicLayoutTabOrderAndDuplicateError; SmallDialogs_RectsMatchClassicMeasurements |  |
| DLG-025 | pass | AddFavoriteDialog_ClassicLayoutTabOrderAndDuplicateError; SmallDialogs_RectsMatchClassicMeasurements |  |
| DLG-026 | pass | Favorites_AddRemoveNavigateAndReportMissingPath |  |
| DLG-027 | pass | Favorites_AddRemoveNavigateAndReportMissingPath |  |
| DLG-028 | pass | ClassicValueEditors_MultiStringWarningAndBinaryFollowTheTemplates |  |
| DLG-029 | pass | ClassicValueEditors_MultiStringWarningAndBinaryFollowTheTemplates |  |
| DLG-030 | pass | ClassicValueEditors_MultiStringWarningAndBinaryFollowTheTemplates |  |
| DLG-031 | pass | Remote_ImportWritesThroughTheConnection; Remote_ImportCancelImportsNothing; Remote_ImportMixedRootsReportsStringOneThirtyTwo (dialog 115 picks the target computer after the file, only while a remote computer is connected) |  |
| DLG-032 | pass | Remote_ImportWritesThroughTheConnection; Remote_ImportCancelImportsNothing; Remote_ImportMixedRootsReportsStringOneThirtyTwo (dialog 115 picks the target computer after the file, only while a remote computer is connected) |  |
| DLG-033 | pass | ResourceViewers_OpenWithEnterAndShowTheClassicLayout; ResourceParserTests (regedit-resource-viewers.json: Enter opens the viewer; the Display... rule after a selection is not measured, a capture limit) |  |
| DLG-034 | pass | ResourceViewers_OpenWithEnterAndShowTheClassicLayout; ResourceParserTests (regedit-resource-viewers.json: Enter opens the viewer; the Display... rule after a selection is not measured, a capture limit) |  |
| DLG-035 | pass | NumberOverflow_QwordLayout (RT_DIALOG 117 rectangles via MapDialogRect, caption, tab order, read-only value name, hexadecimal default) |  |
| DLG-036 | pass | NumberOverflow_QwordDecimalAnswers (prompt text of string 120, Yes stores 18446744073709551615); NumberOverflow_QwordLayout (base switch at the QWORD maximum) |  |
| DLG-037 | pass | ResourceViewers_OpenWithEnterAndShowTheClassicLayout; ResourceParserTests (Resources viewer measured on a real HKEY_LOCAL_MACHINE\HARDWARE value; Data... rule and cell formats inferred, docs/failure-modes/resource-values.md 7.11) |  |
| DLG-038 | pass | ResourceViewers_OpenWithEnterAndShowTheClassicLayout; ResourceParserTests (Resources viewer measured on a real HKEY_LOCAL_MACHINE\HARDWARE value; Data... rule and cell formats inferred, docs/failure-modes/resource-values.md 7.11) |  |
| DLG-039 | pass | ResourceViewers_OpenWithEnterAndShowTheClassicLayout; ResourceParserTests; built from templates 1501, 1601 and 1700: no readable requirements list exists on this machine, so classic is not measured (resource-values.md 7.11) |  |
| DLG-040 | pass | ResourceViewers_OpenWithEnterAndShowTheClassicLayout; ResourceParserTests; built from templates 1501, 1601 and 1700: no readable requirements list exists on this machine, so classic is not measured (resource-values.md 7.11) |  |
| DLG-041 | pass | ResourceViewers_OpenWithEnterAndShowTheClassicLayout; ResourceParserTests; built from templates 1501, 1601 and 1700: no readable requirements list exists on this machine, so classic is not measured (resource-values.md 7.11) |  |
| DLG-042 | pass | Requirement detail viewers Memory 1601, Port 1700, Interrupt 1800, DMA 1900. ResourceViewers_DetailViewersMatchClassicLabelsValuesAndReadOnly checks each viewer's caption, every classic static text of its dump dialog, the control ids, the template-disabled Preferred and Alternative captions, the values decoded from the seed, read-only, and Escape. Classic value text not measured (OQ-16): values are BWC formats (resource-values.md 7.5) | |
| DLG-043 | pass | Port requirement detail viewer (1700): resources.detail.port.{caption,labels,ids,values,template-disabled,read-only,closed,list-open} (ResourceViewers_DetailViewersMatchClassicLabelsValuesAndReadOnly). Labels and ids from regedit-resources.json; values from the seed. Classic value text and DLU positions not measured (OQ-16) | |
| DLG-044 | declared-difference | Developer dialog with no menu item, accelerator or string that opens it (OQ-17); BWC does not implement it |  |
| DLG-045 | declared-difference | Developer dialog with no menu item, accelerator or string that opens it (OQ-17); BWC does not implement it |  |
| DLG-046 | pass | Interrupt requirement detail viewer (1800): resources.detail.interrupt.{caption,labels,ids,values,template-disabled,read-only,closed,list-open} (ResourceViewers_DetailViewersMatchClassicLabelsValuesAndReadOnly). Classic value text not measured (OQ-16) | |
| DLG-047 | pass | DMA requirement detail viewer (1900): resources.detail.dma.{caption,labels,ids,values,template-disabled,read-only,closed,list-open} (ResourceViewers_DetailViewersMatchClassicLabelsValuesAndReadOnly). Classic value text not measured (OQ-16) | |
| DLG-048 | declared-difference | Template 2020 is never opened by classic in this build: Display Binary Data opens variant B (2021) for every value type (regedit-binary.json). BWC does not implement variant A |  |
| DLG-049 | declared-difference | Template 2020 is never opened by classic in this build: Display Binary Data opens variant B (2021) for every value type (regedit-binary.json). BWC does not implement variant A |  |
| DLG-050 | pass | BinaryViewer_FollowsTheClassicLayoutAndFormats; HexViewer_ClassicBinaryDataFollowsTheFormats |  |
| DLG-051 | pass | BinaryViewer_FollowsTheClassicLayoutAndFormats; HexViewer_ClassicBinaryDataFollowsTheFormats |  |

### ACC

| ID | Status | Evidence or reason | Piece |
| --- | --- | --- | --- |
| ACC-001 | pass | Shell_TabCyclesTreeListAddressBar; Shell_ShiftTabReversesCycle |  |
| ACC-002 | pass | Shell_TabCyclesTreeListAddressBar; Shell_ShiftTabReversesCycle |  |
| ACC-003 | pass | Tree_RenameCommitsOnEnterAndFocusLossAndCancelsOnEscape |  |
| ACC-004 | pass | Find_FollowsTheClassicTwoDialogSequence |  |
| ACC-005 | pass | Keyboard_F4FocusesAddressBarAndOpensSuggestions; Keyboard_TypingShowsSubkeySuggestionsAndEnterNavigates (regedit-keys.json) |  |
| ACC-006 | pass | Basics_CopyKeyNameAndF5KeepTheView |  |
| ACC-007 | pass | Shell_F6CyclesPanesLikeTab |  |
| ACC-008 | pass | ContextMenus_ShiftF10OnListBackgroundAndComputerRoot (regedit-keys.json) |  |
| ACC-009 | pass | Basics_DeleteWithTreeFocusDeletesTheKey |  |
| ACC-010 | pass | Find_FollowsTheClassicTwoDialogSequence (Ctrl+F opens Find: find.ctrlF.open) |  |
| ACC-011 | pass | Print_CtrlPFromAddressBarOpensOwnedDialog (Ctrl+P from the address bar opens the owned print dialog, which is cancelled) |  |
| ACC-012 | pass | Parity_CtrlT_TogglesBetweenLocalMachineAndCurrentUser |  |
| ACC-013 | pass | Shell_CtrlLAndAltDFocusAddressBar |  |
| ACC-014 | pass | Shell_CtrlLAndAltDFocusAddressBar |  |
| ACC-015 | pass | Keyboard_Alt1ToAlt9DoNothing (classic changes nothing, regedit-keys.json) |  |
| ACC-016 | pass | Keyboard_Alt1ToAlt9DoNothing (classic changes nothing, regedit-keys.json) |  |
| ACC-017 | pass | Keyboard_Alt1ToAlt9DoNothing (classic changes nothing, regedit-keys.json) |  |
| ACC-018 | pass | Keyboard_Alt1ToAlt9DoNothing (classic changes nothing, regedit-keys.json) |  |
| ACC-019 | pass | Keyboard_Alt1ToAlt9DoNothing (classic changes nothing, regedit-keys.json) |  |
| ACC-020 | pass | Keyboard_Alt1ToAlt9DoNothing (classic changes nothing, regedit-keys.json) |  |
| ACC-021 | pass | Keyboard_Alt1ToAlt9DoNothing (classic changes nothing, regedit-keys.json) |  |
| ACC-022 | pass | Keyboard_Alt1ToAlt9DoNothing (classic changes nothing, regedit-keys.json) |  |
| ACC-023 | pass | Keyboard_Alt1ToAlt9DoNothing (classic changes nothing, regedit-keys.json) |  |
| ACC-024 | pass | Keyboard_AltUpSelectsParentFromTreeListAndAddress (regedit-keys.json) |  |
| ACC-025 | pass | Keyboard_BackspaceSelectsParentOnlyWithTreeFocus (regedit-keys.json) |  |
| ACC-026 | pass | Tree_NumpadKeysAndPageKeys |  |
| ACC-027 | pass | Tree_TypeAheadExtendsPrefixCyclesAndRestarts |  |
| ACC-028 | pass | List_OrderTypeAheadAndHeaderClick |  |

### MSG

| ID | Status | Evidence or reason | Piece |
| --- | --- | --- | --- |
| MSG-001 | pass | String parity check: text equal (MsgCaptionRegistryEditor) |  |
| MSG-002 | pass | String parity check: text equal (Bwc.Registry/RegFile/RegFileDocument.cs) |  |
| MSG-003 | pass | String parity check: text equal (DialogFindData) |  |
| MSG-004 | pass | String parity check: text equal (Bwc.Regedit/Controllers/AddressSuggestions.cs) |  |
| MSG-005 | pass | String parity check: text equal (Bwc.Regedit/Controllers/MessageCatalog.cs) |  |
| MSG-006 | pass | String parity check: text equal (ContextModifyBinary) |  |
| MSG-007 | pass | String parity check: text equal (Bwc.Registry/RegistryExportText.cs) |  |
| MSG-008 | pass | Format template; Editing_CreatesRenamesAndDeletesRegistryObjects shows New Key #1 |  |
| MSG-009 | pass | Format template; Editing_CreatesRenamesAndDeletesRegistryObjects shows New Key #1 |  |
| MSG-010 | pass | String parity check: text equal (ContextCollapse) |  |
| MSG-011 | pass | String parity check: text equal (ContextModify) |  |
| MSG-012 | pass | String parity check: text equal (Bwc.Regedit/Controllers/MessageCatalog.cs) |  |
| MSG-013 | pass | Format template; RegistryValueDisplayTests.DwordShowsOracleHexWidthAndUnsignedDecimal (0x00001388 (5000)) |  |
| MSG-014 | pass | String parity check: text equal (Bwc.Registry/RegistryValueDisplay.cs) |  |
| MSG-015 | pass | String parity check: text equal (Bwc.UI/Dialogs/ClassicDialogTemplates.cs) |  |
| MSG-016 | pass | Format template; ValueDisplayTests (QWORD rows compared with classic) |  |
| MSG-017 | pass | String parity check: text equal (Bwc.Registry/RegistryValueDisplay.cs) |  |
| MSG-018 | declared-difference | Classic shows string 19 (Computer) as the root in this build (regedit-100dpi.png); 34 is never shown (OQ-40) |  |
| MSG-019 | pass | String parity check: text equal (MsgRegistryEditingDisabled) |  |
| MSG-020 | pass | String parity check: text equal (DialogFindFinished) |  |
| MSG-021 | declared-difference | BWC opens the system object picker (Remote_ConnectDialogIsOwnedAndCancelLeavesNoConnection), which shows its own prompt text; the classic hint string is not passed to it |  |
| MSG-022 | pass | CommandLine_SwitchWithoutFilenameReportsStringFortyThree (/s, /e and /a alone: stderr line with string 43 and exit 2) |  |
| MSG-023 | pass | String parity check: text equal (MsgDeleteKeyConfirm) |  |
| MSG-024 | pass | String parity check: text equal (MsgCaptionConfirmKeyDelete) |  |
| MSG-025 | pass | String parity check: text equal (MsgDeleteValuesConfirm) |  |
| MSG-026 | pass | String parity check: text equal (MsgCaptionConfirmValueDelete) |  |
| MSG-027 | pass | String parity check: text equal (MsgDeleteValueConfirm) |  |
| MSG-028 | pass | String parity check: text equal (MsgUnloadHiveConfirm) |  |
| MSG-029 | pass | String parity check: text equal (MsgCaptionConfirmUnloadHive) |  |
| MSG-030 | pass | String parity check: text equal (MsgRestoreKeyConfirm) |  |
| MSG-031 | pass | String parity check: text equal (MsgRestoreKeyCaption) |  |
| MSG-032 | pass | String parity check: text equal (MsgCaptionRenameKeyError) |  |
| MSG-033 | pass | String parity check: text equal (MsgRenameKeyFailed) |  |
| MSG-034 | pass | String parity check: text equal (MsgRenameKeyTooLong) |  |
| MSG-035 | pass | String parity check: text equal (MsgRenameKeyExists) |  |
| MSG-036 | pass | String parity check: text equal (MsgRenameKeyBackslash) |  |
| MSG-037 | pass | String parity check: text equal (MsgRenameKeyEmpty) |  |
| MSG-038 | pass | String parity check: text equal (MsgCaptionRenameValueError) |  |
| MSG-039 | pass | String parity check: text equal (MsgRenameValueFailed) |  |
| MSG-040 | pass | String parity check: text equal (MsgRenameValueExists) |  |
| MSG-041 | pass | String parity check: text equal (MsgRenameValueEmpty) |  |
| MSG-042 | pass | String parity check: text equal (MsgCaptionDeleteKeyError) |  |
| MSG-043 | pass | String parity check: text equal (MsgDeleteKeyFailed) |  |
| MSG-044 | pass | String parity check: text equal (MsgCaptionDeleteValuesError) |  |
| MSG-045 | pass | String parity check: text equal (MsgDeleteValuesFailed) |  |
| MSG-046 | pass | String parity check: text equal (MsgCaptionOpenKeyError) |  |
| MSG-047 | pass | String parity check: text equal (MsgOpenKeyFailed) |  |
| MSG-048 | pass | String parity check: text equal (MsgOpenKeyFailedDetails) |  |
| MSG-049 | pass | String parity check: text equal (MsgCaptionDisplayValueError) |  |
| MSG-050 | pass | String parity check: text equal (MsgDisplayValueFailed) |  |
| MSG-051 | declared-difference | Out-of-memory message: the managed runtime ends the process with a crash report instead |  |
| MSG-052 | pass | NumberOverflow_DwordDecimalAnswers (caption Overflow). Choice of 110 over 111 is inferred from the text; see MSG-053 |  |
| MSG-053 | declared-difference | Not used by BWC. The dump does not say whether 110 or 111 is the overflow caption, and no classic prompt is identified as using 111 (Warning). Not measured: the oracle does not drive the overflow prompt |  |
| MSG-054 | pass | String parity check: text equal (MsgCaptionEditValueError) |  |
| MSG-055 | pass | String parity check: text equal (MsgEditValueReadFailed) |  |
| MSG-056 | pass | String parity check: text equal (MsgEditValueWriteFailed) |  |
| MSG-057 | pass | String parity check: text equal (MultiStringEmptyOne) |  |
| MSG-058 | pass | String parity check: text equal (MultiStringEmptyMany) |  |
| MSG-059 | declared-difference | Out-of-memory message: the managed runtime ends the process with a crash report instead |  |
| MSG-060 | pass | NumberOverflow_DwordDecimalAnswers (text of string 119 compared with line ends normalized) |  |
| MSG-061 | pass | NumberOverflow_QwordDecimalAnswers (text of string 120 compared with line ends normalized) |  |
| MSG-062 | pass | CommandLine_BinaryHiveRefusedWithStringOneTwentyThree (/s on a regf file: stderr text of string 123, exit 1, nothing written). Silent import route only; the window import route is not driven |  |
| MSG-063 | pass | String parity check: text equal (Bwc.Hub/MainWindow.xaml) |  |
| MSG-064 | pass | String parity check: text equal (MsgImportInvalidKey) |  |
| MSG-065 | pass | String parity check: text equal (MsgImportPrivileges) |  |
| MSG-066 | pass | String parity check: text equal (MsgImportRemoteSucceeded, string 128) |  |
| MSG-067 | pass | String parity check: text equal (MsgImportOpenFailed) |  |
| MSG-068 | pass | String parity check: text equal (MsgImportReadFailed) |  |
| MSG-069 | pass | String parity check: text equal (MsgImportRemoteAccess) |  |
| MSG-070 | pass | String parity check: text equal (MsgImportPartial) |  |
| MSG-071 | pass | String parity check: text equal (MsgImportNotRegistryFile) |  |
| MSG-072 | declared-difference | BWC reports every unrecognized header with 133 (MSG-071). Classic's trigger for 134 is not in the dump, and regedit /s (the oracle import route) prints no message, so the case cannot be measured |  |
| MSG-073 | pass | String parity check: text equal (MsgImportRemoteFileMissing) |  |
| MSG-074 | pass | String parity check: text equal (MsgExportOpenFailed) |  |
| MSG-075 | pass | String parity check: text equal (MsgExportWriteFailed) |  |
| MSG-076 | pass | String parity check: text equal (MsgExportPrivileges) |  |
| MSG-077 | pass | String parity check: text equal (MsgExportKeyInvalid) |  |
| MSG-078 | pass | String parity check: text equal (MsgPrintMemory) |  |
| MSG-079 | pass | String parity check: text equal (MsgPrintFailed) |  |
| MSG-080 | pass | String parity check: text equal (MsgPrintReadFailed) |  |
| MSG-081 | pass | String parity check: text equal (DialogAddressBarKeyNotFound) |  |
| MSG-082 | pass | String parity check: text equal (DialogConnectFailedTitle) |  |
| MSG-083 | pass | String parity check: text equal (DialogConnectLocal) |  |
| MSG-084 | pass | String parity check: text equal (MsgConnectFailed) |  |
| MSG-085 | pass | String parity check: text equal (MsgConnectRootsFailed) |  |
| MSG-086 | pass | String parity check: text equal (MsgConnectPermission) |  |
| MSG-087 | pass | String parity check: text equal (MsgCaptionCreateKeyError) |  |
| MSG-088 | pass | String parity check: text equal (MsgCreateKeyOpenFailed) |  |
| MSG-089 | pass | String parity check: text equal (MsgCreateKeyWriteFailed) |  |
| MSG-090 | pass | String parity check: text equal (MsgCreateKeyNoUniqueName) |  |
| MSG-091 | pass | String parity check: text equal (MsgCreateKeyPermission) |  |
| MSG-092 | pass | String parity check: text equal (MsgCaptionCreateValueError) |  |
| MSG-093 | pass | String parity check: text equal (MsgCreateValueWriteFailed) |  |
| MSG-094 | pass | String parity check: text equal (MsgCreateValueNoUniqueName) |  |
| MSG-095 | pass | String parity check: text equal (DialogFavoriteDuplicate) |  |
| MSG-096 | pass | String parity check: text equal (DialogErrorAddingFavorite) |  |
| MSG-097 | pass | String parity check: text equal (DialogFavoriteCaption) |  |
| MSG-098 | pass | FileDialogs_TitleAndFiltersMatchClassicStrings; FileDialogs_ExportDefaultExtensionIsREG (title, filter pieces and default extension compared with regedit-resources.json) |  |
| MSG-099 | pass | String parity check: text equal (DialogLoadHiveTitle) |  |
| MSG-100 | pass | String parity check: text equal (MsgLoadHivePrivileges) |  |
| MSG-101 | pass | String parity check: text equal (MsgLoadHiveFailed) |  |
| MSG-102 | pass | String parity check: text equal (MsgUnloadHivePrivileges) |  |
| MSG-103 | pass | String parity check: text equal (MsgUnloadHiveFailed) |  |
| MSG-104 | pass | String parity check: text equal (DialogUnloadHiveTitle) |  |
| MSG-105 | pass | String parity check: text equal (MsgUnloadHiveAccessDenied) |  |
| MSG-106 | pass | String parity check: text equal (MsgLoadHiveInUse) |  |
| MSG-107 | pass | String parity check: text equal (MsgLoadHiveAccessDenied) |  |
| MSG-108 | declared-difference | Out-of-memory message: the managed runtime ends the process with a crash report instead |  |
| MSG-109 | declared-difference | BWC shows the .reg export text (138 or 141) for hive save failures. Not built. The hive save needs SeBackupPrivilege, which the unelevated run does not hold, so string 213 cannot be driven unelevated or safely (import-export.md 20) |  |
| MSG-110 | declared-difference | BWC shows the .reg export text (138 or 141) for hive save failures. Not built. A write error during a hive save needs the privilege held (import-export.md 20), so string 214 cannot be driven unelevated or safely |  |
| MSG-111 | declared-difference | BWC shows the .reg export text (138 or 141) for hive save failures. Not built. A locked or unopenable destination is reached only after the privilege is held (import-export.md 20), so string 215 cannot be driven unelevated or safely |  |
| MSG-112 | pass | String parity check: text equal (MsgImportSucceeded) |  |
| MSG-113 | pass | String parity check: text equal (MsgImportRegistryAccess) |  |
| MSG-114 | pass | String parity check: text equal (MsgImportFileMissing) |  |
| MSG-115 | pass | String parity check: text equal (MsgImportRegistryFileCaption) |  |
| MSG-116 | pass | FileDialogs_TitleAndFiltersMatchClassicStrings; FileDialogs_ExportDefaultExtensionIsREG (title, filter pieces and default extension compared with regedit-resources.json) |  |
| MSG-117 | pass | FileDialogs_TitleAndFiltersMatchClassicStrings; FileDialogs_ExportDefaultExtensionIsREG (title, filter pieces and default extension compared with regedit-resources.json) |  |
| MSG-118 | pass | FileDialogs_TitleAndFiltersMatchClassicStrings; FileDialogs_ExportDefaultExtensionIsREG (title, filter pieces and default extension compared with regedit-resources.json) |  |
| MSG-119 | pass | String parity check: text equal (MsgImportConfirm) |  |
| MSG-120 | pass | FileDialogs_TitleAndFiltersMatchClassicStrings; FileDialogs_ExportDefaultExtensionIsREG (title, filter pieces and default extension compared with regedit-resources.json) |  |
| MSG-121 | declared-difference | Out-of-memory message: the managed runtime ends the process with a crash report instead |  |
| MSG-122 | declared-difference | File type name for a .reg registration; BWC does not register a .reg file type (OQ-41) |  |
| MSG-123 | declared-difference | BWC does not register a .reg Merge verb; Windows keeps its own (OQ-41) |  |
| MSG-124 | pass | String parity check: text equal (ContextGoToHkcu) |  |
| MSG-125 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-126 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-127 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-128 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-129 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-130 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-131 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-132 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-133 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-134 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-135 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-136 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-137 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-138 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-139 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-140 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-141 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-142 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-143 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-144 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-145 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-146 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-147 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-148 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-149 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-150 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-151 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-152 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-153 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-154 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-155 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-156 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-157 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-158 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-159 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-160 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-161 | declared-difference | Menu help text; classic has no status bar in this build (VIEW-018), so it is never shown (OQ-09) |  |
| MSG-162 | pass | String parity check: text equal (Bwc.Regedit/Dialogs/RegistryPermissionsDialog.cs) |  |
| MSG-163 | pass | String parity check: text equal (Bwc.Regedit/Dialogs/RegistryPermissionsDialog.cs) |  |
| MSG-164 | pass | String parity check: text equal (Bwc.Regedit/Dialogs/RegistryPermissionsDialog.cs) |  |
| MSG-165 | pass | String parity check: text equal (Bwc.Regedit/Dialogs/RegistryPermissionsDialog.cs) |  |
| MSG-166 | pass | String parity check: text equal (Bwc.Regedit/Dialogs/RegistryPermissionsDialog.cs) |  |
| MSG-167 | pass | String parity check: text equal (Bwc.Regedit/Dialogs/RegistryPermissionsDialog.cs) |  |
| MSG-168 | pass | String parity check: text equal (TextEditDelete) |  |
| MSG-169 | pass | String parity check: text equal (Bwc.Regedit/Dialogs/RegistryPermissionsDialog.cs) |  |
| MSG-170 | pass | String parity check: text equal (Bwc.Regedit/Dialogs/RegistryPermissionsDialog.cs) |  |
| MSG-171 | pass | String parity check: text equal (Bwc.Regedit/Dialogs/RegistryPermissionsDialog.cs) |  |
| MSG-172 | pass | String parity check: text equal (Bwc.Regedit/Dialogs/RegistryPermissionsDialog.cs) |  |
| MSG-173 | pass | String parity check: text equal (Bwc.Regedit/Dialogs/RegistryPermissionsDialog.cs) |  |
| MSG-174 | declared-difference | Not shown. No measured classic control has this text: the sheet and Advanced window show aclui's 'Special permissions' (oracle local/security-text.json). permissions.md section 43 |  |
| MSG-175 | gap | Text not in the source (string 7014, Registry &Key). It is the apply-to name of the Permission Entry page, which the oracle does not capture. Open: permissions.md sections 42 and 43 | S1b |
| MSG-176 | declared-difference | Not shown. Classic's measured check box text is aclui's 'Replace all child object permission entries...', and BWC shows that same control (permissions.md sections 16 and 43) |  |
| MSG-177 | declared-difference | Not shown. The audit replace check box needs SI_RESET_SACL_TREE, which BWC does not set, because the engine refuses SACL propagation (permissions.md sections 22 and 43) |  |
| MSG-178 | pass | String parity check: text equal (MsgReplaceSubkeysConfirm) |  |
| MSG-179 | declared-difference | Not shown. The audit replace question belongs to the check box of MSG-177, which BWC does not offer (permissions.md section 43) |  |
| MSG-180 | pass | String parity check: text equal (Bwc.Regedit/Dialogs/RegistryPermissionsDialog.cs) |  |
| MSG-181 | pass | String parity check: text equal (MsgSecurityRetrieveDenied) |  |
| MSG-182 | pass | String parity check: text equal (MsgSecurityRetrieveDeleted) |  |
| MSG-183 | pass | String parity check: text equal (MsgSecurityRetrieveFailed) |  |
| MSG-184 | pass | String parity check: text equal (MsgSecuritySaveDenied) |  |
| MSG-185 | pass | String parity check: text equal (MsgSecuritySaveDeleted) |  |
| MSG-186 | pass | String parity check: text equal (MsgSecuritySetFailed) |  |
| MSG-187 | pass | String parity check: text equal (MsgOwnerSetFailed) |  |
| MSG-188 | pass | String parity check: text equal (MsgSecuritySubkeysDenied) |  |
| MSG-189 | pass | String parity check: text equal (MsgSecuritySubkeysDeleted) |  |
| MSG-190 | pass | String parity check: text equal (MsgSecuritySubkeysFailed) |  |
| MSG-191 | pass | String parity check: text equal (PermissionsInheritThisKeyOnly) |  |
| MSG-192 | pass | String parity check: text equal (PermissionsInheritThisKeyAndSubkeys) |  |
| MSG-193 | pass | String parity check: text equal (PermissionsInheritSubkeysOnly) |  |
| MSG-194 | pass | String parity check: text equal (ResourceBusType0) |  |
| MSG-195 | pass | String parity check: text equal (ResourceBusType1) |  |
| MSG-196 | pass | String parity check: text equal (ResourceBusType2) |  |
| MSG-197 | pass | String parity check: text equal (ResourceBusType3) |  |
| MSG-198 | pass | String parity check: text equal (ResourceBusType4) |  |
| MSG-199 | pass | String parity check: text equal (ResourceBusType5) |  |
| MSG-200 | pass | String parity check: text equal (ResourceBusType6) |  |
| MSG-201 | pass | String parity check: text equal (ResourceBusType7) |  |
| MSG-202 | pass | String parity check: text equal (ResourceBusType8) |  |
| MSG-203 | pass | String parity check: text equal (ResourceBusType9) |  |
| MSG-204 | pass | String parity check: text equal (ResourceBusType10) |  |
| MSG-205 | pass | String parity check: text equal (ResourceBusType11) |  |
| MSG-206 | pass | String parity check: text equal (ResourceLevelSensitive) |  |
| MSG-207 | pass | String parity check: text equal (ResourceLatched) |  |
| MSG-208 | pass | String parity check: text equal (ResourceReadWrite) |  |
| MSG-209 | pass | String parity check: text equal (ResourceReadOnly) |  |
| MSG-210 | pass | String parity check: text equal (ResourceWriteOnly) |  |
| MSG-211 | pass | String parity check: text equal (ResourceMemoryType) |  |
| MSG-212 | pass | String parity check: text equal (ResourcePortType) |  |
| MSG-213 | pass | String parity check: text equal (ResourceInvalid) |  |
| MSG-214 | pass | String parity check: text equal (ResourcePortType) |  |
| MSG-215 | pass | String parity check: text equal (ResourceInterrupt) |  |
| MSG-216 | pass | String parity check: text equal (ResourceMemoryType) |  |
| MSG-217 | pass | String parity check: text equal (ResourceDma) |  |
| MSG-218 | pass | String parity check: text equal (ResourceDeviceSpecific) |  |
| MSG-219 | pass | String parity check: text equal (ResourceUndetermined) |  |
| MSG-220 | pass | String parity check: text equal (ResourceDeviceExclusive) |  |
| MSG-221 | pass | String parity check: text equal (ResourceDriverExclusive) |  |
| MSG-222 | pass | String parity check: text equal (ResourceShared) |  |
| MSG-223 | pass | String parity check: text equal (Bwc.Registry/RegistryValueType.cs) |  |
| MSG-224 | pass | String parity check: text equal (Bwc.Registry/RegistryValueType.cs) |  |
| MSG-225 | pass | String parity check: text equal (Bwc.Registry/RegistryValueType.cs) |  |
| MSG-226 | pass | String parity check: text equal (Bwc.Registry/RegistryValueType.cs) |  |
| MSG-227 | pass | String parity check: text equal (Bwc.Registry/RegistryValueType.cs) |  |
| MSG-228 | pass | String parity check: text equal (Bwc.Registry/RegistryValueType.cs) |  |
| MSG-229 | pass | String parity check: text equal (Bwc.Registry/RegistryValueType.cs) |  |
| MSG-230 | pass | String parity check: text equal (Bwc.Registry/RegistryValueType.cs) |  |
| MSG-231 | pass | String parity check: text equal (Bwc.Registry/RegistryValueType.cs) |  |
| MSG-232 | pass | String parity check: text equal (Bwc.Registry/RegistryValueType.cs) |  |
| MSG-233 | pass | String parity check: text equal (Bwc.Registry/RegistryValueType.cs) |  |
| MSG-234 | pass | String parity check: text equal (Bwc.Regedit/Registry/RegistryValueSource.cs) |  |
| MSG-235 | pass | String parity check: text equal (PrintKeyName) |  |
| MSG-236 | pass | String parity check: text equal (PrintClassName) |  |
| MSG-237 | pass | String parity check: text equal (PrintLastWriteTime) |  |
| MSG-238 | pass | String parity check: text equal (PrintValueHeading) |  |
| MSG-239 | pass | String parity check: text equal (PrintValueName) |  |
| MSG-240 | pass | String parity check: text equal (PrintValueType) |  |
| MSG-241 | pass | String parity check: text equal (PrintValueDataSize) |  |
| MSG-242 | pass | String parity check: text equal (PrintValueData) |  |
| MSG-243 | pass | String parity check: text equal (PrintNoName) |  |
| MSG-244 | pass | String parity check: text equal (PrintNoClass) |  |
| MSG-245 | pass | Classic text golden has "Full Resource Descriptor" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-246 | pass | Classic text golden has "Partial Descriptor" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-247 | pass | Classic text golden has "Interface Type:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-248 | pass | Classic text golden has "Bus Number:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-249 | pass | Classic text golden has "Version:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-250 | pass | Classic text golden has "Revision:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-251 | pass | Classic text golden has "Resource:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-252 | pass | Classic text golden has "Disposition:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-253 | pass | Classic text golden has "Type:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-254 | pass | Classic text golden has "Start:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-255 | pass | Classic text golden has "Length:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-256 | pass | Classic text golden has "Level:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-257 | pass | Classic text golden has "Vector:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-258 | pass | Classic text golden has "Affinity:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-259 | pass | Label is RT_STRING 8168 with its padding. ExportDialogs_ResourceLabelsWriteClassicTextLabels (resourceexport.dma.labels). Layout extrapolated, not measured (export.md 2.1) |  |
| MSG-260 | pass | Label is RT_STRING 8169 with its padding. ExportDialogs_ResourceLabelsWriteClassicTextLabels (resourceexport.dma.labels). Layout extrapolated, not measured (export.md 2.1) |  |
| MSG-261 | pass | Label is RT_STRING 8170 with its padding. ExportDialogs_ResourceLabelsWriteClassicTextLabels (resourceexport.device.labels). Layout extrapolated (export.md 2.1) |  |
| MSG-262 | pass | Label is RT_STRING 8171 with its padding. ExportDialogs_ResourceLabelsWriteClassicTextLabels (resourceexport.device.labels). Layout extrapolated (export.md 2.1) |  |
| MSG-263 | pass | Label is RT_STRING 8172 "Data:" (unpadded), bytes after one space. ExportDialogs_ResourceLabelsWriteClassicTextLabels (resourceexport.device.labels). Layout extrapolated (export.md 2.1) |  |
| MSG-264 | pass | Classic text golden has "Interface Type:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-265 | pass | Classic text golden has "Bus Number:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-266 | pass | Classic text golden has "Slot Number:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-267 | pass | Classic text golden has "List" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-268 | pass | Classic text golden has "Descriptor" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-269 | pass | Classic text golden has "Option:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-270 | pass | Classic text golden has "Alignment:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-271 | pass | Classic text golden has "Minimum Address:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-272 | pass | Classic text golden has "Maximum Address:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-273 | pass | Classic text golden has "Minimum Vector:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-274 | pass | Classic text golden has "Maximum Vector:" (SeedExportMatchesTheClassicTextGoldenByteForByte); print uses the export text (Parity_PrintReport_UsesClassicLabels) |  |
| MSG-275 | pass | Label is RT_STRING 8191 with its padding. ExportDialogs_ResourceLabelsWriteClassicTextLabels (resourceexport.requirement.dma). Layout extrapolated (export.md 2.1) |  |
| MSG-276 | pass | Label is RT_STRING 8192 with its padding. ExportDialogs_ResourceLabelsWriteClassicTextLabels (resourceexport.requirement.dma). Layout extrapolated (export.md 2.1) |  |

### VIEW

| ID | Status | Evidence or reason | Piece |
| --- | --- | --- | --- |
| VIEW-001 | pass | Informational row; the 96 DPI basis is applied by Shell_ClassicMetricsMatchOracleAtWindowScale (100 percent capture still owed, OQ-29) |  |
| VIEW-002 | pass | Shell_ClassicMetricsMatchOracleAtWindowScale; Shell_LaunchShowsClassicStructure |  |
| VIEW-003 | pass | Menus_BarRectsMatchOracleAtWindowScale |  |
| VIEW-004 | pass | Shell_ClassicMetricsMatchOracleAtWindowScale |  |
| VIEW-005 | pass | Shell_EnterNavigatesAndEscapeRestoresPath; AddressBar_NavigatesParsesAndRejectsPaths |  |
| VIEW-006 | pass | Shell_ClassicMetricsMatchOracleAtWindowScale |  |
| VIEW-007 | pass | Icons_TreeAndListRowsMatchClassicLayout; Icons_ClassicRegeditCaptures |  |
| VIEW-008 | pass | Icons_TreeAndListRowsMatchClassicLayout; Icons_ClassicRegeditCaptures |  |
| VIEW-009 | pass | Navigation_TreeSiblingOrderMatchesClassic against docs/parity/oracle/tree-order.json (oracle capture tree-order). The tree sorts with RegistryNameOrder (CompareStringEx, NORM_IGNORECASE) |  |
| VIEW-010 | pass | Theme_SystemPalette_FollowsWindowsAppMode; regedit-selection-colors.json |  |
| VIEW-011 | declared-difference | WinUI controls replace SysTreeView32 and SysListView32; UI Automation exposes Tree, DataGrid and Header patterns instead (X1 asserts them) |  |
| VIEW-012 | pass | Shell_ClassicMetricsMatchOracleAtWindowScale; Icons_TreeAndListRowsMatchClassicLayout |  |
| VIEW-013 | pass | Shell_ClassicMetricsMatchOracleAtWindowScale; Icons_TreeAndListRowsMatchClassicLayout |  |
| VIEW-014 | pass | List_OrderTypeAheadAndHeaderClick; sort-order.json |  |
| VIEW-015 | pass | ValueDisplayTests: every type compared row by row with classic (artifacts/e2e/value-display/classic-compare-*.json). REG_LINK is measured once only: the sandbox write policy refuses link values, so no repeatable seed exists |  |
| VIEW-016 | pass | ValueDisplayTests: every type compared row by row with classic (artifacts/e2e/value-display/classic-compare-*.json). REG_LINK is measured once only: the sandbox write policy refuses link values, so no repeatable seed exists |  |
| VIEW-017 | pass | Theme_E2EOnly_LightOverride_UsesSystemColorTable; regedit-selection-colors.json |  |
| VIEW-018 | pass | Shell_LaunchShowsClassicStructure |  |
| VIEW-019 | pass | SmallDialogs_RectsMatchClassicMeasurements: Add to Favorites, Remove Favorite and Find controls within 4 px of regedit-dialog-rects.json at 192 DPI |  |
| VIEW-020 | pass | Informational row (capture environment); test-all --rid all also runs the x64 build |  |
| VIEW-021 | declared-difference | View > Theme is a user-requested addition (AppServices_ThemeSubmenuSwitchesPaletteAndPersists) |  |

### BEH

| ID | Status | Evidence or reason | Piece |
| --- | --- | --- | --- |
| BEH-001 | pass | Favorites_AddRemoveNavigateAndReportMissingPath |  |
| BEH-002 | declared-difference | BWC keeps LastKey, Find flags, placement, splitter and column widths in its own settings so it never writes classic's Applets key except Favorites; it reads classic LastKey on first start (Navigation_ShowsRootsAndRestoresLastKey; AppServices_PlacementSplitAndColumnsPersistAcrossRestart) |  |
| BEH-003 | declared-difference | BWC keeps LastKey, Find flags, placement, splitter and column widths in its own settings so it never writes classic's Applets key except Favorites; it reads classic LastKey on first start (Navigation_ShowsRootsAndRestoresLastKey; AppServices_PlacementSplitAndColumnsPersistAcrossRestart) |  |
| BEH-004 | declared-difference | BWC keeps LastKey, Find flags, placement, splitter and column widths in its own settings so it never writes classic's Applets key except Favorites; it reads classic LastKey on first start (Navigation_ShowsRootsAndRestoresLastKey; AppServices_PlacementSplitAndColumnsPersistAcrossRestart) |  |
| BEH-005 | declared-difference | BWC keeps LastKey, Find flags, placement, splitter and column widths in its own settings so it never writes classic's Applets key except Favorites; it reads classic LastKey on first start (Navigation_ShowsRootsAndRestoresLastKey; AppServices_PlacementSplitAndColumnsPersistAcrossRestart) |  |
| BEH-006 | declared-difference | BWC keeps LastKey, Find flags, placement, splitter and column widths in its own settings so it never writes classic's Applets key except Favorites; it reads classic LastKey on first start (Navigation_ShowsRootsAndRestoresLastKey; AppServices_PlacementSplitAndColumnsPersistAcrossRestart) |  |
| BEH-007 | pass | Navigation_RefreshShowsValueAddedOutside (value written outside is absent before F5, listed after F5, run key still selected) |  |
| BEH-008 | pass | Shell_MenuTableMatchesCommandStates (no Undo item) |  |
| BEH-009 | pass | Editing_DragAndDropDoesNothing (key onto key, value onto key and key onto the list; registry read after each drag) |  |
| BEH-010 | pass | Tree_RenameCommitsOnEnterAndFocusLossAndCancelsOnEscape; Keyboard_SlowSecondClickStartsRename; Keyboard_DoubleClickOnSelectedValueOpensEditorWithoutRename; Keyboard_DoubleClickOnSelectedKeyTogglesWithoutRename; Keyboard_ClickOnSelectedListRowFromTreeFocusDoesNotRename |  |
| BEH-011 | pass | Editing_CreatesRenamesAndDeletesRegistryObjects; Bwc.Registry.Tests (rename) |  |
| BEH-012 | pass | Editing_CreatesRenamesAndDeletesRegistryObjects; Bwc.Registry.Tests (rename) |  |
| BEH-013 | pass | Messages_ClassicCaptionsTextsAndButtonsAreShown; Basics_MultiDeleteTextAndDefaultRow |  |
| BEH-014 | pass | AddressBar_NavigatesParsesAndRejectsPaths |  |
| BEH-015 | pass | AppServices_SecondLaunchActivatesFirstWindowAndMultipleSwitchOpensAnother |  |
| BEH-016 | pass | CommandLine_ExportsImportsAndRejectsOutsideSandbox; CommandLine_SilentImportExitCodesFollowTheResult |  |
| BEH-017 | pass | AppServices_RegFileArgumentAsksBeforeImport_AnsweredNo; AppServices_RegFileArgumentAsksBeforeImport_AnsweredYes |  |
| BEH-018 | pass | AppServices_RegFileArgumentAsksBeforeImport_AnsweredNo; AppServices_RegFileArgumentAsksBeforeImport_AnsweredYes |  |
| BEH-019 | pass | RegFileReaderTests; RegistryExportTests; RegistryExportHiveTests; ExportDialogs_RangePanelWritesAllBranchTextAndRegedit4 |  |
| BEH-020 | pass | RegFileReaderTests; RegistryExportTests; RegistryExportHiveTests; ExportDialogs_RangePanelWritesAllBranchTextAndRegedit4 |  |
| BEH-021 | pass | ExportDialogs_BranchPathEditRefusesMissingKey (RT_STRING 148 in a box captioned Export Registry File, as measured in regedit-export-range.json; the dialog stays open) |  |
| BEH-022 | pass | AppServices_HiveRulesEnableLoadAndUnloadByRoot |  |
| BEH-023 | pass | Remote_LoopbackConnectShowsRemoteRootsAndMenus (only HKEY_LOCAL_MACHINE and HKEY_USERS); Remote_DisconnectDialogRemovesComputer; every command on remote keys: Remote_KeyAndValueCommandsWriteOnlyUnderTheSandbox, Remote_FindLocatesRemoteValue, Remote_ExportBranchMatchesLocalExport, Remote_AddressBarNavigatesToRemotePath |  |
| BEH-024 | pass | Remote_LoopbackConnectShowsRemoteRootsAndMenus (only HKEY_LOCAL_MACHINE and HKEY_USERS); Remote_DisconnectDialogRemovesComputer; every command on remote keys: Remote_KeyAndValueCommandsWriteOnlyUnderTheSandbox, Remote_FindLocatesRemoteValue, Remote_ExportBranchMatchesLocalExport, Remote_AddressBarNavigatesToRemotePath |  |
| BEH-025 | pass | Remote_ConnectOwnComputerShowsStringOneFiftyThree; Remote_ConnectUnreachableShowsStringOneFiftyFour; 155 and 156 are reviewed in code, not automated (no way to reach half a computer from this machine) |  |
| BEH-026 | pass | Permissions_EffectiveAccessShowsResultsForTheCurrentUser: Effective Access tab, user picked with Select a user, user named in the User/ Group field, at least one Allowed row. Owner recursion and the audit tab: Permissions_OwnerRecursionSetsTheOwnerOfEverySubkey, Permissions_AuditingTabWithoutSecurityPrivilegeShowsTheSystemState |  |
| BEH-027 | declared-difference | Range panel: declared (DLG-016). Progress: pass (DLG-009). Report labels: pass (MSG-245 to MSG-276). Printed page layout (margins, font, wrapping) has no classic print output golden (OQ-44); regedit-print.json records no printed output and no PDF export, so the layout is BWC's own and is not compared |  |
| BEH-028 | pass | Tree_RightAndLeftArrowSemantics; Tree_NumpadKeysAndPageKeys |  |
| BEH-029 | pass | Basics_MultiDeleteTextAndDefaultRow; List_KeyboardSelectionAndFocus |  |
| BEH-030 | pass | Shell_TabCyclesTreeListAddressBar; List_KeyboardSelectionAndFocus |  |
| BEH-031 | pass | ContextMenus_OpenAndInvokeCommands; List_MouseSelectionAndEmptyArea |  |
| BEH-032 | pass | Robustness_ExpandedKeyMenuShowsCollapse |  |
| BEH-033 | pass | Parity_CtrlT_TogglesBetweenLocalMachineAndCurrentUser |  |
| BEH-034 | pass | RegistryLinkFixture; RegistryReaderTests |  |
| BEH-035 | pass | RegistryLinkFixture; RegistryReaderTests |  |
| BEH-036 | pass | Robustness_ExpandOfDeniedKeyShowsClassicError; AppServices_ValueEnumerationFailureShowsClassicError |  |
| BEH-037 | pass | Limits pass through from the platform (Bwc.Registry.Tests). The list uses native CompareStringW like classic (List_OrderTypeAheadAndHeaderClick, sort-order.json), not ordinal. Classic does not list a 16,383-character name (note in ValueDisplayTests) |  |
| BEH-038 | pass | LiteralDefaultRowTests; DefaultRowCommandTests; LargeValueSortTests |  |
| BEH-039 | pass | Find_FollowsTheClassicTwoDialogSequence; Find_FinishedIsStickyUntilTheSelectionMoves; RegistryFinderTests |  |
| BEH-040 | pass | Find_FollowsTheClassicTwoDialogSequence; Find_FinishedIsStickyUntilTheSelectionMoves; RegistryFinderTests |  |
| BEH-041 | pass | Find_FollowsTheClassicTwoDialogSequence; Find_FinishedIsStickyUntilTheSelectionMoves; RegistryFinderTests |  |
| BEH-042 | pass | Find_FollowsTheClassicTwoDialogSequence; Find_FinishedIsStickyUntilTheSelectionMoves; RegistryFinderTests |  |
| BEH-043 | pass | scripts/flavor-metadata.js checkExeManifests: the published BwcRegedit.exe requestedExecutionLevel must match src/Bwc.Regedit.App/app.manifest (highestAvailable, as classic in regedit-manifest.json). Run by scripts/dotnet-build.js publish, which fails on a problem, and by the test-all flavor step |  |
| BEH-044 | pass | Hives_LoadHiveRefusesWithoutPrivilegeBeforeAskingForFile |  |
| BEH-045 | pass | Robustness_PolicyOverrideShowsRegistryEditingDisabled; Robustness_PolicyLevelTwoRefusesSilentImport |  |
| BEH-046 | pass | Basics_EnterAndDoubleClickOpenTheEditorForEachType |  |
| BEH-047 | pass | RegistryValueCodecTests; Robustness_InvalidLengthDwordOpensBinaryEditor |  |
| BEH-048 | pass | Navigation_ShowsRootsAndRestoresLastKey |  |
| BEH-049 | pass | BinaryViewer_FollowsTheClassicLayoutAndFormats; classic opens variant B for every type (regedit-binary.json) |  |

### EXP

| ID | Status | Evidence or reason | Piece |
| --- | --- | --- | --- |
| EXP-001 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-002 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-003 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-004 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-005 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-006 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-007 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-008 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-009 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-010 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-011 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-012 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-013 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-014 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-015 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-016 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-017 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-018 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-019 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-020 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-021 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-022 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-023 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-024 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-025 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-026 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-027 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-028 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-029 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-030 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-031 | pass | RegistryExportTests (byte-for-byte against the three goldens); RegFileReaderTests (round trip). EXP-004, EXP-010 and EXP-011 match the golden; their lossy round trip is classic behavior too |  |
| EXP-032 | pass | Informational row; no print golden exists (BEH-027, OQ-44) |  |

## Open questions

Answers recorded by this audit. "O1" means the oracle piece measures it on classic regedit; the row stays
open here until that measurement is in `docs/parity/oracle/`.

| OQ | Answer or owner |
| --- | --- |
| OQ-01 | Dialog 107 is Disconnect Network Registry. Dialog 115 (caption Import Registry File) picks the target computer for an import while remote computers are connected; strings 131 and 135 name that computer. The classic connect could not be automated (OQ-45), so this follows the resources; BWC is tested over a loopback connection (Remote_DisconnectDialogRemovesComputer, Remote_ImportWritesThroughTheConnection). |
| OQ-02 | RT_MENU 108 is the context menu of the hex area in Edit Binary Value. The address bar, Find what and Edit String data boxes show the system edit menu (regedit-context.json). Built by K1. |
| OQ-03 | The Computer root shows RT_MENU 107. Hive and key menus show RT_MENU 104 as measured. The value menu (105) is the same for every type (regedit-context.json). Remote menus: not measured (the connect could not be automated); R2 follows the resources. |
| OQ-04 | 640 and 641 are Tab and Shift+Tab (Shell_TabCyclesTreeListAddressBar, Shell_ShiftTabReversesCycle). 672 focuses the address bar (Shell_CtrlLAndAltDFocusAddressBar). 642 (F4) focuses the address bar and opens its suggestions; 698 (Alt+Up) selects the parent key; 896 (Shift+F10) opens the context menu; 689 to 697 (Alt+1 to Alt+9) change nothing (regedit-keys.json). |
| OQ-05 | Ctrl+T toggles between HKEY_LOCAL_MACHINE and HKEY_CURRENT_USER, and the menu item text follows (Parity_CtrlT_TogglesBetweenLocalMachineAndCurrentUser). |
| OQ-06 | Expand reads Collapse while the key is expanded (Robustness_ExpandedKeyMenuShowsCollapse). |
| OQ-07 | Classic shows the shell About box. BWC shows its own About box with product version and copyright (AppServices_AboutShowsProductVersionAndCopyright); declared, because BWC must not claim to be Windows. |
| OQ-08 | Favorites list order, text and click behavior: Favorites_AddRemoveNavigateAndReportMissingPath, FavoritesFixes_DefaultValueWhitespaceAndCulture. |
| OQ-09 | No status bar in this build, so menu help strings are never shown; declared difference on those MSG rows. |
| OQ-10 | Find_FollowsTheClassicTwoDialogSequence and find-sequence.json. |
| OQ-11 | Settled before this audit (RegistryFinderTests). |
| OQ-12 | Hexadecimal is the default base (ClassicDialogs_EditStringAndDwordFollowTheTemplate). Overflow prompts: F2. |
| OQ-13 | ClassicValueEditors_MultiStringWarningAndBinaryFollowTheTemplates. |
| OQ-14 | Classic opens variant B (2021) for every type, Byte format by default; variant A (2020) is never opened (regedit-binary.json). BWC matches (BinaryViewer_FollowsTheClassicLayoutAndFormats). |
| OQ-15 | Export branch edit 1282: enabled for both radios and filled with the selected key (regedit-export-range.json). Runtime enable rules of template-disabled viewer controls: measured on classic with a resource value from HKEY_LOCAL_MACHINE\HARDWARE (readable unelevated); D1. |
| OQ-16 | Enter on REG_RESOURCE_LIST opens Resource Lists (116), on REG_FULL_RESOURCE_DESCRIPTOR opens Resources (1305), on REG_RESOURCE_REQUIREMENTS_LIST opens Requirements List (1501); Modify Binary Data shows the hex editor (regedit-resource-viewers.json; the requirements list is from the templates, no sample here). |
| OQ-17 | Not reachable in classic; not implemented (DLG-044, DLG-045 declared). |
| OQ-18 | ImportProgress_ShowsClassicProgressWithoutCancel; ImportResult tests. |
| OQ-19 | BWC shows its own export progress with Cancel and deletes the partial file (ExportProgress_CancelDeletesThePartialFileAndWritesNothing); declared, because a long export must be cancellable. |
| OQ-20 | The privilege check comes before the file picker (Hives_LoadHiveRefusesWithoutPrivilegeBeforeAskingForFile). The elevated path needs the elevated runner and is not measured here. |
| OQ-21 | Unload is enabled only on a direct child of HKLM or HKU (AppServices_HiveRulesEnableLoadAndUnloadByRoot). |
| OQ-22 | Strings 55 and 56 are present; the binary hive import flow is checked in F2. |
| OQ-23 | DisableRegistryTools 1 and 2 (Robustness_PolicyOverrideShowsRegistryEditingDisabled, Robustness_PolicyLevelTwoRefusesSilentImport). |
| OQ-24 | BWC ships highestAvailable (src/Bwc.Regedit.App/app.manifest). Classic asks for highestAvailable too (regedit-manifest.json). The guard is scripts/flavor-metadata.js (checkExeManifests), run by the publish and the test-all flavor step (BEH-043). |
| OQ-25 | Value order: List_OrderTypeAheadAndHeaderClick. Tree order for non-ASCII and symbol names: F2. Export order follows the golden (RegistryExportTests). |
| OQ-26 | sort-order.json and List_OrderTypeAheadAndHeaderClick; non-ASCII value names: V1. |
| OQ-27 | Selection colors: regedit-selection-colors.json (Theme tests). Icon sizes: Icons_TreeAndListRowsMatchClassicLayout. |
| OQ-28 | SmallDialogs_RectsMatchClassicMeasurements compares Add to Favorites, Remove Favorite and Find with regedit-dialog-rects.json (4 px at 192 DPI). The file dialogs are system dialogs and are not compared. |
| OQ-29 | Not measured: a 100 percent scale capture needs a session at that scale on a real desktop. Owed to a later release unless the user runs one. |
| OQ-30 | `test-all --rid all` runs the x64 build (emulated on this Arm64 machine). A real x64 desktop run is owed. |
| OQ-31 | The writer matches the golden (truncates at the first NUL), as classic does. |
| OQ-32 | The lossy round trip is classic behavior; export bytes match the goldens. |
| OQ-33 | The golden governs (78 characters); RegistryExportTests. |
| OQ-34 | Text export: ExportDialogs_RangePanelWritesAllBranchTextAndRegedit4. Hive export: RegistryExportHiveTests. Exit codes: CommandLine_SilentImportExitCodesFollowTheResult. Print report: the export text, so it matches the classic text golden (SeedExportMatchesTheClassicTextGoldenByteForByte) and Parity_PrintReport_UsesClassicLabels. Print page layout: OQ-44. |
| OQ-35 | See OQ-22 (F2). |
| OQ-36 | Basics_EnterAndDoubleClickOpenTheEditorForEachType; Tree_DoubleClickTogglesExpansion. |
| OQ-37 | Editing_CreatesRenamesAndDeletesRegistryObjects (new items enter rename with the name selected). |
| OQ-38 | AddressBar_NavigatesParsesAndRejectsPaths. Remote paths (\\COMPUTER\HKEY_...) both ways: Remote_AddressBarNavigatesToRemotePath. |
| OQ-39 | Rename and Delete are off at Computer and the hive roots (Shell_MenuTableMatchesCommandStates); the Computer-root context menu: O1. |
| OQ-40 | String 34 is not shown; the root is Computer (string 19). |
| OQ-41 | Decision: BWC does not register a .reg file type or Merge verb. |
| OQ-42 | BWC uses the OS ACL editor (Permissions tests). Owner recursion and the audit tab: Permissions_OwnerRecursionSetsTheOwnerOfEverySubkey, Permissions_AuditingTabWithoutSecurityPrivilegeShowsTheSystemState. Remote apply: Permissions_RemoteKeyApplyChangesTheDaclThroughTheConnection. Effective access: Permissions_EffectiveAccessShowsResultsForTheCurrentUser. Prompts 7013, 7015, 7016 and 7018 are declared (permissions.md section 43); 7014 is open (MSG-175). |
| OQ-43 | Favorites_AddRemoveNavigateAndReportMissingPath (duplicate name refused, no confirmation on Remove). |
| OQ-44 | Classic opens the Windows print host, whose default view has no range controls, and the default printer is a physical printer, so nothing was printed (regedit-print.json). BWC uses PrintDlgExW with no range panel (DLG-015 and DLG-016 declared). Progress window with Cancel: DLG-009. Report text: the classic text golden (MSG-245 to MSG-258, MSG-264 to MSG-274). Printed page layout: declared difference (BEH-027). The DMA and device-specific labels (MSG-259 to MSG-263, MSG-275, MSG-276) are checked by ExportDialogs_ResourceLabelsWriteClassicTextLabels. |
| OQ-45 | Classic opens the system object picker (RichEdit name box); typing into it could not be automated, so the connect is not measured (regedit-remote.json). BWC opens the same system picker (Remote_ConnectDialogIsOwnedAndCancelLeavesNoConnection) and falls back to a name prompt when the picker cannot run. |
| OQ-46 | CommandLine_SilentImportExitCodesFollowTheResult; CommandLine_ExportsImportsAndRejectsOutsideSandbox. |
| OQ-47 | The name box is read-only in every value editor (ClassicDialogs_EditStringAndDwordFollowTheTemplate). |
| OQ-48 | Navigation_ShowsRootsAndRestoresLastKey; Basics_CopyKeyNameAndF5KeepTheView. |
| OQ-49 | Favorites use classic's key and format (BEH-001). The other state is kept in BWC settings (BEH-002 to BEH-006 declared). |
