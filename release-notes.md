WinUI 3 rewrite. The UI ships in nine languages: Simplified Chinese, Traditional Chinese, English,
Spanish, French, Russian, German, Japanese and Korean.

- `ExplorerTabUtility_<version>_Setup.exe` — per-user install, no administrator rights required,
  includes an uninstaller. If the .NET 10 Desktop Runtime or the Windows App Runtime is missing,
  Setup offers to download and install it.
- `ExplorerTabUtility_<version>_Portable_x64.zip` — framework-dependent: extract and run, but the
  .NET 10 Desktop Runtime and the Windows App Runtime must already be present.

Settings live in `%APPDATA%\ExplorerTabUtility\`, outside the install directory, so upgrading or
reinstalling never clears them. The uninstaller asks whether to delete them; the default is to keep.
