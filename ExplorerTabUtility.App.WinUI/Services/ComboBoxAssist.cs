using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// Native-only tuning for the three gaps in the stock <see cref="ComboBox"/>:
/// <list type="number">
/// <item>its dropdown items are as tall as a settings row;</item>
/// <item>its dropdown snaps open with no transition;</item>
/// <item>its dropdown has no material, so it opens as a flat panel on a window that has Mica.</item>
/// </list>
/// All three are handled with official WinUI pieces — a <c>BasedOn</c> named style and the stock
/// <see cref="Popup"/> part — so no control template is replaced.
/// </summary>
internal static class ComboBoxAssist
{
    /// <summary>
    /// WinUI's own named style for dropdown items. Deriving from it keeps the official template
    /// (visual states, corner radius, keyboard behaviour) and only overrides the metrics.
    /// </summary>
    private const string DefaultItemStyleKey = "DefaultComboBoxItemStyle";

    /// <summary>Height of one dropdown row. Well under a settings card (~64px) but still comfortable.</summary>
    private const double ItemMinHeight = 28;

    private static readonly Thickness ItemPadding = new(12, 3, 12, 3);

    /// <summary>Marks a ComboBox as already configured, so a cached page does not stack handlers.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ComboBox, object> Configured = new();

    public static void Configure(ComboBox comboBox)
    {
        if (comboBox is null) return;
        if (Configured.TryGetValue(comboBox, out _)) return;

        Configured.Add(comboBox, new object());

        ApplyCompactItems(comboBox);

        // The template is applied lazily, so try at Loaded and again when the list first opens.
        comboBox.Loaded += (_, _) => ConfigureDropDown(comboBox);
        comboBox.DropDownOpened += (_, _) => ConfigureDropDown(comboBox);
    }

    /// <summary>
    /// Shrinks the dropdown rows.
    /// <para>
    /// WinUI's official "compact sizing" dictionary is not shipped with this Windows App SDK
    /// version (probed at runtime: the <c>DensityStyles/Compact.xaml</c> resource does not resolve),
    /// so the same effect is achieved by deriving from WinUI's named item style and overriding only
    /// <c>MinHeight</c> and <c>Padding</c>.
    /// </para>
    /// </summary>
    private static void ApplyCompactItems(ComboBox comboBox)
    {
        try
        {
            if (Application.Current?.Resources is not { } resources)
                return;

            if (!resources.TryGetValue(DefaultItemStyleKey, out var value) || value is not Style baseStyle)
            {
                StartupLog.Step($"ComboBoxAssist: {DefaultItemStyleKey} not found, keeping default item height");
                return;
            }

            var compact = new Style { TargetType = typeof(ComboBoxItem), BasedOn = baseStyle };
            compact.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, ItemMinHeight));
            compact.Setters.Add(new Setter(Control.PaddingProperty, ItemPadding));

            comboBox.ItemContainerStyle = compact;
        }
        catch (Exception ex)
        {
            StartupLog.Fail("ComboBoxAssist compact items", ex);
        }
    }

    private static void ConfigureDropDown(ComboBox comboBox)
    {
        try
        {
            comboBox.ApplyTemplate();

            if (FindPopup(comboBox) is not { } popup)
            {
                StartupLog.Step("ComboBoxAssist: Popup part not found");
                return;
            }

            ApplyDropDownMaterial(popup);
            EnableDropDownTransition(popup);
        }
        catch (Exception ex)
        {
            StartupLog.Fail("ComboBoxAssist dropdown", ex);
        }
    }

    /// <summary>
    /// Puts a system material behind the dropdown, when the platform actually renders one.
    /// <para>
    /// The list is not part of the window: WinUI opens it as a
    /// <c>ShouldConstrainToRootBounds=false</c> <see cref="Popup"/>, which gets a real top-level HWND
    /// of its own. An <c>AcrylicBrush</c> inside it cannot blur anything beyond that window
    /// (microsoft-ui-xaml#9523), so <see cref="Popup.SystemBackdrop"/> is the documented way to reach
    /// the compositor across the boundary.
    /// </para>
    /// <para>
    /// ⛔ It is a no-op on this kind of popup today. The property only renders when the popup is
    /// backed by its own Composition target, and assignment still succeeds and returns normally
    /// whether or not it renders — verified by grabbing the screen with the dropdown open and
    /// closed: across the whole 3840×2160 desktop only a 22×22 pixel patch changed (the cursor),
    /// while this very line had logged "applied". Reported as microsoft-ui-xaml#10087 and #10677
    /// (1.6.1 and 1.7.3 respectively, the latter closed as a duplicate of the former and unfixed).
    /// </para>
    /// <para>
    /// Kept deliberately: it costs nothing, and the moment the platform starts honouring it the
    /// dropdown picks up true desktop-visible acrylic with no further change. The material the user
    /// actually sees today comes from the <c>ComboBoxDropDownBackground</c> override in App.xaml,
    /// which is the in-app acrylic fallback the platform design docs prescribe for this case.
    /// </para>
    /// </summary>
    private static void ApplyDropDownMaterial(Popup popup)
    {
        if (popup.SystemBackdrop is not null) return;

        popup.SystemBackdrop = new DesktopAcrylicBackdrop();
        StartupLog.Step($"ComboBoxAssist: popup backdrop set (rootBound={popup.ShouldConstrainToRootBounds}, platform may not render it)");
    }

    /// <summary>
    /// Gives the dropdown an open/close transition.
    /// <para>
    /// The stock ComboBox template declares its <c>Popup</c> without any <c>ChildTransitions</c>
    /// (verified against the Windows App SDK <c>generic.xaml</c>), so the list appears instantly.
    /// WinUI's <c>MenuPopupThemeTransition</c> (used by menus in UWP) is <b>not</b> shipped in
    /// WinUI 3 and <see cref="PopupThemeTransition"/> produced no observable motion, so the
    /// entrance transition — which is reliably animated — is attached here rather than replacing
    /// the whole control template.
    /// </para>
    /// <para>
    /// The part is located by walking the visual tree: <c>FindName</c> does not resolve template
    /// parts of this control (probed — it returns null even after <c>ApplyTemplate</c>).
    /// </para>
    /// </summary>
    private static void EnableDropDownTransition(Popup popup)
    {
        try
        {
            if (popup.ChildTransitions.Count > 0) return;

            popup.ChildTransitions = new TransitionCollection { new EntranceThemeTransition() };
            StartupLog.Step("ComboBoxAssist: dropdown transition enabled");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("ComboBoxAssist dropdown transition", ex);
        }
    }

    /// <summary>Depth-limited search for the template's Popup, which is a visual child of the control.</summary>
    private static Popup? FindPopup(DependencyObject root, int depth = 0)
    {
        if (depth > 8) return null;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is Popup popup) return popup;
            if (FindPopup(child, depth + 1) is { } found) return found;
        }

        return null;
    }
}
