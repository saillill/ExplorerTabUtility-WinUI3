`ExplorerTabUtility_<version>_Setup_x64.exe` — per-user install, no administrator rights required, includes an uninstaller. If the .NET 10 Desktop Runtime or the Windows App Runtime is missing,Setup offers to download and install it.
`ExplorerTabUtility_<version>_Portable_x64.zip` — framework-dependent: extract and run, but the .NET 10 Desktop Runtime and the Windows App Runtime must already be present.
`ExplorerTabUtility_<version>_Setup_arm64.exe` — the same installer, built for ARM64.
`ExplorerTabUtility_<version>_Portable_arm64.zip` — the portable folder for ARM64.

Settings live in `%APPDATA%\ExplorerTabUtility\`, outside the install directory, so upgrading or reinstalling never clears them. The uninstaller asks whether to delete them; the default is to keep.

**A note**: since the app is not code-signed, your browser's security check and Windows may flag it as a potential risk. This is expected and nothing to worry about.
