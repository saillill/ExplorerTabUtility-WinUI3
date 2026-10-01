WinUI 3 rewrite. The UI ships in nine languages: Simplified Chinese, Traditional Chinese, English,
Spanish, French, Russian, German, Japanese and Korean.

Fixes in this build:

- A window restored by the sign-in entry with "hide window on startup" could come back sized to the
  screen: its geometry was saved before the window had been laid out, in the wrong unit. The save is now
  skipped until the layout exists, and a stored size is validated once instead of on every launch.
- A dialog raised while the window was hidden in the tray was invisible and could not be dismissed. That
  also blocked every later dialog for the rest of the session — tab search included — and left tab
  actions waiting. The window is now brought up before any dialog is shown.
- Paths are no longer mangled by surrounding whitespace or quotes: `"  https://host/x  "` used to be
  rewritten into a file path, and a CLSID wrapped in spaces lost its `shell::` prefix.
- A released Explorer window now reports why it is unusable, instead of the generic COM error that made
  a failing hotkey look like nothing happened. The restore-from-tray line in the log no longer claims the
  window was never hidden.
- Typing in the shortcut editor no longer rebuilds the tray menus and re-lays out every row once per
  keystroke.
- Tooling: 155 unit tests, a CI build that compiles with warnings-as-errors and runs them, and
  `tools/release.py` as the single release chain. `docs/ARCHITECTURE.md` records the window-geometry,
  threading and ownership contracts that used to live only in comments.

This build also carries everything published for this version earlier: outer-window-size persistence (no
window that shrinks on every launch), a measured minimum width, tab search sharing the one-dialog rule and
the app theme, keyboard and screen-reader access to shortcut rows, and failure logging to `error.log`.

- `ExplorerTabUtility_<version>_Setup.exe` — per-user install, no administrator rights required,
  includes an uninstaller. If the .NET 10 Desktop Runtime or the Windows App Runtime is missing,
  Setup offers to download and install it.
- `ExplorerTabUtility_<version>_Portable_x64.zip` — framework-dependent: extract and run, but the
  .NET 10 Desktop Runtime and the Windows App Runtime must already be present.

Settings live in `%APPDATA%\ExplorerTabUtility\`, outside the install directory, so upgrading or
reinstalling never clears them. The uninstaller asks whether to delete them; the default is to keep.
