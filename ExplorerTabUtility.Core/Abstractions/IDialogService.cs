namespace ExplorerTabUtility.Abstractions;

/// <summary>Which buttons a dialog shows. Mirrors the Shell message-box layout.</summary>
public enum DialogButton
{
    OK,
    OKCancel,
    YesNo
}

/// <summary>Semantic dialog icon — the UI shell picks the platform-native glyph.</summary>
public enum DialogIcon
{
    None,
    Information,
    Warning,
    Error,
    Question
}

/// <summary>Platform-neutral dialog result.</summary>
public enum DialogResult
{
    None,
    OK,
    Cancel,
    Yes,
    No
}

/// <summary>
/// Where a dialog is hosted.
/// <para>
/// <see cref="InWindow"/> is the default: the dialog renders inside the app window (a
/// <c>ContentDialog</c> in the WinUI shell), which means it moves with that window and cannot leave it.
/// <see cref="Standalone"/> asks for a window of its own — movable, independent of the app window, and
/// shown without surfacing it. The WinUI shell backs that with the platform's own message box, the way
/// File Explorer's own prompts are separate windows.
/// </para>
/// </summary>
public enum DialogHost
{
    InWindow,
    Standalone
}

/// <summary>
/// Shows a modal message dialog.
/// <para>
/// Exists so the Core library never references a UI toolkit. The WPF shell backs this with
/// its <c>CustomMessageBox</c>; the WinUI shell backs it with a <c>ContentDialog</c>.
/// </para>
/// </summary>
public interface IDialogService
{
    DialogResult Show(
        string message,
        string title,
        DialogButton buttons = DialogButton.OK,
        DialogIcon icon = DialogIcon.None,
        DialogResult defaultResult = DialogResult.None,
        DialogHost host = DialogHost.InWindow);
}
