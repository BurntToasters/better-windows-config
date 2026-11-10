# S2: dense virtualized tree (Bwc.Spike.Tree)

Question: can a custom virtualized tree keep classic density (12 px Segoe UI, 16-20 px rows) and stay smooth with 200,000 visible rows, where WinUI's TreeView does not virtualize and has a 24 px compact row?

## Verdict

- **Row height: pass.** Realized rows measured exactly the requested height (16, 18, 20 DIP), with 0 layout mismatches in every bench.
- **Scrolling 200,002 visible rows: pass.** Final run: scroll-step p99 9.2 to 9.7 ms, max step 10 to 12 ms, frame p99 9.2 to 10.2 ms, 0 frames over 100 ms.
- **Expanding a 10,000-child node: marginal.** The final run passed (max frame 54 to 89 ms). Across 13 full-protocol runs of the same code, 4 had one frame over 100 ms (103, 112, 101, 156 ms), always the expand frame. On this 1-vCPU VM, the 100 ms criterion is not reliable for that one event.

## Approach

- **Host: `ItemsRepeater` + `StackLayout` inside a `ScrollViewer`.** The `DataTemplate` root `Grid` is the element, with `Height` set to the row height. There is no container wrapper, so there is no `ListViewItem` MinHeight or padding to strip.
- **Model: `FlatRowList : List<TreeRow>`.** It holds only the visible rows. Expand inserts the visible descendants as one range and raises one `CollectionChanged`. Collapse removes one range. Child lists are generated on first expand and cached, so a collapsed subtree keeps its expansion state. Collapsing a parent that holds the selection moves the selection to the parent.
- **Row:** indent spacer (depth x 19 px), 9 px +/- box with a 13 px hit area, 16x16 folder rectangle placeholder, 12 px Segoe UI label. Selection is a Background change on the row root.
- **Keyboard:** Up, Down, Home, End, PageUp, PageDown, Left (collapse or go to parent), Right (expand or go to first child), Enter, and type-ahead. The bench checks the key model programmatically (`keyboardOk`). Real key input was not injected, and type-ahead is untested.
- **Not measured:** a `ListView` with a stripped `ItemContainerStyle`. It was not run, so the choice rests on structure, not numbers. A `ListViewItem` is a per-row container with its own template and selection visuals, which would need restyling to reach 16 px.

## Numbers (final `bench.ps1` run, 200,000 keys, win-arm64 Release)

| Row (DIP) | maxFrameMs | p99FrameMs | p95FrameMs | framesOver100ms | measured row | expandMs | collapseMs | startupMs | privateMB |
|---|---|---|---|---|---|---|---|---|---|
| 16 | 89.3 | 9.2 | 9.0 | 0 | 16 | 70.9 | 2.1 | 356 | 74.1 |
| 18 | 54.8 | 9.2 | 8.8 | 0 | 18 | 43.8 | 1.7 | 300 | 73.6 |
| 20 | 54.2 | 10.2 | 8.8 | 0 | 20 | 43.6 | 1.7 | 242 | 73.1 |

Extra fields in the JSON: expandModelMs 35.1 / 23.0 / 23.4 (model plus synchronous host handler), expandGcPauseMs 1.8 / 1.1 / 1.2, layoutMismatches 0, keyboardOk true. Frame count 605 to 606 per run.

**Run-to-run spread** (same protocol, 300 steps each way, 18 px unless noted):

- First `bench.ps1` run: 103.3 (16 px), 111.8 (18 px), 88.5 (20 px). Each overrun was the expand frame.
- Four further 18 px runs: 64.6, 68.2, 60.6, 69.1 (range and count-only notify, mixed).
- Three more 18 px runs with GC instrumentation: 80.0, 155.6, 100.9.

Expand cost, from the instrumented runs: about 23 to 35 ms in our model and host handler, 20 to 36 ms in the layout pass, and 0 to 20 ms of GC pause. Notify variants: `--notify reset` was 120 ms against 59 ms for range at 2,000 rows. `--notify range-count-only` (placeholder payload, to test whether copying the rows is the cost) was not faster. Note that this VM has 1 logical processor, so other load shows up as spikes.

## What blocks lower row heights

- **The host does not impose a minimum.** The row root takes its exact `Height`. Measured heights were exact at 16 to 20 DIP. The 14 px probe was screenshot-only, so no layout check ran at that height.
- **The label is the limit.** Segoe UI 12 px has a line box of about 16 DIP (estimated from the font's ascent and descent, not measured), so below 16 px the `TextBlock` overflows its row. `ItemsRepeater` does not clip, so at 14 px (200% scale) the text still draws unclipped and legible. Not tested at 100% scale, where hinting differs, or at 12 to 13 px. The next step would be `LineStackingStrategy="BlockLineHeight"` with `LineHeight` set to the row height.
- **Icon and expander sizes are fixed** (16 px folder, 9 px box). Below about 14 px, the folder icon would have to shrink.

## DPI notes

- **This machine is at 200%, not 100%.** `RasterizationScale` is 2.0 (`dpi` 192 in the JSON). The physical display is 3600x2260 over 1800x1130 DIP. The brief says 100%. I did not change display settings (no registry access).
- **Logical heights hold at 200%.** Row heights are DIP values. The measured DIP heights equal the requested values, and the device height is 2x (18 DIP gives 36 px in the PNG).
- **Not tested** at 100% or 150%. The expected result at 150% is 1.5x device pixels with the same logical row height, but that is not measured.
- The app is PerMonitorV2 (`app.manifest`).

## Build and run

- `powershell -File spikes\S2-tree\bench.ps1`: publishes win-arm64 Release, then runs 16, 18 and 20 px under `Local\BwcGuiTestLock`. It writes `out\bench-rh16.json`, `out\bench-rh18.json`, `out\bench-rh20.json` and `out\screenshot-rh18.png`.
- Interactive: `Bwc.Spike.Tree.exe --rows 1000 --row-height 18`.
- Options: `--rows`, `--row-height`, `--font-size`, `--indent`, `--steps`, `--notify range|reset|range-count-only`, `--bench <json>`, `--screenshot <png>`.
- Startup errors are written to `bwc-s2-error.log` next to the exe. WinUI turns an exception that escapes a XAML callback into a native fail-fast (0xC000027B), so the log is the only way to see the message.
- Setup note: the first run crashed with 0xC000027B. The cause was a missing `XamlControlsResources` merge in `App.xaml`, which supplies the Fluent theme resources the row template uses.

## Definitions

- `startupMs`: process start to the first frame after the 200k-row tree loads. It includes runtime and WinUI startup.
- `maxFrameMs`, `p99FrameMs`, `p95FrameMs`: percentiles of `CompositionTarget.Rendering` `RenderingTime` deltas, recorded only during the scripted section (scrolls, jump, expand and collapse).
- `expandMs` and `collapseMs`: time for the model change plus `UpdateLayout`.
- `privateMB`: `PrivateMemorySize64` after the bench.
