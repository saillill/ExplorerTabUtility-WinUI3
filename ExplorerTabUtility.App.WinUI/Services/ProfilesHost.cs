using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using H.Hooks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ExplorerTabUtility.Abstractions;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Models;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// <see cref="IProfilesHost"/> over a plain <see cref="Panel"/>.
/// <para>
/// The container is attached late: <c>ProfileManager</c> is constructed during app startup,
/// before the shortcuts page (and therefore its panel) exists.
/// </para>
/// </summary>
public sealed class ProfilesHost : IProfilesHost
{
    private readonly List<IProfileCardView> _cards = [];
    private ItemsControl? _panel;

    public IReadOnlyList<IProfileCardView> Cards => _cards;

    /// <summary>
    /// Binds the panel that hosts the cards and re-parents any cards already created.
    /// <para>
    /// Idempotent: re-attaching the same panel is a no-op, so a page that refreshes on every
    /// navigation does not churn its visual tree.
    /// </para>
    /// </summary>
    public void Attach(ItemsControl panel)
    {
        if (panel is null) throw new ArgumentNullException(nameof(panel));
        if (ReferenceEquals(_panel, panel)) return;

        Detach();
        _panel = panel;

        // The cards go in once the panel is in the tree and the window can show them.
        //
        // Both halves are measured rather than assumed. A transition plays for items added after the control has
        // been rendered, and this is called from OnNavigatedTo, which runs before that — so adding them there
        // meant no transition at all. And the window's content is not painted until about 470ms after the window
        // appears, so an animation started in that gap plays behind a blank window: a frame series showed the
        // window blank from 16.56 to 16.70 while the entrance ran from 16.36 to 16.86.
        if (panel.IsLoaded)
        {
            AddCardsAfterStartup(panel);
            return;
        }

        void OnPanelLoaded(object sender, RoutedEventArgs e)
        {
            panel.Loaded -= OnPanelLoaded;
            AddCardsAfterStartup(panel);
        }

        panel.Loaded += OnPanelLoaded;
    }

    /// <summary>
    /// Adds the cards once the window has painted, so the entrance they play can be seen, and paces them so the
    /// staggering is legible.
    /// <para>
    /// The wait before the first card is the measured start-up time plus a margin. The pace between cards is this
    /// app's choice, and it has to be: <c>EntranceThemeTransition</c> has no property for the interval between
    /// items — the runtime picks it, and what it picks reads as the cards arriving together. Spacing the adds is
    /// not a hand-written animation; each card still enters through the platform's own item transition, which is
    /// what <c>ItemContainerTransitions</c> is for. The pace is one of the guidance's durations, so the timing
    /// stays inside the set this was asked for.
    /// </para>
    /// </summary>
    private async void AddCardsAfterStartup(ItemsControl panel)
    {
        await Task.Delay(EntranceDelay);

        // All at once, deliberately. The staggering is the platform's, from the control's
        // ItemContainerTransitions, and it applies to the items the control takes on as a group. Adding them one
        // at a time to pace the stagger by hand was tried and made no visible difference: the item transition
        // does not appear to run for items added long after the control has loaded, so the cards simply appeared,
        // spaced out. This arrangement is also the only one whose stagger has been reported as visible.
        foreach (var card in _cards)
            panel.Items.Add(((ProfileCardView)card).Root);

        StartupLog.Step($"profiles: {_cards.Count} cards added");
    }

    /// <summary>How long the page stays card-less, waiting for the window's first paint.</summary>
    private static readonly TimeSpan EntranceDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Releases the cards from their current parent.
    /// <para>
    /// WinUI allows an element only one parent, so a card reused by a new page must be removed from
    /// the old page's panel first — otherwise adding it throws and the page never renders.
    /// </para>
    /// </summary>
    public void Detach()
    {
        foreach (var card in _cards)
            RemoveFromParent(((ProfileCardView)card).Root);

        _panel = null;
    }

    private static void RemoveFromParent(FrameworkElement element)
    {
        if (element.Parent is Panel parent)
            parent.Children.Remove(element);
    }

    public void Clear()
    {
        _panel?.Items.Clear();
        _cards.Clear();
    }

    public void Add(IProfileCardView card)
    {
        _cards.Add(card);

        var root = ((ProfileCardView)card).Root;
        RemoveFromParent(root);
        _panel?.Items.Add(root);
    }

    public void Remove(IProfileCardView card)
    {
        _cards.Remove(card);
        _panel?.Items.Remove(((ProfileCardView)card).Root);
    }
}

/// <summary>
/// One hotkey profile, laid out to match the Windows settings pattern used by the reference design:
/// <list type="bullet">
/// <item>One compact row per profile — icon, name, and <c>热键 · 功能</c> — with the enable switch
/// and an expand chevron on the trailing edge.</item>
/// <item>Collapsed by default; every editable setting lives behind the chevron.</item>
/// <item>Expanded rows are grouped under <c>基本</c> / <c>行为</c> headings so eight settings do not
/// read as one undifferentiated list.</item>
/// </list>
/// <para>
/// Every element is a native control (<c>SettingsCard</c>, <c>TextBox</c>, <c>ComboBox</c>,
/// <c>NumberBox</c>, <c>ToggleSwitch</c>, <c>Button</c>, <c>FontIcon</c>) inside a plain
/// <see cref="StackPanel"/>. <c>SettingsExpander</c> was deliberately dropped: its template draws a
/// full-width hairline above the expanded content and does not stretch that content, which both
/// added the stray top border and let the inner cards collapse to their content width.
/// </para>
/// </summary>
internal sealed class ProfileCardView : IProfileCardView
{
    // ---- Summary row ---------------------------------------------------------

    /// <summary>
    /// The card itself: the toolkit's <see cref="SettingsExpander"/>.
    /// <para>
    /// It is the control the reference layout comes from — the Windows Settings page is built with it, which is
    /// why it, and not the platform's own <c>Expander</c>, is what this card uses. It draws the official header
    /// slots (icon, header, description, trailing content), joins the expanded rows to the header with the
    /// separator the reference has, and animates the disclosure itself.
    /// </para>
    /// <para>
    /// This control was dropped once, for two reasons: a full-width hairline above the expanded content, and
    /// content that did not stretch. The hairline is in the reference — it is the separator between the header and
    /// the rows. The stretch is handled by the rows themselves, each of which is a <c>SettingsCard</c> with
    /// <c>HorizontalAlignment.Stretch</c>.
    /// </para>
    /// </summary>
    private readonly SettingsExpander _root = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        IsExpanded = false
    };

    private readonly TextBlock _summary = new();
    private readonly TextBlock _summaryDetail = new();
    private readonly ToggleSwitch _enabled = new();
    private readonly CheckBox _handled = new();
    private readonly CheckBox _asTab = new();

    // ---- Detail rows ---------------------------------------------------------

    private readonly TextBox _name = new();
    private readonly TextBox _hotKeys = new() { IsReadOnly = true };
    private readonly ComboBox _scope = new();
    private readonly ComboBox _action = new();
    private readonly TextBox _path = new();

    // Rows that only apply to some actions / trigger types. Kept so their visibility can follow the
    // selection instead of leaving dead controls in the panel.
    private SettingsCard? _pathCard;
    private SettingsCard? _delayCard;
    private SettingsCard? _asTabCard;
    private SettingsCard? _handledCard;
    private readonly NumberBox _delay = new()
    {
        Minimum = 0,
        Maximum = 10_000,
        SmallChange = 100,
        LargeChange = 1_000,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
    };

    private readonly ProfileCardCallbacks _callbacks;
    private readonly DispatcherQueue _queue;

    /// <summary>
    /// Every row of this card in visual order — index 0 is the summary row, the last entry is the
    /// delete row. Used to apply the Windows 11 corner-radius rule.
    /// </summary>
    private readonly List<SettingsCard> _rowCards = [];

    /// <summary>
    /// Re-applies the localized Header/Description of every settings card. Captured while building
    /// the UI so a language switch refreshes the card labels too, not just the combo boxes.
    /// </summary>
    private readonly List<Action> _localizers = [];

    private LowLevelKeyboardHook? _keyboardHook;
    private LowLevelMouseHook? _mouseHook;
    private Key _lastClickKey;
    private int _lastClickTime;
    private bool _pointerOverHotKeys;

    /// <summary>Suppresses change handlers while we are pushing values into the controls.</summary>
    private bool _loading = true;

    public ProfileCardView(HotKeyProfile profile, ProfileCardCallbacks callbacks)
    {
        Profile = profile;
        _callbacks = callbacks;
        _queue = DispatcherQueue.GetForCurrentThread();

        BuildUi();
        LoadFromProfile();
        _loading = false;
    }

    public FrameworkElement Root => _root;

    public HotKeyProfile Profile { get; }

    public bool IsEnabled
    {
        get => _enabled.IsOn;
        set => _enabled.IsOn = value;
    }

    // ---- Construction --------------------------------------------------------

    private void BuildUi()
    {
        BuildSummaryRow();
        BuildDetailRows();

        // The toolkit's slots, which are the layout the reference uses: icon, header, description, and the
        // trailing content (the enable switch). The rows go into Items, which is what draws them joined to the
        // header — nothing is added or removed per toggle, the control reveals what is already there.
        _root.Header = _summary;
        _root.Description = _summaryDetail;
        _root.HeaderIcon = new FontIcon { Glyph = ProfileGlyph, FontSize = 16 };
        _root.Content = _enabled;

        _root.Expanded += (_, _) => OnExpandedChanged(expanded: true);
        _root.Collapsed += (_, _) => OnExpandedChanged(expanded: false);

        WireHandlers();

        // The control's own state, not the field's default: the corner radii and the row seams depend on whether
        // the card starts open, and the events only fire on user interaction.
        _isExpanded = _root.IsExpanded;
        ApplyCornerRadii();
    }

    /// <summary>
    /// The always-visible row. Deliberately carries only 名称 / 热键 / 功能 — the reference design
    /// keeps the collapsed row to the identifying information plus its primary control.
    /// </summary>
    private void BuildSummaryRow()
    {
        _summary.TextWrapping = TextWrapping.NoWrap;
        _summary.TextTrimming = TextTrimming.CharacterEllipsis;
        if (TryFindStyle("BodyStrongTextBlockStyle", out var strongStyle))
            _summary.Style = strongStyle;

        _summaryDetail.TextWrapping = TextWrapping.NoWrap;
        _summaryDetail.TextTrimming = TextTrimming.CharacterEllipsis;

        // Styled rather than coloured: the style's {ThemeResource} setter re-resolves with the theme
        // that is actually in effect, which a brush fetched in code does not.
        if (TryFindStyle("SecondaryCaptionTextBlockStyle", out var captionStyle))
            _summaryDetail.Style = captionStyle;

        // WinUI renders OnContent/OffContent to the leading side of the switch, which is exactly the
        // "开 [switch]" arrangement in the reference design.
        _enabled.OnContent = Loc("ToggleOn");
        _enabled.OffContent = Loc("ToggleOff");
        _enabled.VerticalAlignment = VerticalAlignment.Center;

        // Nothing else is laid out here: SettingsExpander has the header slots — icon, header, description,
        // trailing content — and lays them out the way the reference does. The hand-built Grid that stood in
        // for them is gone with the platform Expander that needed it.
    }

    /// <summary>
    /// Follows the Expander's own state. The detail rows are always in its content, so there is nothing to attach
    /// or detach — what changes is the corner radii, because the group is taller when it is open, and the log line
    /// that records the measured row heights.
    /// </summary>
    private void OnExpandedChanged(bool expanded)
    {
        _isExpanded = expanded;

        // Which corners count as "outer" changes with the group's height.
        ApplyCornerRadii();

        if (_rowsLogged) return;
        _rowsLogged = true;

        _queue.TryEnqueue(() => StartupLog.Step(
            $"rows: card={_root.ActualHeight:F0} " +
            $"detail={string.Join("/", _rowCards.Select(row => row.ActualHeight.ToString("F0")))}"));
    }

    /// <summary>
    /// Settings shown after expanding, laid out to match the Windows 11 Settings page.
    /// <para>
    /// Nested rows there are single-line: no icon, no description, and a leading control. Per
    /// Microsoft's checkbox guidance, a boolean that belongs to a group of related options is a
    /// check box rather than a toggle switch, and its label is itself the statement the check mark
    /// makes true — which is why those rows need no extra explanatory line.
    /// </para>
    /// </summary>
    private void BuildDetailRows()
    {
        AddCard(_root.Items, "Name", _name);
        AddCard(_root.Items, "HotKeys", _hotKeys);
        AddCard(_root.Items, "Scope", _scope);
        AddCard(_root.Items, "Action", _action);

        // These three only apply to some actions; ApplyOptionVisibility() hides the ones the
        // selected action ignores. Each label therefore states what the field is for instead of
        // relying on a second line of explanatory text.
        _pathCard = AddCard(_root.Items, "Path", _path);
        _delayCard = AddCard(_root.Items, "Delay", _delay);

        // Booleans: leading check box whose label is the statement it asserts.
        _handledCard = AddCheckRow(_root.Items, "HandledTooltip", _handled);
        _asTabCard = AddCheckRow(_root.Items, "TabTooltip", _asTab);

        // Destructive action is the last row of the same group. The button's solid fill comes from
        // DestructiveButtonTemplate — keeping it red while hovered or pressed means overriding six
        // Button* theme keys on the element itself, and those can only be declared in XAML.
        var deleteButton = CreateDestructiveButton();
        deleteButton.Content = Loc("Delete");
        deleteButton.Click += (_, _) =>
        {
            _callbacks.Remove(Profile);
            Save();
        };

        var deleteCard = new SettingsCard
        {
            Header = CreateRowLabel(Loc("DeleteProfileTooltip")),
            Content = deleteButton,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = CompactRowPadding,
            MinHeight = CompactRowMinHeight
        };
        _localizers.Add(() =>
        {
            if (deleteCard.Header is TextBlock label) label.Text = Loc("DeleteProfileTooltip");
            deleteButton.Content = Loc("Delete");
        });
        _root.Items.Add(deleteCard);
        _rowCards.Add(deleteCard);
    }

    /// <summary>
    /// A boolean row: <c>☑ statement</c> on the leading edge, nothing on the trailing edge.
    /// <para>
    /// The label comes from the string that used to be the row's description — Microsoft's guidance
    /// is to "word the checkbox label as a statement that the check mark makes true", so that text
    /// belongs in the label rather than in a second line under it.
    /// </para>
    /// </summary>
    private SettingsCard AddCheckRow(IList<object> host, string statementKey, CheckBox checkBox)
    {
        checkBox.Content = Loc(statementKey);
        checkBox.MinWidth = 0;
        checkBox.VerticalAlignment = VerticalAlignment.Center;

        var card = new SettingsCard
        {
            Header = checkBox,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = CompactRowPadding,
            MinHeight = CompactRowMinHeight
        };

        _localizers.Add(() => checkBox.Content = Loc(statementKey));

        host.Add(card);
        _rowCards.Add(card);

        return card;
    }

    /// <summary>
    /// Shows only the rows the current profile can actually use.
    /// <para>
    /// A row that the selected action never reads is worse than missing: it invites the user to fill
    /// in a field that will be ignored. The predicates live in
    /// <see cref="HotKeyActionCatalog"/> and mirror what <c>HookManager</c> passes on to the
    /// watcher.
    /// </para>
    /// <para>
    /// Collapsing a row changes nothing about the seam/corner geometry of its neighbours (hidden
    /// cards still hold their -1px overlap and zero interior radius), but the group's outer corners
    /// are recomputed anyway so the visible last row keeps its rounding.
    /// </para>
    /// </summary>
    private void ApplyOptionVisibility()
    {
        var action = Profile.Action;

        // Collected rather than applied per row: this method runs from UpdateSummary, which runs on every
        // keystroke in the name and path fields, and ApplyCornerRadii writes Margin + CornerRadius on
        // every row card — i.e. a layout invalidation per keystroke for a result that cannot have
        // changed. Only a real visibility change can move the group's outer corners.
        var changed = false;
        changed |= SetRowVisible(_pathCard, HotKeyActionCatalog.UsesPath(action));
        changed |= SetRowVisible(_delayCard, HotKeyActionCatalog.UsesDelay(action));
        changed |= SetRowVisible(_asTabCard, HotKeyActionCatalog.UsesAsTab(action));

        // IsHandled only governs keyboard profiles. The mouse hook needs no per-profile option:
        // it swallows XButton1/2 automatically (they carry shell-default back/forward behaviour)
        // and never swallows LMB/RMB/MMB, so the checkbox would change nothing a mouse profile does.
        changed |= SetRowVisible(_handledCard, !Profile.IsMouse);

        if (changed)
            ApplyCornerRadii();
    }

    /// <summary>
    /// Shows or hides one row, reporting whether that actually changed the row's state.
    /// </summary>
    private static bool SetRowVisible(SettingsCard? card, bool visible)
    {
        if (card is null) return false;

        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        if (card.Visibility == target) return false;

        card.Visibility = target;
        return true;
    }

    /// <summary>
    /// Applies the Windows 11 corner-radius rule to the row group.
    /// <para>
    /// Per "Geometry in Windows 11" (learn.microsoft.com/windows/apps/design/style/rounded-corner):
    /// in-page elements such as list backplates use <c>ControlCornerRadius</c> (4px — the 8px
    /// <c>OverlayCornerRadius</c> is reserved for top-level containers and overlays), and
    /// "straight edges that intersect with other straight edges are not rounded". The doc's
    /// "when not to round" section is explicit that elements housed inside a container that touch
    /// each other must have no rounding where they meet.
    /// </para>
    /// <para>
    /// So only the first row's top corners and the last row's bottom corners stay rounded; every
    /// interior edge is square. Collapsed, the summary row is the whole group, so it keeps all
    /// four corners.
    /// </para>
    /// </summary>
    private void ApplyCornerRadii()
    {
        var radius = ResolveCornerRadius();
        var expanded = _isExpanded;
        var last = _rowCards.Count - 1;

        for (var i = 0; i < _rowCards.Count; i++)
        {
            var card = _rowCards[i];

            // Each SettingsCard draws its own 1px border. With the rows flush, two borders meet at
            // every junction and read as a 2px line. Pulling each row up by 1px makes the borders
            // overlap into a single hairline — the "one seam" the design calls for.
            card.Margin = i == 0 ? new Thickness(0) : new Thickness(0, -1, 0, 0);

            if (!expanded)
            {
                // Only the summary row is visible, so it is both the first and the last row.
                card.CornerRadius = i == 0 ? new CornerRadius(radius) : new CornerRadius(0);
                continue;
            }

            var roundTop = i == 0;
            var roundBottom = i == last;

            card.CornerRadius = new CornerRadius(
                roundTop ? radius : 0,
                roundTop ? radius : 0,
                roundBottom ? radius : 0,
                roundBottom ? radius : 0);
        }
    }

    /// <summary>
    /// <c>ControlCornerRadius</c> from the official theme resources (4px by default). Read rather
    /// than hard-coded so an app-level override or a future Fluent revision is picked up.
    /// </summary>
    private static double ResolveCornerRadius()
    {
        if (TryFindResource("ControlCornerRadius", out var value) && value is CornerRadius radius)
            return radius.TopLeft;

        return 4;
    }

    private bool _isExpanded;
    private bool _rowsLogged;

    private void WireHandlers()
    {
        _name.TextChanged += (_, _) =>
        {
            if (_loading) return;
            Profile.Name = _name.Text;
            UpdateSummary();
            Save();
        };

        _enabled.Toggled += (_, _) =>
        {
            if (_loading) return;
            Profile.IsEnabled = _enabled.IsOn;
            Save();
        };

        _scope.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (_scope.SelectedItem is not DisplayItem<HotkeyScope> item) return;

            Profile.Scope = item.Value;
            RebuildActions(Profile.Action);
            UpdateSummary();
            Save();
        };

        _action.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (_action.SelectedItem is not DisplayItem<HotKeyAction> item) return;

            Profile.Action = item.Value;
            UpdateSummary();
            Save();
        };

        _path.TextChanged += (_, _) =>
        {
            if (_loading) return;
            Profile.Path = _path.Text;
            Save();
        };

        _delay.ValueChanged += (_, e) =>
        {
            if (_loading || double.IsNaN(e.NewValue)) return;
            Profile.Delay = (int)e.NewValue;
            Save();
        };

        _handled.Click += (_, _) =>
        {
            if (_loading) return;
            Profile.IsHandled = _handled.IsChecked == true;
            Save();
        };

        _asTab.Click += (_, _) =>
        {
            if (_loading) return;
            Profile.IsAsTab = _asTab.IsChecked == true;
            Save();
        };

        // Hotkey capture: the text box is a plain, fully native TextBox. Only the capture
        // interaction is custom, because WinUI ships no hotkey-recorder control.
        _hotKeys.GotFocus += (_, _) => StartCapture();
        _hotKeys.LostFocus += (_, _) => StopCapture();
        _hotKeys.PointerEntered += (_, _) => _pointerOverHotKeys = true;
        _hotKeys.PointerExited += (_, _) => _pointerOverHotKeys = false;
    }

    /// <summary>
    /// One settings row: a label on the leading edge, the control on the trailing edge.
    /// <para>
    /// Deliberately no description and no icon — that is what keeps a nested row single-line,
    /// matching the Windows 11 Settings page where only top-level rows carry an icon and a
    /// description. Anything a row needs to explain belongs in its own label, and rows that do not
    /// apply at all are hidden rather than annotated.
    /// </para>
    /// </summary>
    private SettingsCard AddCard(IList<object> host, string labelKey, FrameworkElement content)
    {
        // SettingsCard places Content on the trailing edge; keep it compact so the label always has
        // room on the leading side.
        if (content is Control control)
        {
            control.HorizontalAlignment = HorizontalAlignment.Right;
            control.MinWidth = 180;
            control.VerticalAlignment = VerticalAlignment.Center;
        }

        // Dropdowns get WinUI's official compact density plus an open/close transition.
        if (content is ComboBox comboBox)
            ComboBoxAssist.Configure(comboBox);

        var label = CreateRowLabel(Loc(labelKey));

        var card = new SettingsCard
        {
            Header = label,
            Content = content,
            // Explicit: the card must fill the column rather than size to its content, otherwise
            // SettingsCard switches to its narrow layout and the controls land under the header.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = CompactRowPadding,
            MinHeight = CompactRowMinHeight
        };

        _localizers.Add(() => label.Text = Loc(labelKey));

        host.Add(card);
        _rowCards.Add(card);

        return card;
    }

    /// <summary>
    /// A row label, top-aligned so its text baseline sits at the same offset from the row's top
    /// edge as the summary card's title. A vertically centred label would ride lower than the title
    /// above it, which reads as misalignment between the collapsed and expanded rows.
    /// </summary>
    private static TextBlock CreateRowLabel(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Top,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    // ---- Data binding --------------------------------------------------------

    private void LoadFromProfile()
    {
        _loading = true;
        try
        {
            _enabled.IsOn = Profile.IsEnabled;
            _name.Text = Profile.Name ?? string.Empty;
            UpdateHotKeyField();

            RebuildScopes();
            var coercedAtLoad = RebuildActions(Profile.Action);

            _path.Text = Profile.Path ?? string.Empty;
            _delay.Value = Profile.Delay;
            _handled.IsChecked = Profile.IsHandled;
            _asTab.IsChecked = Profile.IsAsTab;

            UpdateSummary();

            // After UpdateSummary: the row set follows the action, so it has to be refreshed before the
            // coercion is reported (and before the save that persists it).
            if (coercedAtLoad) ReportCoercedAction();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RebuildScopes()
    {
        _scope.ItemsSource = Enum.GetValues<HotkeyScope>()
            .Select(s => new DisplayItem<HotkeyScope>(HotKeyActionCatalog.GetScopeDisplay(s), s))
            .ToArray();
        _scope.DisplayMemberPath = nameof(DisplayItem<HotkeyScope>.Display);
        _scope.SelectedIndex = Profile.Scope == HotkeyScope.Global ? 0 : 1;
    }

    /// <summary>
    /// Rebuilds the action dropdown for the profile's scope.
    /// </summary>
    /// <returns>
    /// True when the profile's own action had to be coerced to fit the scope — the caller decides whether
    /// that needs reporting (a deliberate scope change does not; loading a stored profile does).
    /// </returns>
    private bool RebuildActions(HotKeyAction preferred)
    {
        var allowed = HotKeyActionCatalog.GetAllowedActions(Profile.Scope);
        var desired = allowed.Contains(preferred) ? preferred : allowed.FirstOrDefault();

        _action.ItemsSource = allowed
            .Select(a => new DisplayItem<HotKeyAction>(HotKeyActionCatalog.GetActionDisplay(a), a))
            .ToArray();
        _action.DisplayMemberPath = nameof(DisplayItem<HotKeyAction>.Display);

        var index = Array.IndexOf(allowed, desired);
        _action.SelectedIndex = index < 0 ? 0 : index;

        Profile.Action = desired;

        return desired != preferred;
    }

    /// <summary>
    /// Reports and persists an action that had to be rewritten to fit the profile's scope.
    /// </summary>
    /// <remarks>
    /// A stored profile can hold a combination the editor will never offer — in practice "global scope"
    /// with an action that only works inside File Explorer, which can only arrive from an imported profile
    /// or a hand-edited <c>settings.json</c>. Rewriting it silently left the dropdown showing one action
    /// while the hook snapshot (and the file) still held the other until something else happened to save,
    /// so the UI and the behaviour disagreed. Saving here keeps all three in step and leaves a trace of
    /// why the user's action changed.
    /// </remarks>
    private void ReportCoercedAction()
    {
        StartupLog.Step(
            $"profile '{Profile.Name}': action was not valid for scope {Profile.Scope}, " +
            $"reset to {Profile.Action} and saved");

        Save();
    }

    /// <summary>Refreshes the collapsed row: name on the first line, "热键 · 功能" on the second.</summary>
    private void UpdateSummary()
    {
        var hotKey = Profile.HotKeys is { Length: > 0 }
            ? Profile.HotKeys.HotKeysToString(Profile.IsDoubleClick)
            : Loc("NoHotKeyBound");

        var action = HotKeyActionCatalog.GetActionDisplay(Profile.Action);

        _summary.Text = string.IsNullOrWhiteSpace(Profile.Name) ? hotKey : Profile.Name;
        _summaryDetail.Text = $"{hotKey}  ·  {action}";

        // UpdateSummary runs on every change that can affect which fields apply — action, scope,
        // recorded trigger (keyboard vs mouse) and language — so the row set is refreshed here.
        ApplyOptionVisibility();

        // ...and so is whether the row can be switched on at all: this method is the single funnel
        // every validity-affecting entry point already goes through.
        UpdateEnabledAvailability();
    }

    /// <summary>
    /// Refreshes the trigger field: the recorded keys, or a placeholder saying what the field needs.
    /// <para>
    /// The placeholder doubles as the state text. <c>SelectHotKey</c> ("按下组合键…") is a recording
    /// prompt, so it is only shown from <see cref="StartCapture"/> until <see cref="StopCapture"/>.
    /// At rest an unbound profile reads <c>NoHotKeyBound</c> ("未绑定") instead — a statement about
    /// the profile rather than an instruction the user has not asked for yet. The capture
    /// interaction itself is untouched: focus in the field still starts recording.
    /// </para>
    /// </summary>
    private void UpdateHotKeyField()
    {
        var hasHotKey = Profile.HotKeys is { Length: > 0 };

        _hotKeys.Text = hasHotKey
            ? Profile.HotKeys!.HotKeysToString(Profile.IsDoubleClick)
            : string.Empty;

        _hotKeys.PlaceholderText = hasHotKey || _keyboardHook is not null
            ? Loc("SelectHotKey")
            : Loc("NoHotKeyBound");
    }

    /// <summary>
    /// Makes the enable toggle unavailable while the profile could not fire anyway.
    /// <para>
    /// A profile with no trigger never fires, so an "on" toggle would promise something the app
    /// cannot deliver. The switch keeps its 开/关 label (set in <c>BuildSummaryRow</c> and
    /// <c>RefreshLocalization</c>) so its state stays readable while unavailable, and
    /// <c>App.xaml</c> restores the off-state track outline WinUI drops — so it never renders as
    /// a bare dot.
    /// </para>
    /// </summary>
    private void UpdateEnabledAvailability()
    {
        _enabled.IsEnabled = Profile.HotKeys is { Length: > 0 };
    }

    public void RefreshLocalization()
    {
        foreach (var apply in _localizers)
            apply();

        UpdateHotKeyField();
        _enabled.OnContent = Loc("ToggleOn");
        _enabled.OffContent = Loc("ToggleOff");

        // The Expander draws the disclosure affordance, so its accessible name is the only label left to refresh
        // here — the hand-built button that used to carry one is gone.
        var expandLabel = Loc("ExpandProfile");
        AutomationProperties.SetName(_root, expandLabel);
        ToolTipService.SetToolTip(_root, expandLabel);

        var scope = Profile.Scope;
        var action = Profile.Action;
        var coerced = false;

        _loading = true;
        try
        {
            RebuildScopes();
            coerced = RebuildActions(action);
            _scope.SelectedIndex = scope == HotkeyScope.Global ? 0 : 1;
        }
        finally
        {
            _loading = false;
        }

        UpdateSummary();

        // A language switch must not quietly change what a profile does: if the rebuild had to coerce the
        // action, say so and persist it (see ReportCoercedAction).
        if (coerced) ReportCoercedAction();
    }

    public void EndHotKeyCapture()
    {
        if (_keyboardHook is null && _mouseHook is null) return;

        StopCapture();

        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _name.Text = _hotKeys.Text;
            Profile.Name = _name.Text;
            UpdateSummary();
        }
    }

    // ---- Hotkey capture ------------------------------------------------------

    private void StartCapture()
    {
        if (_keyboardHook is not null) return;

        _callbacks.CaptureStarted();

        _keyboardHook = new LowLevelKeyboardHook { Handling = true };
        _keyboardHook.Down += OnKeyboardDown;
        _keyboardHook.Start();

        _mouseHook = new LowLevelMouseHook { Handling = true, AddKeyboardKeys = true };
        _mouseHook.Down += OnMouseDown;
        _mouseHook.Start();

        // The field now reads the recording prompt instead of the "unbound" state text.
        UpdateHotKeyField();
    }

    private void StopCapture()
    {
        if (_keyboardHook is not null)
        {
            _keyboardHook.Down -= OnKeyboardDown;
            _keyboardHook.Dispose();
            _keyboardHook = null;
        }

        if (_mouseHook is not null)
        {
            _mouseHook.Down -= OnMouseDown;
            _mouseHook.Dispose();
            _mouseHook = null;
        }

        _callbacks.CaptureStopped();

        UpdateHotKeyField();
    }

    private void OnKeyboardDown(object? sender, KeyboardEventArgs e)
    {
        // Backspace clears the binding.
        if (e.Keys.Are(Key.Back))
        {
            Apply(keys: null, isMouse: false, isDoubleClick: false);
            return;
        }

        // Let Tab move focus out of the capture field instead of being recorded.
        if (e.Keys.Are(Key.Tab) || e.Keys.Are(Key.Shift, Key.Tab))
        {
            e.IsHandled = true;
            _queue.TryEnqueue(() => _name.Focus(FocusState.Programmatic));
            return;
        }

        // Ignore plain typing / navigation keys so users cannot bind e.g. "A".
        if (!e.Keys.Values.Any(IsBindableKey)) return;

        e.IsHandled = true;

        var keys = e.Keys.Values
            .OrderByDescending(k => k is Key.LWin or Key.RWin)
            .ThenBy(k => k)
            .ToArray();

        Apply(keys, isMouse: false, isDoubleClick: false);
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (!e.Keys.Values.Any(k => k is >= Key.MouseLeft and <= Key.MouseXButton2)) return;

        if (!_pointerOverHotKeys)
        {
            _queue.TryEnqueue(() => _name.Focus(FocusState.Programmatic));
            return;
        }

        e.IsHandled = true;

        var isDoubleClick = IsDoubleClick(e.CurrentKey);
        var keys = e.Keys.Values.OrderBy(k => k).ToArray();

        Apply(keys, isMouse: true, isDoubleClick: isDoubleClick);
    }

    private void Apply(Key[]? keys, bool isMouse, bool isDoubleClick)
    {
        Profile.HotKeys = keys is { Length: > 0 } ? keys : null;
        Profile.IsMouse = isMouse;
        Profile.IsDoubleClick = isDoubleClick;

        _queue.TryEnqueue(() =>
        {
            UpdateHotKeyField();
            UpdateSummary();
        });

        Save();
    }

    /// <summary>
    /// Commits the edit straight away. Profiles persist on every change, which is why the
    /// "save on exit" switch and the manual Save button no longer exist.
    /// </summary>
    private void Save() => _callbacks.Save();

    private bool IsDoubleClick(Key currentKey)
    {
        var isDoubleClick = false;
        var now = Environment.TickCount;

        if (now - _lastClickTime < 500 && _lastClickKey == currentKey)
            isDoubleClick = true;

        _lastClickTime = now;
        _lastClickKey = currentKey;
        return isDoubleClick;
    }

    /// <summary>
    /// Keys worth binding. Mirrors the original rule: a modifier must be present, or the key must
    /// be a function/navigation/symbol key that is unlikely to be needed as plain input.
    /// </summary>
    private static bool IsBindableKey(Key key)
    {
        if (key is Key.Ctrl or Key.Alt or Key.Shift or Key.LWin or Key.RWin) return true;
        if (key is >= Key.F1 and <= Key.F23) return true;
        if (key is Key.Multiply or Key.Add or Key.Subtract or Key.Divide) return true;
        if (key is Key.PageUp or Key.PageDown or Key.Print or Key.PrintScreen or Key.Insert or Key.Delete) return true;

        return key is >= Key.MouseLeft and <= Key.MouseXButton2;
    }

    // ---- Official glyphs and resource lookups --------------------------------

    private const string ProfileGlyph = "\uE765";

    /// <summary>
    /// Row metrics for the expanded area.
    /// <para>
    /// Honest note on the standard: Microsoft publishes <b>no</b> pixel height for an expanded
    /// settings row. <i>Guidelines for touch targets</i> / <i>Touch interactions</i> only define
    /// <b>touch target sizes</b> — Fluent Standard 40×40 epx (a floor, not a recommended row height)
    /// and touch-optimised 44×44 epx — and WinUI's own <c>ListViewItemMinHeight</c> is 40. The
    /// closest real-world reference is the Windows 11 Settings page, whose rows measure ~64px.
    /// </para>
    /// <para>
    /// <b>60</b> is therefore the requested value: comfortably above every published floor and
    /// close to the Windows 11 Settings figure. It is applied through <c>MinHeight</c>, so the
    /// control (32px) is untouched. Padding stays modest because the row's height comes from
    /// <c>MinHeight</c>, not from padding.
    /// </para>
    /// </summary>
    private static readonly Thickness CompactRowPadding = new(16, 4, 16, 4);

    private const double CompactRowMinHeight = 60;

    /// <summary>
    /// <summary>
    /// The theme resources the stock <see cref="Button"/> style reads for its border, narrowed to a bare icon.
    /// <para>
    /// The rest state alone was neutralised at first, on the reasoning that the interactive states are the
    /// platform's own feedback and worth keeping. Asked for: only the icon should show — so the border is
    /// neutralised in the interactive states too. The <b>fill</b> those states use is deliberately left alone:
    /// the button keeps the stock hover and press feedback, which is what makes it still read as a button.
    /// No template is replaced and no colour is invented.
    /// </para>
    /// </summary>
    private const string ButtonBackgroundResourceKey = "ButtonBackground";

    private const string ButtonBorderBrushResourceKey = "ButtonBorderBrush";
    private const string ButtonBorderBrushPointerOverResourceKey = "ButtonBorderBrushPointerOver";
    private const string ButtonBorderBrushPressedResourceKey = "ButtonBorderBrushPressed";
    private const string ButtonBorderBrushDisabledResourceKey = "ButtonBorderBrushDisabled";

    private static string Loc(string key) => LocalizationService.Get(key);

    /// <summary>Resolves an official resource without risking a KeyNotFoundException.</summary>
    private static bool TryFindResource(string key, out object? value)
    {
        value = null;
        return Application.Current?.Resources is { } resources && resources.TryGetValue(key, out value);
    }

    /// <summary>Resolves an official text style without risking a KeyNotFoundException.</summary>
    private static bool TryFindStyle(string key, out Style style)
    {
        style = null!;

        if (Application.Current?.Resources is not { } resources) return false;
        if (!resources.TryGetValue(key, out var value) || value is not Style found) return false;

        style = found;
        return true;
    }

    /// <summary>
    /// The destructive button, taken from <c>DestructiveButtonTemplate</c>.
    /// <para>
    /// The template exists because a solid critical fill has to override six <c>Button*</c> theme keys
    /// on the element itself (the stock style repaints ContentPresenter's background, border and
    /// foreground on hover and press), and those overrides have to be declared in XAML: the expand
    /// button assigns <c>Transparent</c> from code because that value is theme-independent, while a
    /// semantic colour fetched in code resolves against the application theme instead of the one in
    /// effect — the trap App.xaml's styles are written to avoid.
    /// </para>
    /// <para>
    /// A missing template is not fatal: the row still works, it is just not red.
    /// </para>
    /// </summary>
    private static Button CreateDestructiveButton()
    {
        if (TryFindResource("DestructiveButtonTemplate", out var found)
            && found is DataTemplate template
            && template.LoadContent() is Button fromTemplate)
        {
            return fromTemplate;
        }

        return new Button();
    }
}
