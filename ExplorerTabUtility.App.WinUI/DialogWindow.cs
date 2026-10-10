using System;
using System.Threading.Tasks;
using ExplorerTabUtility.App.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace ExplorerTabUtility.App;

/// <summary>
/// A window that exists to host one dialog (<see cref="Abstractions.DialogHost.Standalone"/>).
/// <para>
/// A <c>ContentDialog</c> can only be rendered inside a <c>XamlRoot</c>, so "a dialog of its own" means a
/// window of its own to host it. That is all this is: the dialog inside is the platform's own control,
/// drawn by the platform in the theme below, and the title bar is the platform's too — nothing here is
/// custom-drawn.
/// </para>
/// <para>
/// Why a window at all: the prompt that uses this asks about File Explorer's windows and arrives while
/// the app window may be hidden in the tray. Hosting it in the app window would mean surfacing that
/// window — the settings window appearing to ask a question about Explorer — and the dialog could not be
/// moved anywhere the app window does not go.
/// </para>
/// </summary>
internal sealed class DialogWindow : IDisposable
{
    // Effective pixels: the platform measures here, so the window is scaled by the display's DPI below.
    // Sized for a two-line question and the button row — the first attempt used these numbers as physical
    // pixels, which on a 200% display came out at half the size and squeezed the dialog into itself.
    private const int WidthInEpx = 460;
    private const int HeightInEpx = 230;

    private readonly Window _window;
    private readonly Grid _root;
    private readonly TaskCompletionSource _loaded = new();
    private readonly bool _dark;

    public DialogWindow(string title)
    {
        _dark = ContentDialogService.ResolveTheme() == ElementTheme.Dark;

        _root = new Grid
        {
            // Matches the app window, so a dialog on a dark app is dark.
            RequestedTheme = _dark ? ElementTheme.Dark : ElementTheme.Light
        };

        _window = new Window
        {
            // The caption carries the dialog's title, so the dialog itself does not repeat it.
            Title = title,
            Content = _root
        };

        var appWindow = _window.AppWindow;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // A dialog: fixed size, no maximize/minimize. The title bar stays — it is what makes the
            // window movable, and letting the system draw it keeps this free of custom chrome.
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;

            // On top, because the question has to be answerable: it is raised from a background thread
            // while anything may be in front — the tab-search picker does the same for the same reason,
            // and a dialog the user cannot see is a dialog they cannot answer. It is released with the
            // window, which lives only as long as the question does.
            presenter.IsAlwaysOnTop = true;
        }

        // Not an app the user switches to; it belongs to the app that raised it (and Win32 dialogs are
        // not in the taskbar either).
        appWindow.IsShownInSwitchers = false;

        // Provisional size, corrected to the display's scale once the XamlRoot can report it.
        appWindow.Resize(new SizeInt32(WidthInEpx, HeightInEpx));
        Center(appWindow, WidthInEpx, HeightInEpx);

        _root.Loaded += (_, _) => OnLoaded();

        _window.Activate();
    }

    /// <summary>The root the dialog is shown on — valid once <see cref="LoadedAsync"/> has completed.</summary>
    public XamlRoot XamlRoot => _root.XamlRoot;

    public Task LoadedAsync() => _loaded.Task;

    public void Dispose()
    {
        try
        {
            _window.Close();
        }
        catch (Exception)
        {
            // Closing a window that was never shown (or already closed) is not a failure worth raising:
            // the dialog has been answered by the time this runs.
        }
    }

    private void OnLoaded()
    {
        try
        {
            // AppWindow sizes and positions in physical pixels; the constants above are effective
            // pixels. Without this the dialog is laid out for a window half the size it asked for
            // (measured on a 200% display: the title overlapped the message).
            var scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
            var width = (int)Math.Round(WidthInEpx * scale);
            var height = (int)Math.Round(HeightInEpx * scale);

            _window.AppWindow.Resize(new SizeInt32(width, height));
            Center(_window.AppWindow, width, height);

            CaptionColors.Apply(_window.AppWindow, _dark);
        }
        catch (Exception)
        {
            // A dialog at the provisional size is still a usable dialog; never fail the question over it.
        }
        finally
        {
            _loaded.TrySetResult();
        }
    }

    private static void Center(AppWindow appWindow, int width, int height)
    {
        // Centred on the primary display: a dialog with no owner has no window to centre on, and this is
        // where Windows puts its own.
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        appWindow.Move(new PointInt32(
            area.X + ((area.Width - width) / 2),
            area.Y + ((area.Height - height) / 2)));
    }
}
