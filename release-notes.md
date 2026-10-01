WinUI 3 rewrite. The UI ships in nine languages: Simplified Chinese, Traditional Chinese, English,
Spanish, French, Russian, German, Japanese and Korean.

Fixes in this build:

- The settings window no longer shrinks a little on every launch, and no longer opens at screen
  size after being hidden while maximised. Both came from saving the window's client area and
  restoring it as the outer window size.
- The minimum window width is now measured on the live window instead of assumed from a constant,
  so the settings column keeps its full budget at every DPI scale — and the navigation pane stays
  open at the default window size instead of folding away on startup.
- Tab search now shares the app-wide one-dialog rule and follows the app theme: it could silently
  fail next to another dialog, and it rendered in the wrong theme after switching themes at runtime.
  Reading Explorer's tabs also no longer blocks the window while a folder is slow to answer.
- Shortcut rows can be expanded with the keyboard and by screen readers; the About navigation item
  shows the normal selected/hover feedback again; the settings column is capped in width; dropdowns
  and disclosures rise into place instead of sliding in from the side.
- Failures are now appended to `error.log` (the startup log is truncated on each run) and the startup
  log records the build revision, so a deployed binary can be traced back to a commit.

This refresh of the same version also carries the previous build's fixes: DPI-aware minimum window
size, surfacing the window before the tab-search picker, immediate tray menu refresh, and assorted
lifetime and settings-sync fixes.

- `ExplorerTabUtility_<version>_Setup.exe` — per-user install, no administrator rights required,
  includes an uninstaller. If the .NET 10 Desktop Runtime or the Windows App Runtime is missing,
  Setup offers to download and install it.
- `ExplorerTabUtility_<version>_Portable_x64.zip` — framework-dependent: extract and run, but the
  .NET 10 Desktop Runtime and the Windows App Runtime must already be present.

Settings live in `%APPDATA%\ExplorerTabUtility\`, outside the install directory, so upgrading or
reinstalling never clears them. The uninstaller asks whether to delete them; the default is to keep.
