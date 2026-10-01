# Architecture and standing contracts

This is the short list of things that are **not** visible from reading a single file: units, threading,
ownership and the platform workarounds whose absence is not obvious until something misbehaves. When a
comment in the code says "see docs/ARCHITECTURE.md", it means the rule below.

Anything measured on a machine is marked with the measurement, because several of these contracts exist
only because a plausible-looking assumption turned out to be false.

---

## 1. Window geometry

**`FormSize` is the OUTER window size, in effective pixels (epx).** Not the client area, not physical
pixels. `AppWindow.Size`/`Resize` deal in physical pixels of the outer window, so the two conversions
live in exactly two places: `MainWindow.SaveWindowSize` (divide by the rasterization scale) and
`MainWindow.ApplyInitialSize` (multiply).

* Why it matters: saving `Nav.ActualWidth/Height` instead — the client area, and for `Nav` also minus
  the 32 epx title-bar row — loses one frame border of width and a frame border plus a title bar of
  height **on every launch**, until the presenter floor catches the window and leaves it parked at its
  minimum size with the navigation pane folded.
* Measured (175%): `FormSize 856.57x480.57` against a window of `870.29x520` epx; one cycle lost exactly
  the 32 epx title bar (513 → 480.57). After the fix the cycle is a fixed point (`1130.29x600` twice).
* The stored value is **discarded exactly once**, gated by `SettingsManager.FormSizeMigrated`. A value in
  the wrong unit cannot be told apart from a deliberate one by looking at it, so there is no size
  threshold to test against — an earlier attempt used one and misread a window legitimately parked at its
  own minimum (874.7 epx at 175%, where the threshold evaluated to 877), resetting it to the default on
  every launch. After the single discard the stored size is trusted as-is.
* **The save is skipped until the layout has run.** `App.OnLaunched` calls `Activate()` and then
  `HideToTray()` on the sign-in path, and `Activate()` returns *before* `Nav.Loaded` — so `XamlRoot`, and
  with it the rasterization scale, is still null there. Falling back to scale 1.0 in that state wrote the
  physical window size (2880x1536 at 175%) into a field that means effective pixels, and the next
  `ApplyInitialSize` multiplied it by 1.75: the window came back clamped to the screen
  (`target=5040x2688 actual=3868x2188`). `Nav.ActualWidth`/`ActualHeight` are checked as well — before
  layout there is no user-chosen geometry to persist either.

**`OverlappedPresenter.PreferredMinimumWidth/Height` are OUTER window sizes, in physical pixels.**
Measured: with the value set to 860 the window stops with `AppWindow.Size.Width` at 860 while
`Nav.ActualWidth` reads 844 — and on this machine at 175% the floor `1523 = round(870.14 × 1.75)`.
The floors are therefore the *same numbers* as the content-width thresholds, with **no** frame-border
correction folded in; subtracting one makes the window stop sooner than the pane rule expects and the
pane can then never be collapsed or re-opened by a drag.

**The frame border is measured, not assumed.** `MainWindow.ObserveFrameBorder` derives it once as
`AppWindow.Size.Width / scale − Nav.ActualWidth` (guarded to the `Restored` state and a sane band).
Measured on a 175% display: **13.71 epx**, where the old `16 physical / 1.75` constant predicted 9.14 —
a 4.6 epx error that made the settings column land ~5 epx short of its budget. Windows scales the frame
with the DPI, so the value in epx is DPI-invariant and one observation per window is enough.

**The settings column keeps a hard budget of `MainWindow.ContentMinWidth = 620` epx.** Every floor is
derived from it; opening the pane must never eat into it, which is why the window floor is dynamic
(`MinWindowWidthExpanded` while the pane is open, `MinWindowWidthCollapsed` while it is folded) and why
the pane folds at `MinWindowWidthExpanded + PaneCollapseHysteresis`. The pane state machine is three
flags, not one: `_paneAutoCollapsed` (we folded it), `_paneUserOverride` (the user opened it anyway at a
narrow width — widening must not re-open, and a resize must not steal it back) and `_paneUserClosed`
(sticky manual close).

---

## 2. Threading delivery model

* **UI thread**: everything in `ExplorerTabUtility.App.WinUI`. XAML objects are single-threaded; touching
  one from another thread throws a `COMException` that cannot even be marked handled.
* **Hook callbacks** (keyboard/mouse/shell events) run on their own threads. They must not touch XAML
  and must not let an exception escape: a stray exception inside a `WH_KEYBOARD_LL` callback is undefined
  behaviour, and inside an `async void` COM event it terminates the process. Both therefore contain their
  loops and hand work over deliberately (`IUiDispatcher.Post` / `TryPost`).
* **`ConfigureAwait.Fody` weaves the whole Core assembly** (all `await`s behave as
  `ConfigureAwait(false)`), and the App shell is deliberately **not** woven. Both halves are load-bearing:
  Core is a hook-engine library that must not hop back to a UI context, while the App shell's async
  continuations stay on the UI thread — `AboutPage`'s decoded-image cache depends on that (it creates and
  consumes `ImageSource` objects, which are thread-affine).
  `ThreadingContractTests` asserts both: the Core probe fails if the weaver stops running, and the
  configuration check fails if the weaver is added to the App project. Removing the weaver is allowed,
  but it means making the Core awaits explicit and updating those two tests.
* Consequence worth remembering: only the App assembly is in a position to "resume on the UI thread".
  Code moved between the two projects inherits different threading behaviour without any code change.

---

## 3. Ownership and lifetime

* **The profile list is published as an immutable snapshot.** `ProfileManager` owns the editing list and
  publishes a fresh cloned array atomically; the hook threads only ever read that array. Never mutate a
  published snapshot in place — not even a bool, because it is the one mutation the contract forbids and
  copying it elsewhere is how the hook threads end up reading half-updated state. Tray edits go through
  the editing list and republish.
* **`DualKeyDictionary` synchronises internally and enumerates snapshots.** Callers must not hold a lock
  across the enumeration body: those bodies make COM calls, and a single slow window would otherwise
  block every other watcher operation. Because enumeration copies, a `foreach` that removes as it goes is
  legal — and because callers rely on that, a live index (`ElementAt(i)`) must never be used instead.
* **COM (RCW) lifetime**: `ExplorerWindow` wraps one interop RCW, interned by COM identity, and owns it
  for as long as the window is tracked. `Dispose` unadvises the connection points and releases the RCW;
  after that every member throws `ObjectDisposedException` (`IsDisposed` lets a caller check first), and
  the `Try…` members return `false` instead of throwing. `Marshal.ReleaseComObject` on a shared RCW is the
  part of this that is easiest to get wrong, so the rule is: a released wrapper is never used again, and
  every lookup that could race the release is non-throwing (`ExplorerWatcher.GetKnownLocation`).
* **One dialog at a time.** WinUI allows a single `ContentDialog` per `XamlRoot`; a second one throws, and
  because each caller catches locally the failure looks like "the hotkey did nothing". The gate therefore
  lives in `ContentDialogService` (`DialogGate`) and **every** dialog builder must take it — including
  `TabSearchDialog`, which builds its own dialog. Dialog theme resolution is shared for the same reason
  (`ContentDialogService.ResolveTheme`): a `ContentDialog` is its own popup tree and does not inherit the
  window's theme, and a second copy of that switch is a second place for the theme to drift.
* **Settings file**: `%APPDATA%\ExplorerTabUtility\settings.json`, written through
  `SettingsManager.WriteAtomic` (temp file → `File.Replace` → `.bak`), debounced 500 ms and flushed on
  exit. `internal` so `SettingsWriteTests` can exercise it against a temporary directory.

---

## 4. Platform constraints and workarounds

| Constraint | Consequence in this code |
|---|---|
| WinUI 3 does not make `Application.RequestedTheme` follow the OS | `SystemTheme` resolves `UISettings` explicitly; every "follow system" path asks it |
| `Application.RequestedTheme` is writable only before the first window | it is assigned once in the `App` constructor; runtime changes go through the root element / dialog `RequestedTheme` |
| A `ContentDialog` never inherits the window's theme | `ContentDialogService.ResolveTheme()`, resolved per show and never cached |
| `Popup.SystemBackdrop` is a no-op on a windowed popup (microsoft-ui-xaml #10087/#10677) | the dropdown material comes from the `ComboBoxDropDownBackground` override in `App.xaml`, which is the documented in-app acrylic fallback |
| The stock `ComboBox` dropdown declares no `ChildTransitions` | `ComboBoxAssist` attaches an `EntranceThemeTransition` with its default 40 epx **horizontal** offset zeroed, so the list rises into place instead of sliding in sideways |
| `WinUI` ships no tray control | `H.NotifyIcon` in `PopupMenu` mode, where only each item's `Command` runs — menu items must be wired with `Command`, never `Click` |
| An unpackaged app has no `ms-appx` resolution and no package identity | the tray icon and `AppWindow.SetIcon` load `Assets/Icon.ico` by absolute path; file pickers need an explicit owner HWND |
| A `ContentDialog` cannot host a second `ContentDialog` | Tab Search's "clear history" confirmation is expressed with in-place controls |
| Explorer's tab interface has no control API | `ExplorerWatcher` drives it with magic `WM_COMMAND` values and the `ShellTabWindowClass` toolwindow |

---

## 5. Localization

Nine languages, all of them satellites except English: the neutral `Resources.resx` **is** English, so
`en` has no resource set of its own. Adding a language means editing four places that cannot be derived
from each other — the `.resx`, `LocalizationService.SupportedLanguages`, `SatelliteResourceLanguages` in
`Directory.Build.props`, and the installer's `[CustomMessages]`.

`LocalizationTests` enforces the part that has actually broken before: every language must ship exactly
the same key set as the neutral file (a missing satellite resolves to `null`, which is what makes the
check meaningful), no value may be empty, and the labels that must differ from English are compared
directly — a satellite that is a copy of the English file would otherwise pass unnoticed.

---

## 6. Release chain

`tools/release.py` is the chain, and it is the only supported way to produce a release:

```
python tools/release.py            # clean → build -warnaserror → test → publish → zip → installer → verify
python tools/release.py --ship     # …and push, move the tag, replace the assets, re-read the release
```

* **The binaries carry the commit they were built from** (`<AppVersion>+<sha>` in
  `AssemblyInformationalVersionAttribute`, written by the `SetSourceRevisionIdFromGit` target in
  `Directory.Build.props` and logged as the `build` line in `startup.log`). That line is the answer to
  "which build produced this log?".
* The script refuses to build from a dirty tree, refuses to ship unless the stamp names a commit whose
  diff against HEAD touches documentation only, and verifies that the DLL inside the portable zip is
  byte-identical to the published one.
* **`.github/workflows/build.yml` must never publish.** A tag-triggered workflow that uploaded release
  assets once replaced the published binaries with a CI build. CI compiles, tests, and checks the
  satellite resources — nothing else.
* The publish parameters are load-bearing and differ from the plain `dotnet publish` defaults; they are
  spelled out once, in `release.py` (and in the hint printed by `pack-portable.py`).

---

## 7. Audit reference numbers

Comments in the code refer to findings by `AUD-nn`. The audit document itself is not kept in the
repository, so the identifiers are indexed here. They are not contiguous: references disappeared together
with the code they described.

| id | What it was about |
|---|---|
| AUD-01 | Window table synchronisation and snapshot enumeration (never block other operations behind one slow window) |
| AUD-02 | Immutable profile snapshot for the hook threads; no shared mutable collection |
| AUD-03 | `NormalizeLocation` must not trim leading separators — `\\` is the UNC marker, `\\?\` the extended-length prefix |
| AUD-04 | Idempotent, fault-tolerant `Dispose` (a second call must not throw) |
| AUD-05 | RCW interning in `ExplorerWindow.Wrap`; unadvising connection points so COM references do not leak |
| AUD-06 | Shell subscribers: a hotkey action with nothing subscribed to it silently does nothing |
| AUD-07 | Web URLs must survive `NormalizeLocation` for `Open()`'s `StartsWith("http")` test |
| AUD-10 | Bounded `Helper.HiddenWindows` cleanup over a long session |
| AUD-11 | Deterministic COM release for `AccessibleObjectFromPoint` on the mouse-navigation path |
| AUD-12 | `async void` handlers and COM callbacks must contain their exceptions |
| AUD-13 | Bounded STA-thread join: an unbounded one hangs the exit with a leftover tray icon |
| AUD-14 | `await` inside hook callbacks — failures must not become unobserved tasks |
| AUD-15 | Settings save/debounce races (lost updates, change-after-dispose) |
| AUD-17 | The `file:///C:/…` shape read back from a window must be preserved for `Navigate2` |
| AUD-19 | `GetOrAdd` with a side-effecting factory (the window was hidden twice) |
| AUD-20 | One `ContentDialog` at a time |
| AUD-22 | `ProcessWatcher` must release tracked `Process` objects that hold a back-reference to it |
| AUD-23 | Cross-thread flags need `volatile` (the JIT may hoist a stale read out of a loop) |
| AUD-25 | `ApplyInitialSize` runs once, on `Nav.Loaded`, so the window cannot snap back after a resize |
| AUD-27 | `WindowInfo.CreatedAt` for windows that already existed at startup (reuse-suppression window) |
| AUD-29 | Drop the default-location records while taking a closed window, so they cannot shadow the history |
