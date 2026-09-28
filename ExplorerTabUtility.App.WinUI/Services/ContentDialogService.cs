using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ExplorerTabUtility.Abstractions;
using ExplorerTabUtility.Helpers;

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
/// </summary>
public sealed class ContentDialogService : IDialogService
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<XamlRoot?> _xamlRootProvider;

    public ContentDialogService(IUiDispatcher dispatcher, Func<XamlRoot?> xamlRootProvider)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _xamlRootProvider = xamlRootProvider ?? throw new ArgumentNullException(nameof(xamlRootProvider));
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
        var xamlRoot = _xamlRootProvider();
        if (xamlRoot is null)
        {
            // No window yet — nothing to host a ContentDialog on. Falling back to a native
            // message box keeps the caller informed instead of failing silently.
            NativeMessageBox.Show(message, title, ToNativeIcon(icon));
            return DialogResult.OK;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            DefaultButton = ToDefaultButton(buttons, defaultResult)
        };

        dialog.Content = BuildContent(message, icon);

        switch (buttons)
        {
            case DialogButton.OK:
                dialog.CloseButtonText = "OK";
                break;

            case DialogButton.OKCancel:
                dialog.PrimaryButtonText = "OK";
                dialog.CloseButtonText = LocalizationService.Get("Cancel");
                break;

            case DialogButton.YesNo:
                dialog.PrimaryButtonText = LocalizationService.Get("Yes");
                dialog.CloseButtonText = LocalizationService.Get("No");
                break;
        }

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
