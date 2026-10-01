using System;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using ExplorerTabUtility.Abstractions;
using ExplorerTabUtility.App.Pages;
using ExplorerTabUtility.App.Services;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.WinAPI;

namespace ExplorerTabUtility.App;

/// <summary>
/// The settings window. System title bar, Mica backdrop, <see cref="NavigationView"/> navigation.
/// <para>
/// Nothing here draws chrome: window rounding, shadow, title-bar buttons and their hover states
/// are all provided by the OS. The WPF build's ~120 lines of custom chrome, drag/maximise
/// workarounds and title-bar animations are simply gone.
/// </para>
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;
    private bool _exiting;

    /// <summary>
    /// What ends the topmost promotion <see cref="PresentWindow"/> applies.
    /// </summary>
    /// <remarks>
    /// A caller that is about to show a dialog must NOT let the grace-period timer decide: the timer
    /// cannot know how long the user will take to read, and dropping the promotion mid-read loses the
    /// window to a fullscreen game. That choice is expressed here rather than inferred.
    /// </remarks>
    private enum TopmostRelease
    {
        /// <summary>The caller shows a dialog and calls <see cref="ReleaseTopmostNow"/> when it closes.</summary>
        OnDialogDismissed,

        /// <summary>Nothing will be dismissed; the backstop timer ends the promotion.</summary>
        AfterGracePeriod
    }

    /// <summary>How long the window stays promoted to topmost when nothing dismisses it sooner.</summary>
    private static readonly TimeSpan TopmostGracePeriod = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Drops the topmost promotion applied by <see cref="PresentWindow"/>. Created on first use, then
    /// reused — one timer, restarted per request.
    /// </summary>
    private DispatcherTimer? _topmostReleaseTimer;

    /// <summary>
    /// Why the window was last raised, for the release log line. Held on the field rather than captured
    /// by the timer handler, which is created once but must report whichever call armed it last.
    /// </summary>
    private string _topmostReleaseReason = "Window";

    /// <summary>True while the OS colour watcher is attached; guards against a double subscription.</summary>
    private bool _systemThemeWatched;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        StartupLog.Step("MainWindow: InitializeComponent ok");

        Title = LocalizationService.Get("AppTitle");

        ApplyWindowChrome();
        StartupLog.Step("MainWindow: chrome applied");

        ApplySavedTheme();
        StartupLog.Step($"MainWindow: theme mode = {SettingsManager.ThemeMode}");

        LocalizeNavigation();
        WireServices();
        StartupLog.Step("MainWindow: services wired");

        // Navigate only once the NavigationView is loaded. Navigating from the constructor runs
        // before the frame is realised, which leaves the first page blank and — because
        // CurrentSourcePageType is already recorded — makes the guard below skip every later
        // attempt to select that same item. That is why the shortcuts page never appeared.
        Nav.Loaded += OnNavLoaded;
    }

    private bool _initialNavigationDone;
    private bool _initialSizeApplied;

    private void OnNavLoaded(object sender, RoutedEventArgs e)
    {
        // XamlRoot is available from here on, which the DPI-aware sizing needs. Apply it once:
        // Loaded can fire more than once, and re-running this re-issues AppWindow.Resize, which the
        // user sees as the window snapping back to its saved size (AUD-25).
        if (!_initialSizeApplied)
        {
            _initialSizeApplied = true;
            ApplyInitialSize();
        }

        // The presenter's minimum size is in PHYSICAL pixels, so it must be re-computed whenever
        // the rasterization scale changes (window dragged to a monitor with a different DPI, or
        // the system scale setting changes). ApplyInitialSize runs only once (AUD-25), and without
        // this subscription the limits stayed at the STARTUP monitor's scale: a window born on a
        // 200% display kept a 1640px minimum width on a 1080p monitor and could no longer be made
        // narrower (and the reverse direction let the window shrink below the layout's needs).
        // XamlRoot.Changed also fires for non-scale property changes; re-applying is idempotent
        // and cheap, so no change-detection is needed. Subtract first so a repeated Loaded does
        // not stack a second handler (same guard as OnNavSizeChanged below).
        if (Nav.XamlRoot is { } xamlRoot)
        {
            xamlRoot.Changed -= OnXamlRootChanged;
            xamlRoot.Changed += OnXamlRootChanged;
        }

        // Responsive pane: the NavigationView keeps its pane open at any width in "Left" mode, so
        // the collapse has to be driven from here. Subscribed after ApplyInitialSize so the first
        // run of the handler already sees the restored size. Subtract first so a repeated Loaded does
        // not stack a second handler (which would double the pane recomputation) (AUD-25).
        //
        // This block must stay LAST in the method. ApplyInitialSize issues AppWindow.Resize, and the
        // resulting geometry events do not necessarily reach XAML before control returns here — so
        // subscribing and then calling UpdatePaneForWidth eagerly is what makes the FIRST
        // evaluation use a measured width, instead of being fed whatever the previous size happened
        // to be and then never running again (SizeChanged does not re-fire for an unchanged
        // measurement). Getting that wrong leaves a launch at the default size with the pane
        // collapsed and no further event to correct it. The navigation below is deferred to a later
        // dispatcher turn for the same reason: realising a page grows the visual tree and produces
        // the measurement this relies on.
        Nav.SizeChanged -= OnNavSizeChanged;
        Nav.SizeChanged += OnNavSizeChanged;
        UpdatePaneForWidth(Nav.ActualWidth);

        if (_initialNavigationDone) return;
        _initialNavigationDone = true;

        DispatcherQueue.TryEnqueue(() =>
        {
            Nav.SelectedItem = NavShortcuts;
            StartupLog.Step("MainWindow: initial navigation requested");

            // Re-run now that the real visual tree exists: this is the first point at which
            // Nav.ActualWidth reflects the window rather than the pre-layout placeholder.
            UpdatePaneForWidth(Nav.ActualWidth);
        });
    }

    /// <summary>
    /// Width the pane's auto-collapse is <b>delayed</b> by, in effective pixels: the collapse
    /// threshold sits this far above the open-pane floor.
    /// <para>
    /// This is not tuning, it is required for the feature to work at all. The window is clamped at
    /// <see cref="MinWindowWidthExpanded"/> while the pane is open, so a threshold <em>at</em> that
    /// size can never be crossed: the drag stops on the floor first and the pane would never fold no
    /// matter how far the user pulls. The two lines have to be separated, and this is the
    /// separation — collapse a little <em>before</em> the column would be squeezed past its budget,
    /// which is also the friendlier reading of the requirement (the column never has to be defended
    /// at the last pixel).
    /// </para>
    /// <para>
    /// Expressed as one frame border so it scales with DPI rather than being a raw literal, and it
    /// is the smallest value that is unambiguous: the measured content width and the window floor
    /// are each subject to ±1 epx of layout rounding, and a band narrower than that could be
    /// straddled by the two readings disagreeing. For the same reason it must stay well under
    /// <c>MinWindowWidthExpanded − MinWindowWidthCollapsed</c>, so the folded window is always
    /// deeper than the band (192 vs 16 here).
    /// </para>
    /// </summary>
    private static double PaneCollapseHysteresis => FrameBorderWidth;

    /// <summary>
    /// Width below which the pane is collapsed automatically, <b>in window terms</b> — kept in this
    /// unit so it can be compared with the floors directly, and converted to measured content width
    /// only inside <see cref="UpdatePaneForWidth"/>.
    /// <para>
    /// <c>MinWindowWidthExpanded + PaneCollapseHysteresis = 877 + 16 = 893</c>. Above this the pane
    /// is welcome, below it the pane must go; the band between the two lines is the dead zone that
    /// keeps a drag across the boundary from ringing.
    /// </para>
    /// </summary>
    private static double PaneCollapseWindowWidth =>
        MinWindowWidthExpanded + PaneCollapseHysteresis;

    /// <summary>
    /// <see cref="PaneCollapseWindowWidth"/> in the unit <see cref="OnPaneToggleClick"/> has to hand
    /// it: measured XAML content width. Only used there — <see cref="UpdatePaneForWidth"/> works in
    /// window terms throughout.
    /// <para>
    /// The extra <c>OpenPaneOverhead</c> is not a fudge: it is the pane's own 241 epx, which sits
    /// inside the client area, so it has to be charged again when a <em>client</em> width is used to
    /// express a <em>window</em> size. Algebraically this reduces to
    /// <c>ContentMinWidth + OpenPaneOverhead + PaneCollapseHysteresis = 620 + 257 + 16 = 893</c> —
    /// the same number, which is the sanity check that the conversion is honest.
    /// </para>
    /// </summary>
    private static double PaneCollapseContentWidth =>
        PaneCollapseWindowWidth - FrameBorderWidth + OpenPaneOverhead;

    /// <summary>True when the pane was closed by <see cref="UpdatePaneForWidth"/> rather than by the user.</summary>
    private bool _paneAutoCollapsed;

    /// <summary>
    /// True when the user closed the pane <b>by hand</b>, so the width rule must not re-open it — not
    /// even when the window becomes wide enough. Cleared only by <see cref="OnPaneToggleClick"/>, i.e.
    /// by the next manual toggle.
    /// <para>
    /// Without it, widening across the threshold would spring the pane back open on a user who had
    /// deliberately hidden it. The subtlety is that "the pane is closed" and "the user closed the
    /// pane" are not the same fact: <see cref="UpdatePaneForWidth"/> folds the pane itself whenever
    /// the window gets narrow, and that fold <em>must</em> be reversible. So this flag is set only
    /// by the collapse branch when <see cref="_paneAutoCollapsed"/> is clear — i.e. when the closed
    /// pane cannot be explained by our own earlier fold. Setting it for every closed pane would pin
    /// the pane shut for the rest of the session after a single auto-fold, which is exactly the
    /// "auto-collapse后无法自动展开" regression.
    /// </para>
    /// </summary>
    private bool _paneUserClosed;

    /// <summary>
    /// True when the user opened the pane by hand on a window that is still too narrow, so the
    /// width rule must not close it again on the next resize.
    /// <para>
    /// Cleared by <see cref="OnPaneToggleClick"/> on any manual toggle, and by
    /// <see cref="UpdatePaneForWidth"/> as soon as the width reaches
    /// <see cref="PaneCollapseWidth"/> (the pane is legitimately open there, so the override has
    /// done its job).
    /// </para>
    /// </summary>
    private bool _paneUserOverride;

    /// <summary>
    /// Set while <see cref="UpdatePaneForWidth"/> is changing the pane state and re-installing the
    /// window floor, so the geometry events that change produces cannot be misread as a fresh width
    /// transition.
    /// <para>
    /// The transitions are arranged so that the floor install is never above the current window
    /// (see the box on <see cref="UpdatePaneForWidth"/>), which means the OS has nothing to correct
    /// and this guard should never actually fire. It is kept because it is the difference between
    /// "cannot happen today" and "cannot recurse if the thresholds are retuned" — a re-entrant
    /// event would otherwise flip the pane back and forth with the window floor.
    /// </para>
    /// </summary>
    private bool _paneTransitionInProgress;

    /// <summary>
    /// False until the first <see cref="UpdatePaneForWidth"/> call has installed the floor for the
    /// pane state that is actually in effect at startup — covers a pane opened from settings, which
    /// never goes through <see cref="OnPaneToggleClick"/>.
    /// </summary>
    private bool _paneOpenStateKnown;

    private void OnNavSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePaneForWidth(e.NewSize.Width);

    /// <summary>
    /// Keeps the pane from eating the settings column on a narrow window, and puts it back when there
    /// is room again.
    /// <para>
    /// The argument is measured XAML content width, but the decision is <b>about the window</b>: it
    /// is the window that the presenter clamps, so the comparison is done in window terms rather
    /// than mixing units. <c>windowEstimate = w + FrameBorderWidth</c> is exact while the pane is
    /// open, which is the only case that reads it.
    /// </para>
    /// <code>
    /// windowEstimate = width + FrameBorderWidth
    /// fold   when  windowEstimate &lt;  PaneCollapseWindowWidth            (877 + 16 = 893)
    /// unfold when  windowEstimate ≥  PaneCollapseWindowWidth            (same line)
    /// floor  installed by fold   = MinWindowWidthCollapsed   (685)
    /// floor  installed by unfold = MinWindowWidthExpanded    (877)
    /// </code>
    /// <para>
    /// A manual toggle always wins: if the user closed the pane themselves the width never re-opens
    /// it, and if they opened it on a narrow window it stays open until the width crosses the
    /// threshold again. The manual-open case is what <see cref="_paneUserOverride"/> records —
    /// without it the collapse branch below would slam the pane shut again on the very next
    /// resize, even a one-pixel one, which is exactly the "手动打开保不住" regression.
    /// </para>
    /// <para>
    /// <b>Why there is no oscillation.</b> Three numbers have to be kept apart, and the whole design
    /// is the ordering between them: the collapse threshold sits <em>above</em> the open floor
    /// (that is <see cref="PaneCollapseHysteresis"/>), and the open floor sits <em>far above</em> the
    /// collapsed floor. That gives each transition a direction the other cannot undo:
    /// <list type="bullet">
    /// <item><b>Fold.</b> Triggered by the user pulling the window in, at 893 — which is <em>above</em>
    /// the 877 floor, so the drag can actually reach it. It then installs the much lower
    /// <see cref="MinWindowWidthCollapsed"/> (685), so the fold <em>releases</em> room instead of
    /// asking for it; the OS has nothing to correct and emits no event.</item>
    /// <item><b>Unfold.</b> Only taken at 893 or above, on a window that already clears the
    /// <see cref="MinWindowWidthExpanded"/> floor being re-installed. Again nothing is corrected.
    /// Had the rule instead expanded first and let the floor grow the window, the growth would fire
    /// a resize, the resize would fall under the threshold, and the pane would fold and unfold
    /// forever — the loop this ordering exists to prevent.</item>
    /// <item>The 16 epx band between 877 and 893 is a genuine dead zone, not slack: inside it the
    /// pane stays in whatever state it already had. Without the band the threshold would sit exactly
    /// on the floor the presenter clamps the window to, and the drag would be stopped by the clamp
    /// before it could ever cross — the pane would simply never auto-fold. It is also why the two
    /// values must never be collapsed back into one constant.</item>
    /// <item>Because neither transition can produce the event that reverses it, the state is stable
    /// at every width. Verified by measurement: four round trips through the boundary produce
    /// identical widths every time, with no change after settling.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The pane state is also re-applied to the floor on the first event after load
    /// (<see cref="_paneOpenStateKnown"/>), so a pane opened from settings — which never goes
    /// through <see cref="OnPaneToggleClick"/> — still gets the open floor.
    /// </para>
    /// </summary>
    private void UpdatePaneForWidth(double width)
    {
        if (width <= 0) return;

        // Derive the frame border from the live window before anything reads FrameBorderWidth, so the
        // floors and the collapse threshold are computed from a measurement rather than a constant.
        ObserveFrameBorder(width);

        if (!_paneOpenStateKnown)
        {
            _paneOpenStateKnown = true;
            ApplyPaneMinSize();
        }

        var windowEstimate = width + FrameBorderWidth;

        if (windowEstimate < PaneCollapseWindowWidth)
        {
            // Defensive: this event was caused by the floor correction that went with a transition
            // we initiated, not by the user. Cannot fire with the current arrangement, kept so a
            // retuned threshold cannot turn into a re-entrant loop.
            if (_paneTransitionInProgress) return;

            // A window still too narrow for the collapsed layout cannot be folded into it: the fold
            // would install a floor the window does not satisfy and the OS would grow it straight
            // back. Staying open is the self-consistent reading — the open floor is what the window
            // is already held to.
            if (windowEstimate < MinWindowWidthCollapsed) return;

            // Respect a manual open at this (too narrow) width until the width crosses the
            // threshold again. Without this guard any later resize would undo the manual toggle,
            // because the flag is only read in the widen branch.
            if (_paneUserOverride) return;

            // Already in the auto-collapsed state — nothing to do, and re-setting IsPaneOpen would
            // only churn the visual tree.
            if (_paneAutoCollapsed) return;

            // The user closed the pane themselves: leave it closed, and do not claim the state as
            // "auto" — otherwise widening would re-open a pane the user deliberately hid.
            //
            // The two ways a closed pane can arrive here mean opposite things and must not be
            // conflated. If we folded it ourselves a moment ago the flag is already set and this
            // branch is unreachable; reaching it with the flag *clear* is therefore "the user
            // closed it by hand", and that intent has to outlive later widening — otherwise a
            // deliberate close would silently spring back open the next time the window crossed
            // the threshold. Recording it only here (never for a pane that is simply already closed
            // for our own reasons) is what keeps a manual close sticky without also pinning shut a
            // pane that only folded because the window got narrow.
            if (!Nav.IsPaneOpen)
            {
                _paneUserClosed = true;
                return;
            }

            _paneTransitionInProgress = true;
            _paneAutoCollapsed = true;
            Nav.IsPaneOpen = false;
            ApplyPaneMinSize();
            _paneTransitionInProgress = false;
            return;
        }

        // Wide enough: the manual override has served its purpose and the pane is legitimately open.
        _paneUserOverride = false;

        // Only an auto-fold is ours to undo. A pane the user closed by hand stays closed no matter
        // how wide the window gets; a pane that was never open in the first place (startup on a
        // narrow window, or a pane the user opened and closed before this ran) is likewise not ours
        // to re-open. `_paneAutoCollapsed` is exactly the "we folded it" bit, so testing it first
        // makes the widen branch idempotent — it can only ever reverse our own action.
        if (!_paneAutoCollapsed) return;
        if (_paneUserClosed) return;

        // Defensive: ignored if it is our own floor correction producing this event.
        if (_paneTransitionInProgress) return;

        _paneTransitionInProgress = true;
        _paneAutoCollapsed = false;
        Nav.IsPaneOpen = true;
        ApplyPaneMinSize();
        _paneTransitionInProgress = false;
    }

    private void ApplyWindowChrome()
    {
        try
        {
            AppWindow.SetIcon(App.IconPath);
        }
        catch
        {
            // Missing icon file is not fatal.
        }

        // Mica is the Windows 11 system material. On Windows 10 it is unsupported and the window
        // keeps the default opaque background — no fallback drawing needed.
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();

        ApplyCustomTitleBar();
    }

    /// <summary>
    /// Replaces the stock title bar row with the app's own, so the pane toggle, the app name and the
    /// system-drawn minimise/maximise/close buttons sit on one row.
    /// <para>
    /// <c>ExtendsContentIntoTitleBar</c> hands the top strip of the window to XAML, while
    /// <c>AppWindow.TitleBar</c> keeps the caption buttons — those are never redrawn here, which is
    /// what keeps this consistent with "no custom chrome".
    /// </para>
    /// </summary>
    private void ApplyCustomTitleBar()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBar);

            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

            TitleText.Text = LocalizationService.Get("AppTitle");

            ToolTipService.SetToolTip(BtnPaneToggle, LocalizationService.Get("TogglePane"));
            AutomationProperties.SetName(BtnPaneToggle, LocalizationService.Get("TogglePane"));

            StartupLog.Step("MainWindow: custom title bar applied");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("MainWindow.ApplyCustomTitleBar", ex);
        }
    }

    /// <summary>
    /// Applies the saved theme to the XAML root.
    /// <para>
    /// Nothing did this at launch before: the theme was only ever applied inside the preferences
    /// picker's change handler, so a saved Dark preference was ignored on every start — the window
    /// came up in the system theme while the picker still read "Dark". Both paths now go through
    /// this single method.
    /// </para>
    /// <para>
    /// Set on the root element rather than <c>Application.RequestedTheme</c>, which may only be
    /// assigned before the first window is created.
    /// </para>
    /// </summary>
    public void ApplySavedTheme()
    {
        if (Content is not FrameworkElement root) return;

        root.RequestedTheme = SettingsManager.ThemeMode switch
        {
            1 => ElementTheme.Dark,
            2 => ElementTheme.Light,
            // "Follow system": resolve the OS app theme explicitly. ElementTheme.Default would
            // instead inherit Application.RequestedTheme, which WinUI 3 desktop does not tie to
            // the OS — so it fell through to Light. SystemTheme keeps this and the application
            // path from drifting apart.
            _ => SystemTheme.IsDark() ? ElementTheme.Dark : ElementTheme.Light
        };

        ApplyTitleBarColors();
    }

    /// <summary>
    /// Repaints the system-drawn caption buttons for the theme that is actually in effect.
    /// <para>
    /// Windows colours them from <em>its own</em> app theme. This app can override that theme, and
    /// <see cref="ApplyCustomTitleBar"/> makes the button backgrounds transparent — so a light window
    /// on a dark system (or vice versa) ends up with glyphs that do not contrast with the bar.
    /// Setting the foreground explicitly is the documented way to keep them legible; the buttons
    /// themselves are still drawn by the system.
    /// </para>
    /// </summary>
    private void ApplyTitleBarColors()
    {
        var dark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;

        // Fluent caption colours: full-strength glyph, dimmed when the window is inactive, and the
        // standard subtle overlays for hover/pressed.
        var foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var inactive = Windows.UI.Color.FromArgb(0x66, dark ? (byte)0xFF : (byte)0x00,
                                                       dark ? (byte)0xFF : (byte)0x00,
                                                       dark ? (byte)0xFF : (byte)0x00);
        var hover = dark
            ? Windows.UI.Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
        var pressed = dark
            ? Windows.UI.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x17, 0x00, 0x00, 0x00);

        var bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = inactive;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonPressedForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = pressed;
    }

    /// <summary>
    /// Starts following the OS theme so a "Follow system" window tracks a live theme switch.
    /// <para>
    /// <see cref="SystemTheme.Changed"/> fires on a background thread, so the work is marshalled
    /// onto the UI thread in <see cref="OnSystemThemeChanged"/>. Subscribed exactly once and only
    /// from <see cref="WireServices"/>, which runs once per window.
    /// </para>
    /// </summary>
    private void StartSystemThemeWatcher()
    {
        if (_systemThemeWatched) return;

        try
        {
            SystemTheme.Changed += OnSystemThemeChanged;
            _systemThemeWatched = true;
            StartupLog.Step("MainWindow: system theme watcher started");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("MainWindow.StartSystemThemeWatcher", ex);
        }
    }

    /// <summary>
    /// Detaches the OS theme watcher. Called on a real exit only — closing to the tray keeps the
    /// window (and the watcher) alive.
    /// </summary>
    private void StopSystemThemeWatcher()
    {
        if (!_systemThemeWatched) return;
        _systemThemeWatched = false;

        try
        {
            SystemTheme.Changed -= OnSystemThemeChanged;
        }
        catch
        {
            // Detaching from a static event must never break shutdown.
        }
    }

    /// <summary>
    /// Re-applies the theme when the OS changes it, but only while the user is on "Follow system".
    /// An explicit Dark/Light choice must survive a system theme change untouched.
    /// <para>
    /// Raised on a background thread, so everything that touches XAML happens after the dispatcher
    /// hop, never in this callback.
    /// </para>
    /// </summary>
    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        _services.Dispatcher.TryPost(() =>
        {
            if (SettingsManager.ThemeMode != 0) return;
            ApplySavedTheme();
        });
    }

    /// <summary>
    /// Re-localizes the shell after a language change: the navigation labels plus the current page,
    /// whose strings are applied in its own <c>Localize</c> pass during <c>OnNavigatedTo</c>.
    /// <para>
    /// The page is re-navigated <b>with the services instance</b>. Pages take their <c>_services</c>
    /// from the navigation parameter, so navigating with <c>null</c> left them without it — and a
    /// page that bails out on a null parameter keeps whichever strings were applied before the
    /// re-navigation. That is how the "Hide tray icon" row ended up showing its plain tooltip
    /// instead of the disabled-state badge after a language switch.
    /// </para>
    /// </summary>
    public void ReloadForLanguage()
    {
        LocalizeNavigation();

        var pageType = ContentFrame.CurrentSourcePageType;
        if (pageType is null) return;

        ContentFrame.Navigate(pageType, _services, new SuppressNavigationTransitionInfo());
    }

    /// <summary>
    /// The shared services, so a page can recover them when it is navigated to without a parameter.
    /// </summary>
    internal AppServices Services => _services;

    /// <summary>
    /// Answers a second launch.
    /// <para>
    /// The second process owns no window and no <c>XamlRoot</c>, so it cannot present anything
    /// itself — it signals <see cref="Program.ShowRequestEvent"/> and this instance, which does have
    /// a window, shows a real WinUI dialog. The previous build popped a raw Win32 message box from
    /// the second process instead.
    /// </para>
    /// </summary>
    private void ListenForSecondInstance()
    {
        var handle = Program.ShowRequestEvent;
        if (handle is null) return;

        _ = Task.Run(() =>
        {
            try
            {
                while (handle.WaitOne())
                    _services.Dispatcher.TryPost(OnSecondInstanceRequested);
            }
            catch (Exception ex)
            {
                StartupLog.Fail("MainWindow.ListenForSecondInstance", ex);
            }
        });
    }

    private async void OnSecondInstanceRequested()
    {
        try
        {
            // Raise the window on both paths; both end in ForceToTop. The release policy differs by what
            // happens next, not by how the window was surfaced:
            //   visible -> raise, then show the notice   (the caller releases when it is dismissed)
            //   hidden  -> un-hide and raise, then show the notice
            // The visible case is the one the user reported: the window was perfectly visible, just
            // buried under a fullscreen game, so the notice rendered inside a window nobody could see.
            // Visibility is not the question here — being on top is.
            //
            // ShowFromTray() is deliberately not used on the visible path: it calls AppWindow.Show(),
            // and this window may not be hidden at all. UnhideAndPresent() does the part that matters
            // (restore from the tray only where it is needed) and logs honestly which case it was.
            //
            // Both paths deliberately use OnDialogDismissed. The grace-period timer must not arbitrate
            // here, because a dialog IS about to be shown: on a fullscreen game, dropping the promotion
            // while the user is still reading hands the top of the Z order back to the game and the
            // notice disappears (~2.5s, measured 3/3).
            if (AppWindow.IsVisible)
                PresentWindow("SecondInstance", TopmostRelease.OnDialogDismissed);
            else
                UnhideAndPresent("SecondInstance", TopmostRelease.OnDialogDismissed);

            await _services.Dialogs.ShowAsync(
                LocalizationService.Get("AlreadyRunning"),
                LocalizationService.Get("AppTitle"),
                DialogButton.OK,
                DialogIcon.Information);
        }
        catch (Exception ex)
        {
            StartupLog.Fail("MainWindow.OnSecondInstanceRequested", ex);
        }
        finally
        {
            // The window only needed the front so this notice would be seen, so give the promotion up
            // as soon as the notice is gone. In `finally` rather than after the await: OnDialogDismissed
            // means the backstop timer is NOT armed on this path, so if ShowAsync throws, nothing else
            // would ever release it and the window would stay pinned above everything — the exact
            // outcome ReleaseTopmostNow exists to prevent. Safe when nothing was promoted.
            ReleaseTopmostNow();
        }
    }

    /// <summary>
    /// Handles the title-bar pane toggle.
    /// <para>
    /// A manual toggle is the authority on the pane's intended state, so it resets all three bits of
    /// the responsive rule at once: the fold is no longer "ours" (<see cref="_paneAutoCollapsed"/>),
    /// and the user's own choice is recorded in whichever direction it went —
    /// <see cref="_paneUserClosed"/> for a close, <see cref="_paneUserOverride"/> for a close-following
    /// re-open on a window too narrow to hold the pane normally.
    /// </para>
    /// <para>
    /// The floors are re-applied here because this is the one pane transition that does not go
    /// through <see cref="UpdatePaneForWidth"/>; without it the open pane would take its 257 epx out
    /// of the window budget on a wide window that still had only the collapsed floor installed.
    /// Note that re-applying the open floor here is also what lifts a narrow window back up to
    /// <see cref="MinWindowWidthExpanded"/> when the user forces the pane open — which is the
    /// behaviour the override flag then protects from being undone on the next resize.
    /// </para>
    /// </summary>
    private void OnPaneToggleClick(object sender, RoutedEventArgs e)
    {
        Nav.IsPaneOpen = !Nav.IsPaneOpen;

        // An explicit toggle overrides the responsive rule — otherwise the next resize would
        // immediately undo what the user just asked for.
        _paneAutoCollapsed = false;

        // Same unit reconciliation as UpdatePaneForWidth: the response the user is opting out of is
        // driven by content width, so the override flag is recorded against the content-width form
        // of the threshold.
        _paneUserOverride = Nav.IsPaneOpen && Nav.ActualWidth < PaneCollapseContentWidth;

        // A manual close is sticky too: widening must not re-open a pane the user hid. Recorded
        // explicitly rather than inferred from "not auto-collapsed", so the rule holds regardless of
        // the order in which the resize and the toggle happened.
        _paneUserClosed = !Nav.IsPaneOpen;

        ApplyPaneMinSize();
    }

    /// <summary>
    /// Restores the saved window size.
    /// <para>
    /// <c>FormSize</c> is stored in <b>effective</b> (XAML) pixels as the <b>outer window size</b>,
    /// matching the original WPF build's <c>System.Windows.Size</c> semantics — and matching what
    /// <c>AppWindow.Resize</c> takes, which is the outer size in <b>physical</b> pixels, hence the
    /// scaling below. <see cref="SaveWindowSize"/> writes the same unit back, so a save/restore cycle is
    /// a fixed point.
    /// </para>
    /// <para>
    /// <b>What is stored is discarded exactly once</b>, guarded by <c>SettingsManager.FormSizeMigrated</c>.
    /// Files written before the unit was settled hold a <b>client-area</b> size (one frame border smaller,
    /// and one title bar smaller again in height), and the build that first tried to recover from that did
    /// so with a size threshold — which turned out to be undecidable: a legitimately stored window sitting
    /// at its own minimum (874.7 epx at 175%, where the threshold evaluated to 877) was misread as a
    /// pre-migration value and replaced with the default on <em>every</em> launch. A value in the wrong
    /// unit simply cannot be told apart from a deliberate one by looking at it, so the stored value is
    /// dropped once and then trusted: the user resizes once, and from then on the size is a fixed point.
    /// </para>
    /// <para>
    /// Runs from <c>Nav.Loaded</c>: <c>XamlRoot</c> (and therefore the rasterization scale) is not
    /// available in the constructor.
    /// </para>
    /// </summary>
    private void ApplyInitialSize()
    {
        var scale = Nav.XamlRoot?.RasterizationScale ?? 1.0;

        if (!SettingsManager.FormSizeMigrated)
        {
            var stored = SettingsManager.FormSize;
            var invalid = stored.Width <= 0 || stored.Height <= 0;

            StartupLog.Step(invalid
                ? "size: no stored size, using the default"
                : $"size: stored {stored.Width:F0}x{stored.Height:F0} discarded once (pre-migration unit)");

            SettingsManager.FormSizeMigrated = true;
            SettingsManager.FormSize = WindowSize.Default;

            // Flushed immediately, not left to the 500 ms debounce: if the process died before the flag
            // reached disk, the next launch would discard the value the user had just saved.
            SettingsManager.ForceSave();
        }

        var size = SettingsManager.FormSize;
        var usable = size.IsValid;

        var logicalWidth = usable ? size.Width : 1130;
        var logicalHeight = usable ? size.Height : 600;

        var targetWidth = (int)Math.Round(logicalWidth * scale);
        var targetHeight = (int)Math.Round(logicalHeight * scale);

        AppWindow.Resize(new SizeInt32(targetWidth, targetHeight));

        StartupLog.Step(
            $"size: form={logicalWidth:F0}x{logicalHeight:F0} scale={scale:F3} " +
            $"target={targetWidth}x{targetHeight} actual={AppWindow.Size.Width}x{AppWindow.Size.Height}");

        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;

        // WinUI's Window has no MinWidth/MinHeight; the presenter does. Applied after the Resize so
        // the floor cannot interfere with restoring the saved size.
        ApplyPresenterMinSize(presenter, scale, Nav.IsPaneOpen);
    }

    /// <summary>Recomputes the minimum size for the CURRENT scale AND the current pane state.</summary>
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            ApplyPresenterMinSize(presenter, sender.RasterizationScale, Nav.IsPaneOpen);
    }

    /// <summary>
    /// The width the settings column (the <c>Frame</c> inside the <c>NavigationView</c>) is never
    /// allowed to drop below, in effective pixels.
    /// <para>
    /// This is the user-facing requirement: <b>opening the pane must not eat into this budget</b>.
    /// The settings column therefore renders every row untruncated at this width, in whichever
    /// layout the settings toolkit picks for the available space. Measured at 100% scale on the
    /// shortcuts page with a profile card expanded, the widest fixed element — the 180 epx value
    /// controls plus 2×16 card padding plus 2×36 column margin — still fits at this width, and one
    /// step narrower the value controls start clipping against the card's right edge.
    /// </para>
    /// <para>
    /// It is enforced structurally rather than by leaving slack: <see cref="MinWindowWidthExpanded"/>
    /// and <see cref="MinWindowWidthCollapsed"/> are both derived from it, and
    /// <see cref="UpdatePaneForWidth"/> folds the pane away as soon as the window can no longer
    /// afford the open pane on top of it.
    /// </para>
    /// </summary>
    private const double ContentMinWidth = 620;

    /// <summary>
    /// Width the pane occupies when it is collapsed, in effective pixels — the icon-only strip.
    /// <para>
    /// A WinUI toolkit constant rather than a number this app chooses; it is named here so
    /// <see cref="MinWindowWidthCollapsed"/> is a formula instead of a magic 668. If a future
    /// toolkit bump changes the strip width, the collapsed floor follows automatically.
    /// </para>
    /// </summary>
    private const double CollapsedPaneWidth = 48;

    /// <summary>
    /// Non-client frame borders at 100% scale — the difference between <c>AppWindow.Size.Width</c> and
    /// the client area, measured on a window reporting 1400 with a 1384-wide content site.
    /// <para>
    /// Only a fallback for <see cref="FrameBorderWidth"/> until the real value has been observed. It is
    /// deliberately <b>not</b> treated as a fixed physical-pixel quantity: Windows sizes the frame with
    /// the DPI, so expressing it in physical pixels is wrong at every scale but 100%.
    /// </para>
    /// </summary>
    private const double FrameBorderFallbackEpx = 16;

    /// <summary>
    /// Frame borders in effective pixels, as measured on this window — see
    /// <see cref="ObserveFrameBorder"/>.
    /// </summary>
    private static double _observedFrameBorderEpx;

    /// <summary>True once the measurement above has been taken, so it is taken exactly once.</summary>
    private static bool _frameBorderObserved;

    /// <summary>
    /// Frame borders in effective pixels: the measured value once known, the 100%-scale fallback before
    /// that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured at 175% on this machine (2026-10-01): ≈13.7 epx (≈24 physical px)</b>, not the
    /// 9.14 epx that <c>16 / 1.75</c> predicts. With the old physical-pixel constant the floors were
    /// derived from a frame border ~4.6 epx too small, so the settings column landed ~5 epx short of
    /// the <see cref="ContentMinWidth"/> budget at that scale (window 1523, <c>Nav.ActualWidth</c> 856,
    /// pane collapsed ⇒ column ≈615). Re-deriving the floors from the measured value makes the budget
    /// exact at any scale.
    /// </para>
    /// </remarks>
    private static double FrameBorderWidth =>
        _observedFrameBorderEpx > 0 ? _observedFrameBorderEpx : FrameBorderFallbackEpx;

    /// <summary>
    /// Takes the frame border from the live window: <c>AppWindow.Size.Width / scale</c> is the outer
    /// width in effective pixels and <paramref name="clientWidth"/> is the client width, so their
    /// difference <em>is</em> the frame border, on any DPI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs from the top of <see cref="UpdatePaneForWidth"/> — that is the one place where both numbers
    /// are known to come from the same settled layout pass, and it runs before any floor is derived.
    /// </para>
    /// <para>
    /// One-shot by design: Windows scales the frame with the DPI, so the value in <em>effective</em>
    /// pixels is DPI-invariant and a single observation holds for the life of the window. The value is
    /// only accepted inside a sanity band (a non-client frame is a few pixels in every theme), which
    /// rejects a reading taken while the window is maximised or mid-resize — where the two
    /// measurements can disagree for reasons that have nothing to do with the frame.
    /// </para>
    /// </remarks>
    private void ObserveFrameBorder(double clientWidth)
    {
        if (_frameBorderObserved || clientWidth <= 0) return;

        if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;

        var scale = _rasterizationScale > 0 ? _rasterizationScale : 1.0;
        var candidate = AppWindow.Size.Width / scale - clientWidth;

        if (candidate is <= 2 or >= 48) return;

        _observedFrameBorderEpx = candidate;
        _frameBorderObserved = true;

        StartupLog.Step(
            $"frame: measured {candidate:F2} epx (window {AppWindow.Size.Width} @ scale {scale:F3}, " +
            $"Nav={clientWidth:F1}) → floors {MinWindowWidthExpanded:F1}/{MinWindowWidthCollapsed:F1} epx");

        // The floors already installed by ApplyInitialSize used the fallback, so re-install them once
        // with the measured value. Cannot re-enter ObserveFrameBorder: the flag above is already set.
        ApplyPaneMinSize();
    }

    /// <summary>
    /// One effective pixel of separator the <c>NavigationView</c> draws between the pane and the
    /// content area.
    /// <para>
    /// Measured from the UIA geometry: with the pane open the settings column starts at client
    /// x=241 while the pane is 240 wide, so the column gets <c>clientWidth − 240 − 1</c>. It has to
    /// be part of the floor or the column lands one pixel short of <see cref="ContentMinWidth"/> at
    /// the minimum window size — a one-pixel shortfall, but the requirement is a hard floor and this
    /// is what makes it exact.
    /// </para>
    /// </summary>
    private const double PaneContentSeparatorWidth = 1;

    /// <summary>
    /// Everything the <b>open</b> pane takes out of the window that the content column does not get:
    /// the frame border, the pane itself, and the separator between them.
    /// <code>16 + 240 + 1 = 257</code>
    /// <para>
    /// Measured, and constant across every window size: with the pane open the column reports
    /// <c>window − 257</c> at 1200, 1150 and 880 alike.
    /// </para>
    /// </summary>
    private static double OpenPaneOverhead =>
        FrameBorderWidth + OpenPaneLength + PaneContentSeparatorWidth;

    /// <summary>
    /// Everything the <b>collapsed</b> pane leaves out of the column: the frame border plus the
    /// icon strip.
    /// <code>16 + 48 = 64</code>
    /// <para>
    /// Note the separator term disappears: the collapsed strip is measured to sit flush, with the
    /// column reporting <c>window − 65</c> rather than <c>window − 64</c> — the extra pixel is a
    /// rounding of the strip's own measured width, which is why
    /// <see cref="MinWindowWidthCollapsed"/> carries one pixel of slack (see there). Also measured
    /// constant: 861→796, 700→635, 668→603.
    /// </para>
    /// </summary>
    private static double CollapsedPaneOverhead => FrameBorderWidth + CollapsedPaneWidth;

    /// <summary>
    /// Window floor while the pane is <b>open</b>, in effective pixels: the content budget plus
    /// everything the open pane takes. <c>620 + 257 = 877</c> — the column then reports exactly
    /// <see cref="ContentMinWidth"/>.
    /// <para>
    /// This is the value handed to <see cref="OverlappedPresenter.PreferredMinimumWidth"/> while the
    /// pane is open, and it is defined here (rather than built inline) so
    /// <see cref="PaneCollapseWidth"/> and the pane state machine all read from one place.
    /// </para>
    /// </summary>
    private static double MinWindowWidthExpanded => ContentMinWidth + OpenPaneOverhead;

    /// <summary>
    /// Window floor while the pane is <b>collapsed</b>, in effective pixels: the content budget plus
    /// the collapsed overhead, plus one pixel of slack. <c>620 + 64 + 1 = 685</c>; the column then
    /// reports 620.
    /// <para>
    /// The extra pixel is deliberate and is not padding for its own sake: the collapsed strip
    /// measures 48 but costs the column 49 (see <see cref="CollapsedPaneOverhead"/>), and the
    /// requirement is a hard floor, so the rounding is absorbed here rather than left as a
    /// one-pixel shortfall. Note the collapsed floor is comfortably below
    /// <see cref="MinWindowWidthExpanded"/>, which is what makes the window genuinely shrinkable
    /// once the pane has folded away — without it the fold would free the pane's width inside the
    /// layout but the window itself would stay clamped.
    /// </para>
    /// </summary>
    private static double MinWindowWidthCollapsed => ContentMinWidth + CollapsedPaneOverhead + 1;

    /// <summary>
    /// Height floor, in effective pixels.
    /// <para>
    /// Independent of the pane: the <c>NavigationView</c> scrolls its menu vertically and no layout
    /// mode keys off height, so the width work above leaves this untouched. 520 keeps the page
    /// header, the first settings card and its action row on screen at 100% scale without the
    /// content column needing to scroll to reach the first control.
    /// </para>
    /// </summary>
    private const double MinWindowHeight = 520;

    /// <summary>
    /// The XAML pane length declared in <c>MainWindow.xaml</c> (<c>OpenPaneLength</c>), mirrored
    /// here so the window floors can be derived from it in code. Keep the two in sync: the collapse
    /// threshold and the open-pane floor both assume the pane is this wide.
    /// </summary>
    private const double OpenPaneLength = 240;

    /// <summary>
    /// The rasterization scale most recently observed from the <c>XamlRoot</c>, used to convert the
    /// DPI-independent floors above into the physical pixels <c>PreferredMinimumWidth</c> wants.
    /// Refreshed by <see cref="ApplyPresenterMinSize"/>, which is the only writer and runs on both
    /// startup and every <c>XamlRoot.Changed</c>.
    /// <para>
    /// Static because <see cref="ApplyPresenterMinSize"/> is static; every window in this app is
    /// created on the same UI thread and shares one <c>XamlRoot</c>, so a single slot is sufficient.
    /// </para>
    /// </summary>
    private static double _rasterizationScale = 1.0;

    /// <summary>
    /// Applies the DPI-dependent window size limits for the <b>current</b> pane state.
    /// <para>
    /// <see cref="OverlappedPresenter.PreferredMinimumWidth"/>/<c>Height</c> are in <b>physical</b>
    /// pixels while the floors above are in effective pixels, so the values must be scaled — and
    /// re-scaled on every rasterization-scale change (see <see cref="OnXamlRootChanged"/>). There is
    /// deliberately no maximum: the only clamped direction is "too small for the settings column",
    /// the screen already bounds "too large".
    /// </para>
    /// <para>
    /// <b>The floors are outer-window sizes.</b> Windows enforces <c>PreferredMinimumWidth</c> against
    /// <c>AppWindow.Size</c>, not against the client area: with the value set to 860 the window stops
    /// with <c>AppWindow.Size.Width</c> at 860 while <c>Nav.ActualWidth</c> reads 844 — and the same
    /// relationship is visible on the live window (<c>1523 = round(870.14 × 1.75)</c> at 175%, with
    /// <c>Nav.ActualWidth</c> 856). <see cref="UpdatePaneForWidth"/> therefore compares in the same
    /// unit — <c>Nav.ActualWidth + FrameBorderWidth</c> reconstructs the outer width — and
    /// <see cref="PaneCollapseWidth"/> is <see cref="MinWindowWidthExpanded"/> verbatim, with no
    /// frame-border correction folded in.
    /// </para>
    /// <para>
    /// Subtracting <see cref="FrameBorderWidth"/> here would make the window stop <em>sooner</em> than
    /// the pane rule expects: the pane could then never be collapsed or re-opened by a drag, because the
    /// window can no longer reach the sizes those branches require. If a future SDK changes the
    /// measurement, this is the line to re-derive.
    /// </para>
    /// <para>
    /// The floor is <b>dynamic</b>: it has to follow the pane, or the two requirements would fight
    /// each other. With the pane open the window must stay at
    /// <see cref="MinWindowWidthExpanded"/> (the pane would otherwise eat into
    /// <see cref="ContentMinWidth"/>); with it collapsed the window may go down to
    /// <see cref="MinWindowWidthCollapsed"/>. Re-applied by <see cref="ApplyPaneMinSize"/> whenever
    /// the pane opens or closes — and only then can it go <em>down</em>, since the fold happens at
    /// <see cref="PaneCollapseWidth"/> and the collapsed floor is exactly
    /// <see cref="OpenPaneLength"/> − <see cref="CollapsedPaneWidth"/> below it.
    /// </para>
    /// </summary>
    private static void ApplyPresenterMinSize(OverlappedPresenter presenter, double scale, bool paneOpen)
    {
        _rasterizationScale = scale > 0 ? scale : 1.0;

        var logicalWidth = paneOpen ? MinWindowWidthExpanded : MinWindowWidthCollapsed;

        presenter.PreferredMinimumWidth = (int)Math.Round(logicalWidth * _rasterizationScale);
        presenter.PreferredMinimumHeight = (int)Math.Round(MinWindowHeight * _rasterizationScale);
    }

    /// <summary>
    /// Re-applies the window floors for the pane state that is in effect right now.
    /// <para>
    /// Called after every pane transition — from <see cref="UpdatePaneForWidth"/> and from
    /// <see cref="OnPaneToggleClick"/>. It re-reads the <c>NavigationView</c>'s current
    /// rasterization scale, so a DPI change and a pane transition cancelling each other out is
    /// still handled correctly (a stale <c>sender</c> scale from a previous
    /// <see cref="OnXamlRootChanged"/> call would otherwise pin the floors to the old monitor).
    /// Cheap and idempotent, so it needs no change detection.
    /// </para>
    /// </summary>
    private void ApplyPaneMinSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;

        ApplyPresenterMinSize(presenter, Nav.XamlRoot?.RasterizationScale ?? 1.0, Nav.IsPaneOpen);
    }

    private void LocalizeNavigation()
    {
        NavShortcuts.Content = LocalizationService.Get("TabShortcuts");
        NavPreferences.Content = LocalizationService.Get("TabPreferences");
        NavAbout.Content = LocalizationService.Get("TabAbout");
    }

    private void WireServices()
    {
        ListenForSecondInstance();
        StartSystemThemeWatcher();

        // An in-progress hotkey capture hooks the keyboard GLOBALLY (Handling = true), so it must
        // not outlive this window's activation: otherwise every Ctrl+chord the user presses in
        // another app gets swallowed and rebinds the profile. LostFocus is not guaranteed to fire
        // on window deactivation, so end the capture explicitly here.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                _services.ProfileManager.EndHotKeyCapture();
        };

        if (_services.HookManager is { } hooks)
        {
            hooks.OnVisibilityToggled += ToggleVisibility;
            // The reader-facing picker for the TabSearch hotkey action — previously this event had no
            // subscriber at all, so the action did nothing (AUD-06).
            hooks.OnTabSearchRequested += ShowTabSearch;
            StartHooks(hooks);
        }

        if (_services.Tray is { } tray)
        {
            tray.ShowRequested += ShowFromTray;
            tray.ExitRequested += ExitApplication;
        }

        AppWindow.Closing += OnClosing;
    }

    /// <summary>
    /// Applies the saved hook state at startup — the WPF build's <c>StartHooks()</c>.
    /// This was missing, so hooks only ever ran after being toggled from the tray.
    /// </summary>
    private static void StartHooks(HookManager hooks)
    {
        try
        {
            StartupLog.Step(
                $"hooks: window={SettingsManager.IsWindowHookActive} " +
                $"keyboard={SettingsManager.IsKeyboardHookActive} " +
                $"mouse={SettingsManager.IsMouseHookActive} " +
                $"reuseTabs={SettingsManager.ReuseTabs}");

            if (SettingsManager.IsWindowHookActive) hooks.StartWindowHook();
            if (SettingsManager.IsMouseHookActive) hooks.StartMouseHook();
            if (SettingsManager.IsKeyboardHookActive) hooks.StartKeyboardHook();
            hooks.SetReuseTabs(SettingsManager.ReuseTabs);

            StartupLog.Step("hooks: started");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("MainWindow.StartHooks", ex);
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;

        var pageType = tag switch
        {
            "Shortcuts" => typeof(ShortcutsPage),
            "Preferences" => typeof(PreferencesPage),
            _ => typeof(AboutPage)
        };

        // Guard on the actually-realised page, not on CurrentSourcePageType: the latter is set as
        // soon as navigation is requested, so it can claim a page is showing while the frame is
        // still empty.
        if (ContentFrame.Content is FrameworkElement currentPage && currentPage.GetType() == pageType)
        {
            StartupLog.Step($"nav: {tag} already realised, skipping");
            return;
        }

        StartupLog.Step($"nav: {tag}");
        ContentFrame.Navigate(pageType, _services);
        StartupLog.Step($"nav: {tag} done, content={ContentFrame.Content?.GetType().Name ?? "null"}");
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;

        // Without a tray icon there would be no way back to a hidden window, so fall through and
        // let the window actually close instead of stranding the process.
        if (_services.Tray is null)
        {
            StartupLog.Step("OnClosing: no tray icon, allowing real close");
            _exiting = true;
            StopSystemThemeWatcher();
            return;
        }

        // Close means "hide to tray" — the process is a resident hook host.
        //
        // IMPORTANT: this handler is strictly synchronous. Awaiting async UI work here (a
        // ContentDialog, for instance) is a documented source of 0xC0000005 access violations in
        // WinUI 3 because the native window can be torn down before the await resumes.
        args.Cancel = true;
        HideToTray();
    }

    /// <summary>Saves the window size, then hides to the tray. Profiles are already persisted on edit.</summary>
    public void HideToTray()
    {
        // Never hide while a dialog is up. The dialog would go with the window — invisible and therefore
        // unclosable — and since it holds the one-dialog gate, every later dialog (tab search included)
        // would queue behind it for the rest of the session while the caller blocked on the answer (the
        // restore prompt blocks the STA queue) never returns. Every hide path funnels through here —
        // the close button, the toggle hotkey and the sign-in start — so this is the one place to check.
        if (_services.Dialogs.IsDialogOpen)
        {
            StartupLog.Step("HideToTray: refused, a dialog is open");
            return;
        }

        SaveWindowSize();

        // The user is done editing: now it is safe to discard rows that were never filled in.
        // Profiles themselves are already persisted on every change.
        _services.ProfileManager.PruneUntouchedProfiles();

        AppWindow.Hide();
    }

    /// <summary>
    /// Persists the current window geometry in the unit <see cref="WindowSize"/> is defined in:
    /// <b>the outer window size, in effective pixels</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unit has to match <see cref="ApplyInitialSize"/> exactly, which hands the value to
    /// <c>AppWindow.Resize</c> — i.e. to the <b>outer</b> window size in physical pixels. Reading it
    /// back from <see cref="FrameworkElement.ActualWidth"/> (the client area, and for
    /// <c>Nav</c> also minus the 32 epx title-bar row) is what made the window <b>shrink on every
    /// launch</b>: each save wrote a value one frame border (width) / one frame border plus one title
    /// bar (height) smaller than the window it was restored as, and the sequence only stopped once the
    /// presenter floor clamped it. Measured on a 175% display before the fix:
    /// <c>FormSize=856.57x480.57</c> against a window of <c>870.29x520</c> epx, and one cycle lost
    /// exactly the 32 epx title bar (513 → 480.57).
    /// </para>
    /// <para>
    /// Maximised and minimised states are skipped. <c>ActualWidth</c>/<c>AppWindow.Size</c> report the
    /// screen (or the restored icon geometry) rather than the size the user chose, so storing them
    /// made the next launch open as a screen-sized normal window. The last user-chosen size is kept
    /// instead — which is also what the WPF build did, since <c>Window.Width</c> keeps its last
    /// non-maximised value.
    /// </para>
    /// </remarks>
    private void SaveWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;

        if (presenter.State != OverlappedPresenterState.Restored)
        {
            StartupLog.Step($"HideToTray: size kept ({presenter.State}, not a user-chosen geometry)");
            return;
        }

        // The layout has to have run before there is anything meaningful to save, and this is not a
        // theoretical guard: `App.OnLaunched` calls `Activate()` and then `HideToTray()` on the sign-in
        // path, while `Activate()` returns BEFORE `Nav.Loaded` — so on that path XamlRoot (and with it
        // the rasterization scale) is still null and Nav has not been measured.
        //
        // The previous version fell back to scale 1.0 there. Measured on the sign-in path at 175%: it
        // wrote `2880x1536` — the PHYSICAL window size — into a field that means effective pixels, and
        // the next ApplyInitialSize multiplied it by 1.75, so the window came back clamped to the
        // screen (`target=5040x2688 actual=3868x2188`). Skipping the save is the only safe answer, and
        // nothing is lost: before the layout there is no user-chosen geometry to remember either.
        if (Nav.XamlRoot is not { RasterizationScale: > 0 } root ||
            Nav.ActualWidth <= 0 ||
            Nav.ActualHeight <= 0)
        {
            StartupLog.Step("HideToTray: size not saved (window not laid out yet)");
            return;
        }

        var scale = root.RasterizationScale;

        var size = AppWindow.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        SettingsManager.FormSize = new WindowSize(size.Width / scale, size.Height / scale);

        StartupLog.Step(
            $"HideToTray: size saved {size.Width / scale:F1}x{size.Height / scale:F1} epx " +
            $"(window {size.Width}x{size.Height} @ scale {scale:F3})");
    }

    /// <summary>
    /// Brings the window back on screen if it is hidden, without the topmost promotion.
    /// </summary>
    /// <remarks>
    /// Used by <c>ContentDialogService</c> immediately before a dialog is shown. A dialog attached to a
    /// hidden window is invisible <em>and</em> unclosable, and because it holds the one-dialog gate that
    /// is not merely cosmetic: every later dialog queues behind it forever, and the caller blocked on it
    /// (the restore prompt runs on the STA thread) keeps the STA queue. Deliberately not
    /// <see cref="ShowFromTray"/>: that arms the grace-period timer, whose whole problem is that it
    /// cannot know how long the user will take over a dialog (see <see cref="PresentWindow"/>).
    /// </remarks>
    internal void EnsureWindowVisible()
    {
        if (AppWindow.IsVisible) return;

        StartupLog.Step("EnsureWindowVisible: window was hidden in the tray, showing it for a pending dialog");

        AppWindow.Show();
        Activate();
    }

    public void ShowFromTray()
    {
        // A tray restore on its own is the whole point of the call — nothing follows it to dismiss, so
        // the backstop timer is what ends the promotion.
        UnhideAndPresent("ShowFromTray", TopmostRelease.AfterGracePeriod);
    }

    /// <summary>
    /// Un-hides the window if it is in the tray, then raises it. Shared by the tray restore and the
    /// second-instance notice, which differ only in what ends the topmost promotion.
    /// </summary>
    private void UnhideAndPresent(string reason, TopmostRelease releaseWhen)
    {
        // The pre-state is logged HERE, before Show(), because that is the only moment it is still
        // observable: PresentWindow's own line always runs after the window is up again and used to
        // report "wasHidden=False" for a genuine tray restore.
        StartupLog.Step($"{reason}: restoring from the tray (was hidden)");

        AppWindow.Show();
        PresentWindow(reason, releaseWhen);
    }

    /// <summary>
    /// Raises the window above whatever is covering it and gives it the foreground.
    /// </summary>
    /// <param name="reason">Short tag for the startup log, so the path taken can be told apart.</param>
    /// <param name="releaseWhen">
    /// What ends the topmost promotion. Use <see cref="TopmostRelease.OnDialogDismissed"/> when the
    /// caller is about to show a dialog and will call <see cref="ReleaseTopmostNow"/> itself once it
    /// closes; use <see cref="TopmostRelease.AfterGracePeriod"/> when nothing is dismissed and the
    /// backstop timer is the only thing that can end it.
    /// </param>
    /// <remarks>
    /// <para>
    /// Split out of <see cref="ShowFromTray"/> because "the window is visible but covered" needs the
    /// same treatment as "the window is hidden in the tray" — but only the latter involves
    /// <c>AppWindow.Hide/Show</c>. The two used to be one method, so callers that needed the front
    /// without the tray semantics had to fake a hide first; and whenever they reused it, the log line
    /// claimed a tray restore that never happened.
    /// </para>
    /// <para>
    /// Best effort, deliberately not a guarantee. A controlled experiment (see
    /// <see cref="WinApi.ForceToTop"/>) showed that against a fullscreen game neither a bare
    /// <c>SetForegroundWindow</c> nor a verified <c>HWND_TOPMOST</c> promotion is enough on its own —
    /// the window can remain invisible. That is the environment, not a retryable bug here, so the
    /// result is only logged rather than asserted.
    /// </para>
    /// <para>
    /// <b>The grace-period timer must never be armed while a dialog is coming.</b> It was, and a real
    /// fullscreen game punished it: the promotion was dropped at ~2.5s, the game reclaimed the top of
    /// the Z order immediately, and the notice — still open and being read — vanished behind the game.
    /// Measured 3/3 on Ghost Recon Breakpoint. A timer cannot know how long the user will take to read,
    /// so it is not allowed to arbitrate that; that is what <paramref name="releaseWhen"/> is for.
    /// </para>
    /// </remarks>
    private void PresentWindow(string reason, TopmostRelease releaseWhen)
    {
        // NB: "was the window hidden?" is deliberately NOT sampled here. Every caller that restores from
        // the tray (UnhideAndPresent) has already called AppWindow.Show() by the time this runs, so the
        // answer would always be "no" — which is exactly what the log used to report, and it made a
        // genuine tray restore look like a plain re-raise. That path logs the pre-state itself.

        // A tray popup menu owns the foreground while its click handler runs, so the OS silently
        // ignores a plain Activate(). ForceToTop restores, promotes the Z-order band and then takes the
        // foreground through the synthetic-key workaround the Core uses for Explorer windows.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var foreground = WinApi.ForceToTop(hwnd);

        Activate();

        // Only start the backstop when nothing else will end the promotion. Arming it unconditionally
        // is what let it fire underneath an open dialog (see the remarks above).
        if (releaseWhen == TopmostRelease.AfterGracePeriod) ScheduleTopmostRelease(reason);

        // Logged after the call, so a window that failed to come forward is diagnosable from the log
        // rather than assumed. `foreground=False` is the signature of the fullscreen case above.
        StartupLog.Step($"{reason}: visible={AppWindow.IsVisible} foreground={foreground}");
    }

    /// <summary>
    /// Drops the topmost promotion applied by <see cref="PresentWindow"/> once the window is no longer
    /// needed at the front, and after a grace period as a backstop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="WinApi.ForceToTop"/> needs <c>HWND_TOPMOST</c> to surface the window at all, but the
    /// promotion is a persistent style — left alone, the window would keep outranking the user's
    /// Explorer windows and browser for the rest of the session, which is exactly the stray-topmost
    /// behaviour people complain about.
    /// </para>
    /// <para>
    /// The grace period is a <b>backstop, not the primary trigger</b>. Timing it against the notice
    /// would be a guess: the "already running" dialog stays up until the user dismisses it, so a
    /// fixed short deadline could drop the promotion while it is still being read, and a long one
    /// would leave the window pinned over everything for no reason. Callers that show a dialog
    /// therefore call <see cref="ReleaseTopmostNow"/> as soon as it closes; this timer only covers the
    /// paths where nothing is dismissed (a tray restore, say), so the promotion can never be left on.
    /// </para>
    /// <para>
    /// Re-armed per call, so a second launch extends the window rather than inheriting a nearly
    /// expired deadline.
    /// </para>
    /// </remarks>
    private void ScheduleTopmostRelease(string reason)
    {
        _topmostReleaseReason = reason;

        var timer = _topmostReleaseTimer;
        if (timer is null)
        {
            timer = new DispatcherTimer { Interval = TopmostGracePeriod };
            timer.Tick += (_, _) =>
            {
                timer!.Stop();
                ReleaseTopmostNow();
            };
            _topmostReleaseTimer = timer;
        }

        // Restart from now, so a second launch extends the window rather than inheriting a nearly
        // expired deadline.
        timer.Stop();
        timer.Start();
    }

    /// <summary>
    /// Drops the topmost promotion immediately. Safe to call when nothing is promoted.
    /// </summary>
    /// <remarks>
    /// This is the primary path: call it once whatever the window was raised for is finished with —
    /// i.e. right after the dialog that prompted it is dismissed — rather than waiting out the
    /// backstop timer in <see cref="ScheduleTopmostRelease"/>.
    /// </remarks>
    private void ReleaseTopmostNow()
    {
        _topmostReleaseTimer?.Stop();

        WinApi.ReleaseTop(WinRT.Interop.WindowNative.GetWindowHandle(this));
        StartupLog.Step($"{_topmostReleaseReason}: topmost released");
    }

    private void ToggleVisibility()
    {
        if (AppWindow.IsVisible) HideToTray();
        else ShowFromTray();
    }

    /// <summary>
    /// Shows the tab-search picker for the TabSearch hotkey action.
    /// <para>
    /// <see cref="HookManager"/> already marshals this onto the UI thread via its dispatcher, so the
    /// handler must not hop threads again — it builds and shows the dialog directly (AUD-06).
    /// </para>
    /// </summary>
    private async void ShowTabSearch()
    {
        try
        {
            var watcher = _services.HookManager?.WindowHook;

            // The dialog needs a XamlRoot to attach to; without a realised window there is nowhere to
            // host it, so bail out rather than throw.
            var root = (Content as FrameworkElement)?.XamlRoot;
            if (watcher is null || root is null)
            {
                StartupLog.Step("ShowTabSearch: window watcher or XamlRoot unavailable");
                return;
            }

            // A ContentDialog renders inside the window's visual tree. While the window sits hidden
            // in the tray the dialog would be invisible AND unclosable — and because
            // TabSearchDialog only allows one instance at a time, the hotkey would stay dead for
            // the rest of the session. Surface the window first (same treatment as the
            // second-instance notice in OnSecondInstanceRequested).
            //
            // Unlike the second-instance notice this branch stays conditional on IsVisible: the hotkey
            // is only an issue when the dialog would be unclosable, and raising an already-visible
            // window mid-typing would be an unasked-for interruption.
            //
            // OnDialogDismissed, never the grace-period timer: a dialog follows immediately, and a timer
            // cannot know how long the user will take over it. Dropping the topmost promotion mid-read
            // hands the front straight back to a fullscreen game and takes the picker with it (measured
            // 3/3; see PresentWindow). The release happens in the finally below.
            if (!AppWindow.IsVisible) UnhideAndPresent("ShowTabSearch", TopmostRelease.OnDialogDismissed);

            try
            {
                await TabSearchDialog.ShowAsync(watcher, root, _services.Dialogs);
            }
            finally
            {
                // Safe when nothing was promoted. OnDialogDismissed means no backstop timer was armed, so
                // nothing else would ever give the promotion up.
                ReleaseTopmostNow();
            }
        }
        catch (Exception ex)
        {
            StartupLog.Fail("MainWindow.ShowTabSearch", ex);
        }
    }

    private void ExitApplication()
    {
        // Let the close handler know this is a real exit, not a hide.
        _exiting = true;

        StopSystemThemeWatcher();
        _services.Dispose();
        Application.Current.Exit();
    }
}
