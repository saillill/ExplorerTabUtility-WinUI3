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
/// drawn by the platform in the theme below, and nothing here is custom-drawn.
/// </para>
/// <para>
/// Why a window at all: the prompt that uses this asks about File Explorer's windows and arrives while the
/// app window may be hidden in the tray. Hosting it in the app window would mean surfacing that window —
/// the settings window appearing to ask a question about Explorer — and the dialog could not be moved
/// anywhere the app window does not go.
/// </para>
/// </summary>
internal sealed class DialogWindow : IDisposable
{
    // Effective pixels. The window is sized to the dialog itself in FitTo; these are what it uses until
    // then, and the ceiling when the measurement is unusable.
    private const double DefaultWidthInEpx = 460;
    private const double DefaultHeightInEpx = 230;
    private const double MaxWidthInEpx = 620;
    private const double MaxHeightInEpx = 640;

    private readonly Window _window;
    private readonly Grid _root;
    private readonly TaskCompletionSource _loaded = new();
    private readonly bool _dark;

    private double _widthInEpx = DefaultWidthInEpx;
    private double _heightInEpx = DefaultHeightInEpx;

    private uint _dragPointer;
    private FrameworkElement? _dragSurface;
    private Point _dragPoint;
    private PointInt32 _windowOrigin;

    public DialogWindow(string title)
    {
        _dark = ContentDialogService.ResolveTheme() == ElementTheme.Dark;

        _root = new Grid
        {
            // Matches the app window, so a dialog on a dark app is dark.
            RequestedTheme = _dark ? ElementTheme.Dark : ElementTheme.Light,

            // The dialog's own colour, so the area the card does not cover reads as part of the dialog
            // rather than as a frame around it. A WinUI window paints nothing itself, and both ways of
            // making that area invisible were measured to fail (see ApplyLayout) — so it is filled with the
            // surface the dialog is using instead. Shrinking the window onto the card was also tried: the
            // card's real size cannot be read (measuring the dialog returns the template root, which carries
            // the smoke layer and answers with the constraint it was given).
            Background = CardBackground()
        };

        _window = new Window
        {
            // Kept for the shell (alt-tab, taskbar); the dialog shows the title itself, because this
            // window has no caption to put it in.
            Title = title,
            Content = _root
        };

        var appWindow = _window.AppWindow;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // No caption and no border: the dialog is the window. A title bar made this read as a Win32
            // window wrapped around a WinUI dialog, which it was.
            //
            // ⛔ Do not "fix" this to SetBorderAndTitleBar(false, true) to bring the caption back for
            //    dragging: that combination is invalid and throws "Border=false, TitleBar=true" from this
            //    constructor, which is a dialog that never appears at all.
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;

            // On top, because the question has to be answerable: it is raised from a background thread
            // while anything may be in front — the tab-search picker does the same for the same reason,
            // and a dialog the user cannot see is a dialog they cannot answer. Released with the window,
            // which lives only as long as the question does.
            presenter.IsAlwaysOnTop = true;
        }

        // Not an app the user switches to; it belongs to the app that raised it (and Win32 dialogs are not
        // in the taskbar either).
        appWindow.IsShownInSwitchers = false;

        // Provisional, corrected in ApplyLayout once the display's scale can be read.
        appWindow.Resize(new SizeInt32((int)DefaultWidthInEpx, (int)DefaultHeightInEpx));
        CenterOnPrimaryDisplay(appWindow);

        _root.Loaded += (_, _) => OnLoaded();

        // Idempotent, and between them the layout lands whichever of the two arrives with the XamlRoot in
        // place.
        _window.Activated += (_, _) => ApplyLayout();

        _window.Activate();
    }

    /// <summary>The root the dialog is shown on — valid once <see cref="LoadedAsync"/> has completed.</summary>
    public XamlRoot XamlRoot => _root.XamlRoot;

    public Task LoadedAsync() => _loaded.Task;

    /// <summary>
    /// Sizes the window to <paramref name="dialog"/> and makes the dialog a drag handle.
    /// <para>
    /// Sizing to the dialog is the whole point. Earlier versions gave the window a comfortable size and let
    /// the card sit inside it, and the difference was what the user saw as a black ring around the dialog.
    /// The measurement is clamped, because the dialog's template root also carries the smoke layer, which
    /// stretches to any constraint it is given.
    /// </para>
    /// </summary>
    public void FitTo(ContentDialog dialog)
    {
        // After the dialog is laid out, not before: measuring it up front returns the template's root, which
        // carries the smoke layer and stretches to whatever constraint it is given — so that measurement is
        // always the constraint back, not the card. What is wanted is the card's real size, and that only
        // exists once the dialog has been arranged.
        dialog.Opened += (_, _) => FitToCard(dialog);

        // The drag is attached to the dialog rather than to this window's root: the dialog lives in a popup
        // of its own and its pointer events never reach the root element (measured — attaching them there
        // did nothing). handleEventsToo, and never marking them handled, so the dialog's own buttons keep
        // receiving everything.
        _dragSurface = dialog;
        dialog.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        dialog.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        dialog.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
    }

    /// <summary>
    /// Shrinks the window onto the dialog's card, which is what removes the ring around it.
    /// <para>
    /// The card is the largest filled <see cref="Border"/> inside the dialog — the only reliable way to ask
    /// a <c>ContentDialog</c> how big its card came out, since the dialog element itself is stretched to fill
    /// the whole <c>XamlRoot</c>. If nothing filled is found, the window keeps the size it has: a dialog with
    /// a ring around it beats no dialog.
    /// </para>
    /// </summary>
    private void FitToCard(ContentDialog dialog)
    {
        try
        {
            var card = LargestFilledBorder(dialog);
            if (card is null || card.ActualWidth <= 0 || card.ActualHeight <= 0) return;

            _widthInEpx = Math.Min(card.ActualWidth, MaxWidthInEpx);
            _heightInEpx = Math.Min(card.ActualHeight, MaxHeightInEpx);

            ApplyLayout();
        }
        catch (Exception ex)
        {
            StartupLog.Fail("DialogWindow: fit to the card", ex);
        }
    }

    private static Border? LargestFilledBorder(DependencyObject root)
    {
        Border? best = null;
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is Border { Background: not null } border &&
                (best is null || border.ActualWidth * border.ActualHeight > best.ActualWidth * best.ActualHeight))
            {
                best = border;
            }

            if (LargestFilledBorder(child) is { } deeper &&
                (best is null || deeper.ActualWidth * deeper.ActualHeight > best.ActualWidth * best.ActualHeight))
            {
                best = deeper;
            }
        }

        return best;
    }

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
            // The XamlRoot is attached just after Loaded — measured: it is still null inside this handler,
            // while the dialog shown a moment later on that same root works. Laying out on the next turn of
            // the queue is what makes the display's scale readable.
            _root.DispatcherQueue?.TryEnqueue(ApplyLayout);
        }
        catch (Exception ex)
        {
            // Reported, not swallowed: a silently failed layout here means the user gets a dialog in the
            // wrong place or the wrong theme and nothing anywhere says why.
            StartupLog.Fail("DialogWindow: layout", ex);
        }
        finally
        {
            // Released even when the layout failed: a dialog at the provisional size is still usable.
            _loaded.TrySetResult();
        }
    }

    /// <summary>
    /// Sizes, centres and rounds the window once the display's scale is known.
    /// <para>
    /// ⛔ Two attempts at filling or hiding the area around the card were measured and both are worse, so
    /// they are recorded rather than left to be rediscovered:
    /// </para>
    /// <list type="bullet">
    /// <item><c>SystemBackdrop = MicaBackdrop()</c> does not render on a window without a caption, so that
    /// area came out pure black.</item>
    /// <item><c>WindowUtilities.MakeTransparent(handle)</c> — the library's own helper, which its
    /// context-menu window uses — made the entire window disappear, card included.</item>
    /// </list>
    /// </summary>
    private void ApplyLayout()
    {
        if (_root.XamlRoot is not { } xamlRoot) return;

        var appWindow = _window.AppWindow;
        var scale = xamlRoot.RasterizationScale;

        // AppWindow works in physical pixels; the fields are effective pixels.
        appWindow.Resize(new SizeInt32(
            (int)Math.Round(_widthInEpx * scale),
            (int)Math.Round(_heightInEpx * scale)));

        CenterOnPrimaryDisplay(appWindow);

        // Rounded like every other WinUI surface — a borderless window is square without this.
        DesktopWindowsManagerMethods.SetRoundedCorners((nint)WinRT.Interop.WindowNative.GetWindowHandle(_window));
    }

    /// <summary>
    /// The brush a <c>ContentDialog</c> paints its card with, so this window can match it. Falls back to the
    /// window background brush if the SDK renames the key — a slightly different shade is a smaller problem
    /// than no dialog.
    /// </summary>
    private static Brush CardBackground()
    {
        foreach (var key in new[] { "ContentDialogBackground", "SolidBackgroundFillColorBaseBrush" })
        {
            if (Application.Current.Resources.TryGetValue(key, out var found) && found is Brush brush)
            {
                return brush;
            }
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragSurface is not { } surface) return;

        _dragPointer = e.Pointer.PointerId;
        _dragPoint = e.GetCurrentPoint(surface).Position;
        _windowOrigin = _window.AppWindow.Position;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragSurface is not { } surface || _dragPointer != e.Pointer.PointerId) return;

        var position = e.GetCurrentPoint(surface).Position;

        // Only the top of the dialog drags: below it are the buttons and the text, which have to keep
        // behaving like buttons and text.
        if (position.Y > surface.ActualHeight * DragAreaFraction) return;

        _window.AppWindow.Move(new PointInt32(
            _windowOrigin.X + (int)Math.Round(position.X - _dragPoint.X),
            _windowOrigin.Y + (int)Math.Round(position.Y - _dragPoint.Y)));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer == e.Pointer.PointerId) _dragPointer = 0;
    }

    /// <summary>How much of the dialog's top moves the window when dragged. Its header, in practice.</summary>
    private const double DragAreaFraction = 0.25;

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
