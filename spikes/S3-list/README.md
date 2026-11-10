# S3 list spike: classic-density details list in WinUI 3

Question: can a custom virtualized details list (header row, resizable and sortable columns, selection) hit classic
regedit row density (17-20 px rows, 12 px Segoe UI) and stay smooth with 200,000 rows?

Answer on this machine: yes for timing and row height. Not at the density the brief asked for, because this desktop runs at
200% scale, not 100% (see DPI notes). A physical-equivalent run shows that 18 px rows and 12 px text do render at 200% with
9 DIP rows and a 6 DIP font.

## Layout

- `Bwc.Spike.List/` WinUI 3 exe (net10.0-windows10.0.26100.0, WinAppSDK 2.6.5, unpackaged, PerMonitorV2).
  - `Controls/DetailsList.*`: header + virtualized body, selection, keyboard, sort, resize, auto-size.
  - `Controls/RowPanel.cs`: one ItemsRepeater element per row. Explicit `Height` = row height, no padding, no min-height.
  - `Controls/HeaderPanel.cs`: click targets, titles, sort arrow, divider, 8 px drag grip per column.
  - `Model/ColumnModel.cs`: shared width model (left edges, total width, `WidthChanged`).
  - `Model/SampleData.cs`: deterministic values, names `Value000001...`, types cycle REG_SZ / REG_DWORD / REG_BINARY / REG_MULTI_SZ.
  - `Bench/BenchRunner.cs`, `Bench/FrameRecorder.cs`: scripted run, frame sampling, JSON report.
- `bench.ps1`: builds win-arm64 Release (fails on any warning), runs every configuration under `Local\BwcGuiTestLock`,
  writes `out/bench-*.json` and `out/shot-*.png`. `-IncludePhysicalEquivalent` adds the 9 DIP run.
- `out/` (git-ignored): JSON reports and PNG screenshots.

## Approach and why

- **ItemsRepeater + StackLayout in a ScrollViewer, not ListView.** Each row is one element with an exact height and no
  container chrome, so the 32 px `ListViewItem` minimum is not in play. ItemsRepeater met every criterion, so no ListView
  comparison was needed.
- **Column geometry from a shared model.** `RowPanel` and `HeaderPanel` place cells in `ArrangeOverride` from
  `ColumnModel`. A width change updates the header width, invalidates realized rows and re-arranges them. It does not change
  `ItemsSource`, so the bench measures element prepares during resizes (see numbers).
- **Horizontal scroll.** The body ScrollViewer scrolls both ways. The header is clipped and translated by
  `-HorizontalOffset` on `ViewChanged`.
- **Sort off the UI thread.** A 200k sort is 80-140 ms of compute (measured below). `SortAsync` sorts a copy on the
  thread pool and applies it on the UI thread (ItemsSource reset plus layout, 4-8 ms in the measured runs). `RegValue` is
  immutable, so this is safe.
- **Selection:** `HashSet<int>` of indices. Single and extended modes; click, Ctrl+click, Shift+click, Up/Down,
  Home/End, PgUp/PgDn, Ctrl+A.
- **Text:** 12 DIP Segoe UI, `NoWrap`, `CharacterEllipsis`, `BlockLineHeight` with line height = 4/3 x font size.

## Numbers (200,000 rows, host calm: 9-19% CPU, final series)

All values in DIP. At this machine's 200% scale, 1 DIP = 2 physical px (`dpi` 192 in the JSON).

| run | row / header / font (DIP) | measured row / header | maxFrameMs | p99 | p95 | frames >100 ms | idle p95 / max (no list work) | scroll layout median / max | sortMs (desc / asc) | sort bg (max of desc/asc) | sort UI-thread (comparison) | resizeMsMax | selectAllMs | startupMs | privateMB |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| rh17 | 17 / 22 / 12 | 17 / 22 | 19.4 | 13.0 | 10.8 | 0 | 8.7 / 13.3 | 3.7 / 8.9 | 141.6 / 121.9 | 136.8 | 86.2 | 4.3 | 3.8 | 462 | 124 |
| rh18 | 18 / 22 / 12 | 18 / 22 | 19.3 | 12.1 | 9.9 | 0 | 8.8 / 11.3 | 3.5 / 9.1 | 142.2 / 110.3 | 137.4 | 137.8 | 5.1 | 4.0 | 401 | 124 |
| rh20 | 20 / 22 / 12 | 20 / 22 | 19.2 | 10.7 | 9.2 | 0 | 9.1 / 14.2 | 3.0 / 7.6 | 149.7 / 65.8 | 145.3 | 79.0 | 2.9 | 3.4 | 400 | 122 |
| equiv 9 DIP | 9 / 11 / 6 (icon 8) | 9 / 11 | 23.2 | 17.0 | 13.5 | 0 | 9.2 / 11.5 | 6.1 / 13.1 | 152.7 / 116.0 | 144.6 | 89.2 | 8.2 | 3.7 | 425 | 127 |

Other fields from the same runs:
- Realized rows at end: 98 / 94 / 84 / 188. Element prepares during the scroll phase: 26.6k / 25.2k / 22.9k / 49.4k.
- Element prepares across the 20 resizes: 58 / 56 / 50 / 114 (2.5-6 per resize, not every row).
- One gen2 GC per run inside the recorded window, pause 17-31 ms.
- Startup (process start to first frame after load, 200k rows generated): 400-460 ms in the calm series.

Definitions: `maxFrameMs`, `p99FrameMs`, `p95FrameMs` are CompositionTarget.Rendering deltas across the recorded window
(scroll, sorts, resizes, auto-size, select-all). `idle*` is 59 frames with nothing running. `scrollLayout*` is the
synchronous cost of one scroll step including `UpdateLayout`. `sortMs` is the larger of desc and asc, each measured as
background sort + UI apply + layout. `resizeMsMax` is the largest of 20 resizes of +10 DIP, each measured as operation +
layout. `selectAllMs` is `SelectAll()`, the same path Ctrl+A takes. `privateMB` is `PrivateMemorySize64` at the end.

### Pass / fail

- maxFrameMs <= 100 at 200,000 rows on win-arm64: **PASS** in every calm-host run (19-23 ms, 0 frames over 100 ms).
  **Not robust to host load**: an earlier series, run while other workers' compilers were active (VBCSCompiler and
  crossgen2 visible in the process list; host CPU sampled at 25-37% just after that series, not recorded per run), gave
  maxFrameMs 114 (rh17), 295 (rh18) and 203 (rh20), and idle frames stalled too (idle p95 up to 49 ms). That series used
  the same code as the final one except for the per-run host-CPU field, so the list cannot be blamed for it; the pass
  result is conditional on a quiet desktop.
- Measured row heights equal the requested height: **PASS**. 17, 18, 20 and 9 DIP exact; header 22 and 11 exact.
- Text not clipped: **PASS** by inspection of `out/shot-rh18.png` (18 DIP = 36 px) and `out/shot-equiv-9dip-6font.png`
  (9 DIP = 18 px, 6 DIP = 12 px). Descenders intact, ellipsis trims long REG_BINARY values.
- 0 build warnings: **PASS** (enforced by `bench.ps1`).
- Three JSON files: **PASS** (`out/bench-rh17.json`, `bench-rh18.json`, `bench-rh20.json`), plus `bench-equiv-9dip-6font.json`.

## DPI notes

- The brief said DPI 100%. This desktop is a 3600x2260 Parallels display at 200% (`GetDpiForMonitor` = 192,
  WinUI `RasterizationScale` = 2). All parameters are DIP, so the requested 17/18/20 px rows and 12 px font are
  **34/36/40 px and 24 px physically here**. The 17-20 run is therefore *not* classic density on this machine.
- Changing display scale to 100% is a system-wide setting shared with other workers, and the registry is off limits, so it
  was not changed. Re-running `bench.ps1` on a 100% display is the remaining check for the brief's exact target.
- Physical-equivalent run: 9 DIP rows, 6 DIP font, 11 DIP header, 8 DIP icon = 18 px rows, 12 px text, 22 px header,
  16 px icon at 200%. It renders and does not clip. Its frame cost is higher than the 17-20 runs (more rows fit), but
  maxFrameMs stays under 100 (23 ms in the calm series).
- The window is sized in DIP (960x600) and converted with the monitor scale.

## Blockers and gaps

- **Lower heights:** 9 DIP works here. 8 DIP (16 px) was not tried. The icon has to shrink with the row (`--icon-size`).
- **Real input not driven.** Click, Ctrl/Shift click, Up/Down, header-click sort and divider drag are implemented in code
  but the bench does not inject input. The bench calls the same model methods (`ResizeColumn`, `AutoSizeColumn`, `SortAsync`,
  `SelectAll`), so those paths are measured. The pointer and key routing is not.
- **Double-click divider auto-size** is wired to `DoubleTapped` but not exercised. `AutoSizeColumn` itself is measured.
- **Resizing is not zero re-prepares.** ItemsRepeater re-prepares about 2.5-6 elements per resize (not every row).
  Templates are not re-created wholesale, but this is not the zero-prepare ideal.
- **Sort cost is real.** A 200k sort is 80-140 ms of compute, which blocks one frame on the UI thread. The background path
  keeps frames under 100 ms, but the list is not instantly sorted.
- **Measurement hygiene:** frames are sampled per composition frame, not from an ETW trace. The bench forces a GC settle
  before recording. A gen2 still lands in the recorded window (17-31 ms). Idle baselines are reported so noise is visible.
- Private bytes are per process at the end, not peak.

## Running

```powershell
cd spikes\S3-list
.\bench.ps1                           # 200k rows at 17, 18, 20 DIP
.\bench.ps1 -IncludePhysicalEquivalent
```
