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
        DialogResult defaultResult = DialogResult.None);
}
