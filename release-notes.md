WinUI 3 rewrite. The UI ships in nine languages: Simplified Chinese, Traditional Chinese, English,
Spanish, French, Russian, German, Japanese and Korean.

Fixes in this build:

- The window minimum size now follows the monitor's DPI scale. Previously it was computed once
  for the startup display, so after moving the window to a monitor with different scaling
  (e.g. 200% → 100%) the stale physical-pixel limit blocked resizing.
- Tab search opened while the window is hidden in the tray now surfaces the window first —
  the picker used to open invisibly and the hotkey then stayed dead for the rest of the session.
- The tray profile menus refresh immediately when profiles are edited, and toggling window
  interception via hotkey now also pauses tab reuse, matching the tray menu behaviour.
- Robustness: a failed launch no longer leaves a zombie process that blocks all later launches;
  closed-tab matching no longer misfires after 25 days of system uptime; XButton mouse profiles
  no longer trigger double navigation; assorted lifetime and settings-sync fixes.

- `ExplorerTabUtility_<version>_Setup.exe` — per-user install, no administrator rights required,
  includes an uninstaller. If the .NET 10 Desktop Runtime or the Windows App Runtime is missing,
  Setup offers to download and install it.
- `ExplorerTabUtility_<version>_Portable_x64.zip` — framework-dependent: extract and run, but the
  .NET 10 Desktop Runtime and the Windows App Runtime must already be present.

Settings live in `%APPDATA%\ExplorerTabUtility\`, outside the install directory, so upgrading or
reinstalling never clears them. The uninstaller asks whether to delete them; the default is to keep.
