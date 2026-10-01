WinUI 3 rewrite. The UI ships in nine languages: Simplified Chinese, Traditional Chinese, English,
Spanish, French, Russian, German, Japanese and Korean.

Fixes in this build:

- A shortcut's action is no longer changed silently. If a stored profile holds an action its scope does
  not allow — only reachable through an imported or hand-edited settings file — the reset is now saved and
  reported, instead of leaving the editor showing one action while the hotkey did another.
- A dialog left open can no longer block every later dialog and the tab-search picker for the rest of the
  session: the wait is bounded and the log records why it gave up. Hiding the window while a dialog is open
  is refused for the same reason.
- The tab-search picker keeps the window in front while it is open, even over a fullscreen game, instead of
  the promotion being dropped on a timer mid-selection.
- After an Explorer restart, a window can no longer be mistaken for one that had already been hidden, which
  used to leave that window out of the tab-folding path (recycled window handles).

This refresh also carries everything published for this version earlier: outer-window-size persistence (no
window that shrinks on every launch, and the sign-in start no longer saves the size before the window has
been laid out), a measured minimum width, dialogs that surface a hidden window before opening, tab search
sharing the one-dialog rule and the app theme, keyboard and screen-reader access to shortcut rows, and
failure logging to `error.log`.

- `ExplorerTabUtility_<version>_Setup.exe` — per-user install, no administrator rights required,
  includes an uninstaller. If the .NET 10 Desktop Runtime or the Windows App Runtime is missing,
  Setup offers to download and install it.
- `ExplorerTabUtility_<version>_Portable_x64.zip` — framework-dependent: extract and run, but the
  .NET 10 Desktop Runtime and the Windows App Runtime must already be present.

Settings live in `%APPDATA%\ExplorerTabUtility\`, outside the install directory, so upgrading or
reinstalling never clears them. The uninstaller asks whether to delete them; the default is to keep.
