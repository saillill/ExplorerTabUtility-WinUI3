using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Hooks;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.WinAPI;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// The tab-search picker shown for the <c>TabSearch</c> hotkey action.
/// <para>
/// Built entirely from the stock WinUI primitives (<see cref="ContentDialog"/>, <see cref="TextBox"/>,
/// <see cref="ListView"/>, <see cref="Button"/>, <see cref="TextBlock"/>) with theme resources — no
/// custom templates or hand-drawn chrome. The list comes from <see cref="ExplorerWatcher.GetWindows"/>
/// and a choice is executed through <see cref="ExplorerWatcher.SwitchTo"/>, which wires those
/// otherwise-dead APIs back into the app (AUD-06).
/// </para>
/// <para>
/// It is a <see cref="ContentDialog"/> built here rather than by <c>ContentDialogService</c> (the
/// picker needs its own layout and keyboard handling), but two rules of that service still apply and
/// are honoured explicitly: it must take the shared <c>DialogGate</c> — WinUI allows only one
/// ContentDialog per <see cref="XamlRoot"/>, and a collision throws an exception that both callers
/// swallow, leaving the hotkey looking dead — and it must resolve the dialog theme from the same
/// place, or it renders against the frozen launch-time application theme.
/// </para>
/// </summary>
public static class TabSearchDialog
{
    /// <summary>Only one picker may be up at a time; a second request while open is ignored.</summary>
    private static bool _isOpen;

    public static async Task ShowAsync(ExplorerWatcher watcher, XamlRoot xamlRoot, ContentDialogService dialogs)
    {
        if (watcher is null) throw new ArgumentNullException(nameof(watcher));
        if (xamlRoot is null) throw new ArgumentNullException(nameof(xamlRoot));
        if (dialogs is null) throw new ArgumentNullException(nameof(dialogs));

        if (_isOpen) return;
        _isOpen = true;

        try
        {
            await ShowCoreAsync(watcher, xamlRoot, dialogs);
        }
        finally
        {
            _isOpen = false;
        }
    }

    private static async Task ShowCoreAsync(ExplorerWatcher watcher, XamlRoot xamlRoot, ContentDialogService dialogs)
    {
        // Take the process-wide one-dialog slot before building anything. Awaiting it returns to the
        // message loop, so this queues behind whatever dialog is already up instead of throwing.
        await dialogs.DialogGate.WaitAsync();
        try
        {
            // The window list is built from blocking cross-process COM reads (LocationURL, the
            // selected-items collection). Doing that on the UI thread froze the settings window for as
            // long as any single Explorer window took to answer. Load on a pool thread — the shell
            // objects live in the MTA, which is where the watcher's own callbacks run — and keep the
            // UI thread for building the dialog.
            var records = await Task.Run(watcher.GetWindows);

            // Mutable so a history clear can rebuild it without recreating the dialog.
            var entries = BuildEntries(records);

            var searchBox = new TextBox
            {
                PlaceholderText = LocalizationService.Get("TabSearchPlaceholder"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 320,
                MinHeight = 120
            };

            var emptyText = new TextBlock
            {
                Text = LocalizationService.Get("TabSearchNoResults"),
                Opacity = 0.7,
                Visibility = Visibility.Collapsed
            };

            // Inline "clear history" confirmation. It is NOT a second ContentDialog: WinUI forbids two
            // open dialogs at once, so the confirm is expressed with in-place native controls (AUD-20).
            var confirmHeader = new TextBlock
            {
                Text = LocalizationService.Get("ConfirmClearHistory"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Visibility = Visibility.Collapsed
            };

            var confirmText = new TextBlock
            {
                Text = LocalizationService.Get("ClearHistoryConfirm"),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

            var confirmYes = new Button { Content = LocalizationService.Get("Yes") };
            var confirmNo = new Button { Content = LocalizationService.Get("No") };

            var confirmRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Visibility = Visibility.Collapsed
            };
            confirmRow.Children.Add(confirmYes);
            confirmRow.Children.Add(confirmNo);

            var hint = new TextBlock
            {
                Text = LocalizationService.Get("TabSearchHint"),
                Opacity = 0.6,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };

            var panel = new StackPanel { Spacing = 8, Width = 460 };
            panel.Children.Add(searchBox);
            panel.Children.Add(list);
            panel.Children.Add(emptyText);
            panel.Children.Add(confirmHeader);
            panel.Children.Add(confirmText);
            panel.Children.Add(confirmRow);
            panel.Children.Add(hint);

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = LocalizationService.Get("TabSearchTitle"),
                Content = panel,
                // A ContentDialog is hosted in its own popup tree, so it never inherits the theme this
                // app sets on the window's root element — it resolves against Application.RequestedTheme,
                // which is a snapshot taken in the Application constructor. Resolving per show through
                // the same helper the other dialogs use is what keeps the picker in step with the window
                // after a runtime theme switch.
                RequestedTheme = ContentDialogService.ResolveTheme(),
                // "Clear closed windows history" — README's "single click" affordance. Clicking it only
                // ARMS the confirmation; it never closes the picker (the handler cancels the click).
                SecondaryButtonText = LocalizationService.Get("ClearHistory"),
                CloseButtonText = LocalizationService.Get("Close"),
                DefaultButton = ContentDialogButton.Close
            };

            void ApplyFilter()
            {
                var query = searchBox.Text?.Trim() ?? string.Empty;
                var filtered = string.IsNullOrEmpty(query)
                    ? entries
                    : entries.Where(entry => entry.Matches(query)).ToList();

                list.ItemsSource = filtered;
                list.SelectedIndex = filtered.Count > 0 ? 0 : -1;
                emptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            void HideConfirm()
            {
                confirmHeader.Visibility = Visibility.Collapsed;
                confirmText.Visibility = Visibility.Collapsed;
                confirmRow.Visibility = Visibility.Collapsed;
            }

            // Enter commits the highlighted entry. Modifier state is read at this instant (not from the
            // hook event that opened the picker) so Shift/Ctrl pressed now take effect.
            async void CommitSelected()
            {
                if (list.SelectedItem is not Entry entry) return;

                var shiftDown = IsDown(VirtualKey.LeftShift) || IsDown(VirtualKey.RightShift);
                var ctrlDown = IsDown(VirtualKey.LeftControl) || IsDown(VirtualKey.RightControl);

                // Shift → open in a NEW window; Ctrl → force a duplicate; otherwise reuse-or-open a tab.
                var asTab = !shiftDown;
                var duplicate = ctrlDown;

                dialog.Hide();

                try
                {
                    await watcher.SwitchTo(entry.Record.Location, entry.Record.Handle, entry.Record.SelectedItems, asTab, duplicate);
                }
                catch (Exception ex)
                {
                    StartupLog.Fail("TabSearchDialog.CommitSelected", ex);
                }
            }

            searchBox.TextChanged += (_, _) => ApplyFilter();

            searchBox.KeyDown += (_, e) =>
            {
                // KeyRoutedEventArgs.Key is Windows.System.VirtualKey (not the app's own enum), so the
                // labels are fully qualified here.
                switch (e.Key)
                {
                    case Windows.System.VirtualKey.Down:
                        MoveSelection(list, +1);
                        e.Handled = true;
                        break;

                    case Windows.System.VirtualKey.Up:
                        MoveSelection(list, -1);
                        e.Handled = true;
                        break;

                    case Windows.System.VirtualKey.Enter:
                        e.Handled = true;
                        CommitSelected();
                        break;
                }
            };

            // Double-click on a row behaves like Enter.
            list.DoubleTapped += (_, _) => CommitSelected();

            // Clearing history is destructive, so the secondary button only reveals an inline confirm.
            dialog.SecondaryButtonClick += (_, args) =>
            {
                args.Cancel = true; // keep the picker open
                confirmHeader.Visibility = Visibility.Visible;
                confirmText.Visibility = Visibility.Visible;
                confirmRow.Visibility = Visibility.Visible;
            };

            confirmYes.Click += async (_, _) =>
            {
                try
                {
                    watcher.ClearClosedWindows();

                    // Re-read the list so the cleared history disappears immediately — off the UI
                    // thread, for the same reason as the initial load above.
                    entries = BuildEntries(await Task.Run(watcher.GetWindows));
                }
                catch (Exception ex)
                {
                    // async void handler: nothing above it can catch, so contain it here.
                    StartupLog.Fail("TabSearchDialog.ClearHistory", ex);
                }

                HideConfirm();
                ApplyFilter();
            };

            confirmNo.Click += (_, _) => HideConfirm();

            dialog.Opened += (_, _) =>
            {
                ApplyFilter();
                searchBox.Focus(FocusState.Programmatic);
            };

            await dialog.ShowAsync();
        }
        finally
        {
            // Release the shared one-dialog slot; _isOpen is cleared by ShowAsync's own finally.
            dialogs.DialogGate.Release();
        }
    }

    /// <summary>Moves the highlight by <paramref name="delta"/> and keeps it in view.</summary>
    private static void MoveSelection(ListView list, int delta)
    {
        var count = list.Items.Count;
        if (count == 0) return;

        var index = list.SelectedIndex < 0 ? 0 : list.SelectedIndex + delta;
        index = Math.Clamp(index, 0, count - 1);

        list.SelectedIndex = index;

        if (list.SelectedItem is not null)
            list.ScrollIntoView(list.SelectedItem);
    }

    private static bool IsDown(VirtualKey key) => KeyboardSimulator.IsKeyPressed((int)key);

    private static List<Entry> BuildEntries(IReadOnlyCollection<WindowRecord> records)
    {
        var entries = new List<Entry>(records.Count);

        foreach (var record in records)
        {
            var location = record.Location ?? string.Empty;
            var name = record.Name ?? string.Empty;

            var display = string.IsNullOrWhiteSpace(name) ? location : $"{name}  ·  {location}";
            if (string.IsNullOrWhiteSpace(display)) continue;

            entries.Add(new Entry(record, display));
        }

        return entries;
    }

    /// <summary>
    /// A single row. <see cref="ToString"/> is what the stock <see cref="ListView"/> renders when no
    /// item template is supplied — no template needed, so no custom drawing.
    /// </summary>
    private sealed class Entry(WindowRecord record, string display)
    {
        public WindowRecord Record { get; } = record;
        public string Display { get; } = display;

        public bool Matches(string query) =>
            Display.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        public override string ToString() => Display;
    }
}
