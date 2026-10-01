using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ExplorerTabUtility.Abstractions;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// <see cref="IDialogService"/> backed by the native <see cref="ContentDialog"/>.
/// <para>
/// Two entry points, because WinUI dialogs are inherently asynchronous and need a
/// <see cref="XamlRoot"/>:
/// </para>
/// <list type="bullet">
/// <item><see cref="ShowAsync"/> — for callers already on the UI thread.</item>
/// <item><see cref="Show"/> — the <see cref="IDialogService"/> contract, for Core callers on a
/// background/STA thread. It queues the dialog and blocks only that calling thread.</item>
/// </list>
/// <para>
/// <b>The one-dialog-at-a-time rule is process-wide, so its gate lives here and is shared.</b> WinUI
/// throws a COMException if a second <see cref="ContentDialog"/> is opened while another is still up,
/// and that applies to <em>every</em> ContentDialog in the same <see cref="XamlRoot"/> — including
/// dialogs this class never constructed. A dialog built elsewhere (the tab-search picker in
/// <c>TabSearchDialog</c>) must therefore take <see cref="DialogGate"/> too; otherwise the two can
/// collide, and because each caller catches the exception locally, the failure is silent — the hotkey
/// simply appears dead.
/// </para>
/// </summary>
public sealed class ContentDialogService : IDialogService
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<XamlRoot?> _xamlRootProvider;

    /// <summary>
    /// Brings the window on screen if it is hidden in the tray. Called before every dialog.
    /// </summary>
    /// <remarks>
    /// A dialog attached to a hidden window is invisible <b>and</b> unclosable, and because it holds
    /// <see cref="DialogGate"/> that is not a cosmetic problem: every later dialog — including the tab
    /// search picker — queues behind it for the rest of the session, and the caller blocked on it stays
    /// blocked. The reachable case is the "restore previously opened windows?" prompt, which is raised
    /// when a new Explorer window registers after an Explorer crash, i.e. while this app is typically
    /// hidden in the tray; its caller (`ExplorerWatcher.RestorePreviousWindows`) blocks the whole STA
    /// task queue on the answer, so tab actions stop responding too.
    /// <para>
    /// Optional so the service stays constructible without a window; the composition root supplies it.
    /// </para>
    /// </remarks>
    private readonly Action? _ensureWindowVisible;

    /// <summary>
    /// Gate so only one <see cref="ContentDialog"/> is ever open. WinUI throws a COMException if a
    /// second one is shown while the first is still up (AUD-20).
    /// <para>
    /// Exposed to sibling dialog builders so the rule holds across the whole app rather than only for
    /// the dialogs that happen to go through <see cref="ShowAsync"/>.
    /// </para>
    /// </summary>
    internal System.Threading.SemaphoreSlim DialogGate { get; } = new(1, 1);

    public ContentDialogService(
        IUiDispatcher dispatcher,
        Func<XamlRoot?> xamlRootProvider,
        Action? ensureWindowVisible = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _xamlRootProvider = xamlRootProvider ?? throw new ArgumentNullException(nameof(xamlRootProvider));
        _ensureWindowVisible = ensureWindowVisible;
    }

    DialogResult IDialogService.Show(
        string message,
        string title,
        DialogButton buttons,
        DialogIcon icon,
        DialogResult defaultResult)
    {
        // Called from Core's STA thread (e.g. the "restore previous windows?" prompt inside
        // ExplorerWatcher). Blocking here is fine — it is not the UI thread.
        var completion = new TaskCompletionSource<DialogResult>();

        _dispatcher.Post(async () =>
        {
            try
            {
                completion.TrySetResult(await ShowAsync(message, title, buttons, icon, defaultResult));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task.GetAwaiter().GetResult();
    }

    /// <summary>Shows the dialog. Must be called on the UI thread.</summary>
    public async Task<DialogResult> ShowAsync(
        string message,
        string title,
        DialogButton buttons = DialogButton.OK,
        DialogIcon icon = DialogIcon.None,
        DialogResult defaultResult = DialogResult.None)
    {
        // Serialize: only one ContentDialog may be open at a time. Awaiting the gate on the UI thread
        // simply returns to the message loop, so a queued dialog appears once the current one closes
        // rather than throwing (AUD-20).
        await DialogGate.WaitAsync();
        try
        {
            return await ShowCoreAsync(message, title, buttons, icon, defaultResult);
        }
        finally
        {
            DialogGate.Release();
        }
    }

    private async Task<DialogResult> ShowCoreAsync(
        string message,
        string title,
        DialogButton buttons,
        DialogIcon icon,
        DialogResult defaultResult)
    {
        // Before anything else: a dialog needs a window the user can actually see and reach. Every
        // dialog the app raises from a background thread (the restore prompt, the second-instance
        // notice) can arrive while this window is hidden in the tray, and an invisible dialog cannot be
        // dismissed — which, with the gate below, would strand every later dialog and the blocked caller.
        _ensureWindowVisible?.Invoke();

        var xamlRoot = _xamlRootProvider();
        if (xamlRoot is null)
        {
            // No window yet — nothing to host a ContentDialog on. Falling back to a native
            // message box keeps the caller informed instead of failing silently.
            //
            // Known limitation, deliberately not papered over: a Win32 MessageBox is drawn by the
            // system and follows the Windows app theme, so it can never follow the theme this app
            // applies. It is unreachable in practice — AppServices is built after Application.Start
            // but before MainWindow, every dialog call is posted to the dispatcher queue, and no
            // dialog is produced while MainWindow is being constructed (nothing subscribes to
            // anything until WireServices returns, and the window is Activate()d after that). If a
            // future call site ever fires before Activate(), its dialog comes out system-themed.
            NativeMessageBox.Show(message, title, ToNativeIcon(icon));
            return DialogResult.OK;
        }

        var dialog = new ContentDialog();

        // Give the dialog the theme that is actually in effect, otherwise it resolves its own theme
        // from Application.RequestedTheme and disagrees with the window.
        //
        // A ContentDialog is not a child of the window's XAML tree — it is hosted in its own popup
        // with its own XamlRoot, so its visual parent is null and it never inherits the ElementTheme
        // that MainWindow.ApplySavedTheme (MainWindow.xaml.cs) sets on the root element. Measured on
        // this codebase: with Application.RequestedTheme=Light while the root was Dark, the dialog
        // came up Light — the window and its dialog disagreed, which is what the user saw as "the
        // dialog ignores the app theme". The two theme sources are independent:
        //   App.ApplyApplicationTheme()  -> Application.RequestedTheme  (assigned once, in the ctor)
        //   MainWindow.ApplySavedTheme() -> root element RequestedTheme  (re-applied at runtime)
        // Setting the theme below is what makes ResolveTheme() — not Application.RequestedTheme — the
        // dialog's source of truth, so the two agree for every ThemeMode rather than only when the
        // ThemeMode setting and the OS theme coincide.
        //
        // Application.RequestedTheme cannot be used to fix this: it is only writable before the first
        // window is created. RequestedTheme on the dialog itself is the native switch that works at
        // runtime (ContentDialog has no ContentTheme property in WinAppSDK 2.5.1 — only
        // ContentDialogButton does).
        //
        // Resolved per call, never cached: the user can switch theme while the app runs, and the next
        // dialog must reflect the new theme immediately. It must also follow the OS when the app is on
        // "follow system" — see ResolveTheme for why ElementTheme.Default cannot serve as the fallback.
        dialog.RequestedTheme = ResolveTheme();

        // Everything else is populated BEFORE XamlRoot/Title are assigned, because ShowAsync touches
        // the root and activates the popup: changing Content after that makes the re-entrant popup
        // throw "Cannot change the activation state of a popup that is already in the process of
        // closing or activating" (0x80000019). Content, button text and DefaultButton first; the two
        // that attach the dialog to the tree last.
        dialog.Content = BuildContent(message, icon);
        dialog.DefaultButton = ToDefaultButton(buttons, defaultResult);

        switch (buttons)
        {
            case DialogButton.OK:
                // Use the resource key rather than a hard-coded "OK": the string is already translated in
                // all 9 resource files, and hard-coding it left that key unused (AUD report §5.1).
                dialog.CloseButtonText = LocalizationService.Get("OK");
                break;

            case DialogButton.OKCancel:
                dialog.PrimaryButtonText = LocalizationService.Get("OK");
                dialog.CloseButtonText = LocalizationService.Get("Cancel");
                break;

            case DialogButton.YesNo:
                dialog.PrimaryButtonText = LocalizationService.Get("Yes");
                dialog.CloseButtonText = LocalizationService.Get("No");
                break;
        }

        dialog.XamlRoot = xamlRoot;
        dialog.Title = title;

        var result = await dialog.ShowAsync();

        return (buttons, result) switch
        {
            (DialogButton.YesNo, ContentDialogResult.Primary) => DialogResult.Yes,
            (DialogButton.YesNo, _) => DialogResult.No,
            (DialogButton.OKCancel, ContentDialogResult.Primary) => DialogResult.OK,
            (DialogButton.OKCancel, _) => DialogResult.Cancel,
            _ => DialogResult.OK
        };
    }

    /// <summary>
    /// ContentDialog has no icon slot, so the semantic icon becomes an official
    /// <see cref="FontIcon"/> tinted with the official semantic brush — no custom drawing.
    /// </summary>
    private static object BuildContent(string message, DialogIcon icon)
    {
        if (icon == DialogIcon.None)
            return new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };

        var (glyph, brushKey) = icon switch
        {
            DialogIcon.Information => ("\uE946", "SystemFillColorAttentionBrush"),
            DialogIcon.Warning => ("\uE7BA", "SystemFillColorCautionBrush"),
            DialogIcon.Error => ("\uEA39", "SystemFillColorCriticalBrush"),
            DialogIcon.Question => ("\uE9CE", "SystemFillColorAttentionBrush"),
            _ => ("\uE946", "SystemFillColorAttentionBrush")
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconElement = new FontIcon
        {
            Glyph = glyph,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Top
        };

        if (Application.Current.Resources.TryGetValue(brushKey, out var brush))
            iconElement.Foreground = (Microsoft.UI.Xaml.Media.Brush)brush;

        Grid.SetColumn(iconElement, 0);
        grid.Children.Add(iconElement);

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return grid;
    }

    /// <summary>
    /// Maps the saved theme setting to the theme a dialog should be rendered in.
    /// <para>
    /// This mirrors <c>MainWindow.ApplySavedTheme</c> exactly on purpose: a dialog is a separate
    /// popup tree, so the only way it can match the window is by resolving the same setting the same
    /// way. Kept as its own method (rather than reusing the window's) because the window one writes to
    /// an element and would drag the window into the service.
    /// </para>
    /// <para>
    /// <b>Shared with every other dialog builder in the app</b> (the tab-search picker included): a
    /// second copy of this switch would be a second place for the runtime theme to drift, which is
    /// exactly how a dialog ends up light on a dark window.
    /// </para>
    /// <para>
    /// It is called on every show rather than once, so a theme change made while the app is running is
    /// picked up by the very next dialog; nothing here may be cached.
    /// </para>
    /// <para>
    /// "Follow system" (0) MUST ask the OS here rather than return <see cref="ElementTheme.Default"/>.
    /// Default would inherit <c>Application.RequestedTheme</c>, which <c>App.ApplyApplicationTheme</c>
    /// assigns exactly once in the Application constructor. That value is a snapshot: after it is set,
    /// a later OS theme change never reaches it. Measured: with the OS on light at launch the app theme
    /// is pinned to Light; flipping the OS to dark mid-session leaves <c>Application.RequestedTheme</c>
    /// at Light while the window (whose theme <c>MainWindow.OnSystemThemeChanged</c> re-applies from
    /// <see cref="SystemTheme"/>) goes dark — and a dialog on Default then renders light on a dark
    /// window, which is precisely the mismatch this service exists to prevent. Resolving through
    /// <see cref="SystemTheme"/> re-reads <c>UISettings</c> on every call, so it costs nothing to
    /// resolve per show and keeps "follow system" following the system.
    /// </para>
    /// <para>
    /// Note for anyone reproducing this: the mismatch is only visible when the application theme is
    /// assigned EXPLICITLY, as this app does. Left untouched, WinAppSDK resolves the ambient app theme
    /// from the OS on demand and Default appears to track the OS — so a probe that never assigns
    /// <c>Application.RequestedTheme</c> will wrongly conclude there is no defect.
    /// </para>
    /// </summary>
    internal static ElementTheme ResolveTheme() => SettingsManager.ThemeMode switch
    {
        1 => ElementTheme.Dark,
        2 => ElementTheme.Light,
        // "Follow system": ask the OS now. Never ElementTheme.Default — see the remarks above.
        _ => SystemTheme.IsDark() ? ElementTheme.Dark : ElementTheme.Light
    };

    private static ContentDialogButton ToDefaultButton(DialogButton buttons, DialogResult defaultResult)
    {
        return (buttons, defaultResult) switch
        {
            (DialogButton.YesNo, DialogResult.Yes) => ContentDialogButton.Primary,
            (DialogButton.YesNo, _) => ContentDialogButton.Close,
            (DialogButton.OKCancel, DialogResult.Cancel) => ContentDialogButton.Close,
            (DialogButton.OKCancel, _) => ContentDialogButton.Primary,
            _ => ContentDialogButton.Close
        };
    }

    private static NativeMessageBox.Icon ToNativeIcon(DialogIcon icon) => icon switch
    {
        DialogIcon.Information => NativeMessageBox.Icon.Information,
        DialogIcon.Warning => NativeMessageBox.Icon.Warning,
        DialogIcon.Error => NativeMessageBox.Icon.Error,
        DialogIcon.Question => NativeMessageBox.Icon.Question,
        _ => NativeMessageBox.Icon.None
    };
}
