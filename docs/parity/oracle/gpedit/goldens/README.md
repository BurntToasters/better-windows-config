# gpedit goldens

What classic `gpedit.msc` writes when it saves, for the fixture templates in `../fixture`. Captured by `Bwc.Oracle.Capture gpedit matrix`
(plan and failure modes: `docs/failure-modes/oracle-gpedit-matrix.md`). The policy engine tests compare against these files byte for byte.

## Layout

`<group>/<case>/` for groups A to H. Group letters:

| Group | Holds |
| --- | --- |
| A | first saves into an empty GroupPolicy folder |
| B | every fixture policy: Enabled defaults, alternate values per element kind, Disabled, Not Configured |
| C | sequences: order, re-apply, comments, clearing, version carry, shared lists, comment namespaces |
| D | crafted Registry.pol and gpt.ini start states, then one unrelated save |
| E | validation message boxes (what is written, or not written, after a bad value) |
| F | visuals: tree order and icons, columns, status text, sort, filter, menus, toolbar, dialog rectangles, keys |
| G | refresh timing of the live registry after OK, Apply and Not Configured |
| H | template load errors (each bad fixture next to the good one) |

Inside a case folder:

- `Machine.Registry.pol`, `User.Registry.pol`, `Machine.comment.cmtx`, `User.comment.cmtx`, `gpt.ini`: the bytes after the case's last step
  (a file that does not exist is not written).
- `start/`: the files the case placed before classic started (only for crafted starts).
- `step-NN-<label>/`: the same five files after each recorded step, for cases with more than one step.
- `case.json`: the plan hash, the operations, the steps (size and SHA-256 of every file, gpt.ini version) and what the driver observed.
- `complete.json`: written last; a folder without it is not a golden. It holds the plan hash that `--resume` compares.

## Privacy

Nothing from the machine's own policy ever enters a golden: before every case GroupPolicy is emptied (and the start files written), the MMC state
and the BWC-Oracle registry keys are deleted, and the session restores the machine afterwards. Text that classic shows and that is not in the
fixture is stored as `#` plus the first 12 hex digits of its SHA-256, never as text. Screenshots and raw logs stay under
`artifacts/oracle/gpedit/matrix/<group>/<case>/` (not tracked).

## Run

From an administrator terminal on the VM desktop, after a VM snapshot, with nothing else using the keyboard or mouse:

```
Bwc.Oracle.Capture.exe gpedit matrix
```

- `--list` prints every case and the estimated time per group; `--dry-run` prints the counts and the preflight. Both work unelevated and change nothing.
- `--only A,B` and `--case B/Pol_Decimal_MinMax` select part of the matrix.
- `--resume` skips a case whose folder is complete and was captured with the same plan.
- A case that fails keeps its earlier golden. After 3 failures in a row the run stops; fix the cause and run again with `--resume`.
- The end of the run prints `done, skipped, failed, changed`. `changed` counts cases whose bytes differ from the golden that was there before.

To check that a capture is repeatable, run the whole matrix twice and compare: `git status` and `git diff --stat -- docs/parity/oracle/gpedit/goldens`
must show no change after the second run. Any `CHANGED` line in the run output names the case and files that differ.

After a run the session has already restored GroupPolicy, the MMC state and the registry keys. If the machine looks wrong anyway, revert the VM
snapshot, or run `gpupdate /force` in an administrator terminal.
