using System;
using System.Threading.Tasks;
using ExplorerTabUtility.App.Services;
using H.NotifyIcon.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
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

    private uint _dragPointer;
    private FrameworkElement _dragSurface = null!;
    private Point _dragOriginInWindow;
    private PointInt32 _windowOrigin;

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
            // Kept for the shell (alt-tab, taskbar); the dialog itself shows the title, because this window
            // has no caption to put it in (see below).
            Title = title,
            Content = _root,

            // Same backdrop as the main window, and not only for looks: a WinUI window paints nothing
            // itself, so without a backdrop the area the dialog does not cover came out black — a black
            // frame around the dialog card.
            SystemBackdrop = new MicaBackdrop()
        };

        var appWindow = _window.AppWindow;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // No visible caption: the dialog is the window, and a title bar made this read as a Win32 window
            // wrapped around a WinUI dialog, which it was. The title bar itself stays — with the content
            // extended into it nothing of it is drawn, and a borderless window with the caption removed
            // entirely cannot be dragged at all (measured: SetDragRectangles had no effect until the caption
            // came back).
            presenter.SetBorderAndTitleBar(false, true);
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

        // Provisional size and position, both corrected in OnLoaded once the display's scale is known.
        appWindow.Resize(new SizeInt32(WidthInEpx, HeightInEpx));
        CenterOnPrimaryDisplay(appWindow);

        _root.Loaded += (_, _) => OnLoaded();

        // Both are idempotent, and between them the layout lands whichever of the two arrives with the
        // XamlRoot in place.
        _window.Activated += (_, _) => ApplyLayout();

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
            // The XamlRoot is attached just after Loaded — measured: it is still null inside this handler
            // (so the scale read 1.0 and the window kept its provisional size), while the dialog shown a
            // moment later on that same root works. Laying out on the next turn of the queue is what makes
            // the scale readable.
            _root.DispatcherQueue?.TryEnqueue(ApplyLayout);
        }
        catch (Exception ex)
        {
            // Reported, not swallowed: a silently failed layout here means the user gets an unthemed or
            // stuck dialog and nothing anywhere says why.
            StartupLog.Fail("DialogWindow: layout", ex);
        }
        finally
        {
            // Released even when the layout failed: a dialog at the provisional size is still usable, and
            // the question must not be lost over window furniture.
            _loaded.TrySetResult();
        }
    }

    /// <summary>
    /// Sizes, centres and wires up the window once the display's scale is known.
    /// </summary>
    private void ApplyLayout()
    {
        if (_root.XamlRoot is not { } xamlRoot) return;

        var appWindow = _window.AppWindow;
        var scale = xamlRoot.RasterizationScale;

        // AppWindow works in physical pixels; the constants above are effective pixels.
        appWindow.Resize(new SizeInt32(
            (int)Math.Round(WidthInEpx * scale),
            (int)Math.Round(HeightInEpx * scale)));

        CenterOnPrimaryDisplay(appWindow);

        // Rounded like every other WinUI surface — a borderless window is square without this.
        DesktopWindowsManagerMethods.SetRoundedCorners((nint)WinRT.Interop.WindowNative.GetWindowHandle(_window));

        // The caption's colours are painted even though nothing of the caption itself is drawn, and they
        // are what the close button uses.
        CaptionColors.Apply(appWindow, _dark);

        // The contents extend into the caption, so none of it is drawn.
        appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
    }

    /// <summary>
    /// Makes <paramref name="surface"/> a drag handle: pressing it and moving moves the window.
    /// <para>
    /// Called with the dialog itself, because the dialog lives in a popup of its own and its pointer
    /// events never reach this window's root element — attaching the handlers there was measured to do
    /// nothing at all. They are attached with <c>handledEventsToo</c> and deliberately never mark the event
    /// handled, so the dialog's own controls still receive every one of them.
    /// </para>
    /// <para>
    /// The supported alternative, <c>AppWindowTitleBar.SetDragRectangles</c>, was tried first and does not
    /// work for this window: with the caption removed entirely it had no effect, and with the caption
    /// present but undrawn the window still would not move (both measured by dragging it from a script).
    /// </para>
    /// </summary>
    public void EnableDragging(FrameworkElement surface)
    {
        _dragSurface = surface;

        surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        surface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        surface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragPointer = e.Pointer.PointerId;
        _dragOriginInWindow = e.GetCurrentPoint(_dragSurface).Position;
        _windowOrigin = _window.AppWindow.Position;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId) return;

        var position = e.GetCurrentPoint(_dragSurface).Position;

        // Only the header drags: below it are the buttons and the text, which have to keep behaving like
        // buttons and text.
        if (position.Y > _window.AppWindow.TitleBar.Height) return;

        _window.AppWindow.Move(new PointInt32(
            _windowOrigin.X + (int)Math.Round(position.X - _dragOriginInWindow.X),
            _windowOrigin.Y + (int)Math.Round(position.Y - _dragOriginInWindow.Y)));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer == e.Pointer.PointerId) _dragPointer = 0;
    }

    /// <summary>
    /// Centres on the primary display, using the size the window actually has — <c>DisplayArea.WorkArea</c>
    /// and <c>AppWindow.Size</c> agree on units, so nothing is converted twice. A dialog with no owner has
    /// no window to centre on, and the primary display is where Windows puts its own.
    /// </summary>
    private static void CenterOnPrimaryDisplay(AppWindow appWindow)
    {
        var size = appWindow.Size;
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        appWindow.Move(new PointInt32(
            area.X + ((area.Width - size.Width) / 2),
            area.Y + ((area.Height - size.Height) / 2)));
    }
}
