using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// Gives content its entrance as it arrives: a page's blocks in order, or an <see cref="ItemsControl"/>'s items.
/// </summary>
/// <remarks>
/// <para>
/// The entrance has to start at the moment the content appears on screen, and for a list that moment is not
/// "when the items were added": an <c>ItemsControl</c> generates its item containers during the layout pass that
/// follows, so at the moment of the add there is nothing yet to animate. What the two share is the layout
/// pass, which is what the pending flag waits for.
/// </para>
/// <para>
/// <b>Two shapes of host, one wave.</b> A page is a <see cref="Panel"/> of blocks — heading, toolbar, rows — so
/// those blocks are the host's targets; an <c>ItemsControl</c>'s targets are its items. Both are animated by
/// <see cref="Motion.PosterEnter"/>, which is what makes a page and the list inside it read as one arrival
/// instead of two: the list is given its position in the page's wave rather than starting its own.
/// </para>
/// <para>
/// Deliberately not <c>ItemContainerTransitions</c>. That is the platform's own item entrance, and it is
/// what this app used before the stagger moved into <see cref="Motion"/>. Two things it cannot do decided
/// the move: it has no property for the interval between items — so the pace was the runtime's choice, and
/// what the runtime chose read as the items arriving together — and it cannot stop the interval from
/// growing, so a long list would have kept the wave running well past the point where the user was already
/// looking at the content.
/// </para>
/// <para>
/// Items that arrived after the first wave get an entrance of their own: what is animated is the run of
/// items that has not been animated yet, and the stagger is indexed from the start of <em>that</em> run. One
/// card added by hand therefore enters alone, and a list handed over in one go enters as a wave.
/// </para>
/// </remarks>
internal static class ListEntrance
{
    private sealed class State
    {
        /// <summary>The element the wave belongs to, and how to walk what it holds.</summary>
        public FrameworkElement Host = null!;
        public Func<int> Count = null!;
        public Func<int, UIElement?> Target = null!;

        /// <summary>
        /// Where this host's first target sits in the wave being played. Non-zero when the host is a list whose
        /// page put a heading and a toolbar in front of it — see <see cref="ReplayColumn"/>.
        /// </summary>
        public int WaveIndex;

        /// <summary>Set when the host has taken on content, cleared once a pass has animated it.</summary>
        public bool Pending;

        /// <summary>
        /// How many leading targets already have their entrance. A new target's position in its own batch is its
        /// index minus this, which is what keeps a late arrival from inheriting the whole list's delays.
        /// </summary>
        public int Animated;

        /// <summary>
        /// The visuals the last entrance was started on, kept so that a host whose elements were given new
        /// ones underneath it can be noticed — see <see cref="PlayedOnLiveVisuals"/>.
        /// </summary>
        public Visual?[]? PlayedOn;

        /// <summary>How many times the entrance has been restarted for a rebuilt host.</summary>
        public int Restarts;
    }

    /// <summary>Per-host state. Weak so a discarded page does not keep its content alive.</summary>
    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    /// <summary>
    /// How many times an entrance may be restarted because the content was rebuilt underneath it.
    /// </summary>
    /// <remarks>
    /// Bounded so that a host which never stops rebuilding cannot keep the entrance running forever. Three is
    /// enough for the case this exists for — a page re-attached, which settles within a pass or two.
    /// </remarks>
    private const int RestartLimit = 3;

    // ---- A list: the wave runs over the items of an ItemsControl --------------------------------------

    /// <summary>
    /// Marks <paramref name="list"/> as having just taken on items, so they are given their entrance on the
    /// next layout pass. <paramref name="waveIndex"/> positions them inside a wave the caller is playing.
    /// </summary>
    public static void Queue(ItemsControl list, int waveIndex = 0)
    {
        if (list is null) return;

        var state = StateOfList(list);
        state.WaveIndex = waveIndex;
        state.Pending = true;
    }

    /// <summary>
    /// Plays the entrance again for a list whose items are already there — the page is being entered again,
    /// or the window is being shown again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because queuing alone made the entrance a once-per-process event. That is what the
    /// trigger it replaced did too — the platform's item transition only fires for items a control takes on
    /// — and it is wrong for a cached page and for a tray-resident window: neither of those hands the list
    /// its items again, so nothing animates, and the only way to ever see the effect was to restart the app.
    /// </para>
    /// <para>
    /// It plays on the spot as well as queueing, because one of the callers has no layout pass coming:
    /// showing a window that is already laid out does not invalidate layout, so waiting for a pass would
    /// leave the flag set forever. If the list is not ready at this moment — items handed over during a
    /// navigation, whose containers do not exist yet — the attempt does nothing and the pass plays instead.
    /// </para>
    /// </remarks>
    public static void Replay(ItemsControl list, int waveIndex = 0)
    {
        if (list is null) return;

        var state = StateOfList(list);
        Forget(state);
        state.WaveIndex = waveIndex;
        state.Pending = true;
        Play(state);
    }

    /// <summary>
    /// Forgets what has been animated, for a list that is about to be handed its items again.
    /// </summary>
    /// <remarks>
    /// Needed because the two cases are indistinguishable from the item count alone: one item fewer means
    /// either "something was removed, nothing left to animate" or "the list is being filled again, everything
    /// is new". Only the caller knows which it is.
    /// </remarks>
    public static void Reset(ItemsControl list)
    {
        if (list is null) return;
        if (!States.TryGetValue(list, out var state)) return;

        Forget(state);
    }

    // ---- A column: the wave runs over the children of a panel ----------------------------------------

    /// <summary>
    /// Plays the entrance for a page: its content column's children, in order, as one wave.
    /// </summary>
    /// <param name="column">The page's content column.</param>
    /// <param name="ownWave">
    /// A child that is a list and runs the wave itself — an <c>ItemsControl</c> whose items are the wave's
    /// remaining positions. It is skipped here; its items are animated when the caller hands it this wave's
    /// next index. It still counts as a position, so the blocks after it keep theirs.
    /// </param>
    /// <returns>
    /// The index the wave reached, which is what the skipped list's own items should start from.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the page half of the app's arrival motion. It replaces the platform's page transition on
    /// purpose: that transition does not swap the pages at once — the outgoing page stays on screen until it
    /// has run, measured at ~200ms on this window — so an entrance handed over at navigation time played and
    /// finished behind a page the user was still looking at. It is also a second entrance for the same
    /// arrival, which is the competition rather than the feature.
    /// </para>
    /// <para>
    /// A page is entered again on every navigation, so the wave is replayed rather than queued: a cached page
    /// is handed no new children, and without the replay its blocks would animate once per process.
    /// </para>
    /// </remarks>
    public static int ReplayColumn(Panel column, UIElement? ownWave = null)
    {
        if (column is null) return 0;

        var state = StateOfColumn(column);
        Forget(state);
        state.WaveIndex = 0;
        state.Pending = true;
        Play(state);

        if (ownWave is null) return column.Children.Count;

        var index = column.Children.IndexOf(ownWave);
        return index < 0 ? column.Children.Count : index;
    }

    // ---- Shared ---------------------------------------------------------------------------------------

    private static State StateOfList(ItemsControl list) =>
        StateOf(list, () => new State
        {
            Host = list,
            Count = () => list.Items.Count,
            Target = index => Target(list, index)
        });

    private static State StateOfColumn(Panel column) =>
        StateOf(column, () => new State
        {
            Host = column,
            Count = () => column.Children.Count,
            Target = index => index < column.Children.Count ? column.Children[index] : null
        });

    private static State StateOf(FrameworkElement host, Func<State> create)
    {
        if (States.TryGetValue(host, out var existing)) return existing;

        var state = create();
        host.LayoutUpdated += (_, _) => Play(state);
        States.Add(host, state);

        return state;
    }

    /// <summary>Forgets what has been animated, for a host that is about to be filled again.</summary>
    private static void Forget(State state)
    {
        state.Animated = 0;

        // What the previous entrance was started on describes content that is about to stop existing.
        state.PlayedOn = null;
        state.Restarts = 0;
    }

    /// <summary>
    /// The element to animate for the item at <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// An item that is itself an element is preferred over the container the panel generated for it: the item
    /// element is held by the panel rather than generated, so it survives the panel rebuilding its containers
    /// — which is what happens when a cached page is re-attached. Data-templated lists have no such element
    /// (their items are plain objects) and fall back to the container.
    /// </remarks>
    private static UIElement? Target(ItemsControl list, int index)
    {
        if (index < list.Items.Count && list.Items[index] is UIElement item)
            return item;

        return list.ContainerFromIndex(index) as UIElement;
    }

    private static void Play(State state)
    {
        if (state.PlayedOn is not null && !PlayedOnLiveVisuals(state)) return;

        if (!state.Pending) return;

        // Content that is not in the tree must not be animated: its elements have no visual yet, or — worse —
        // one that is about to be replaced when the page is re-attached. The item elements of a list that is
        // being handed over again do exist at that moment, which is exactly why this is checked here rather
        // than left to the target lookup. Waiting for the pass that follows re-attachment both puts the host
        // back on screen and gives it its final containers.
        if (!state.Host.IsLoaded) return;

        var count = state.Count();

        // Targets can be removed at any time, and the watermark cannot point past the end. Clamping is what
        // keeps the meaning of the watermark correct after a removal: the first N surviving targets are still
        // the ones that have been animated.
        if (state.Animated > count) state.Animated = count;

        if (count == 0)
        {
            // Nothing to animate. Cleared rather than left set, or every later layout pass would come back
            // to look at an empty host.
            state.Pending = false;
            return;
        }

        var targets = new UIElement[count];

        for (var i = 0; i < count; i++)
        {
            // Not realised yet — the containers are generated during this very pass, so the ones that are
            // missing will be there for the next one. Leaving the flag set is what retries.
            if (state.Target(i) is not { } element) return;

            targets[i] = element;
        }

        for (var i = state.Animated; i < count; i++)
        {
            // The wave index only positions the first run — the one the caller composed. A continuation is a
            // run of its own: a card added by hand has no heading in front of it and must not wait for one.
            var position = state.Animated == 0 ? state.WaveIndex + i : i - state.Animated;

            Motion.PosterEnter(targets[i], position);
        }

        state.Animated = count;
        state.Pending = false;

        // What the entrance was actually started on. Asked again on the next pass: an element that is moved
        // to a new parent is given a new visual, and an animation left on the old one is gone — it starts,
        // reports success and plays nowhere at all, which is the one failure this effect kept having.
        state.PlayedOn = new Visual?[count];
        for (var i = 0; i < count; i++)
            state.PlayedOn[i] = VisualOf(targets[i]);
    }

    /// <summary>
    /// Whether the elements the entrance was started on still have the visuals it was started on.
    /// </summary>
    /// <remarks>
    /// The host cannot be asked whether it is about to rebuild itself, so it is asked afterwards instead:
    /// if the visuals changed, the entrance went nowhere and is started again on the current ones. This is
    /// what makes the effect survive a cached page being re-attached, rather than depending on getting the
    /// timing right by guesswork.
    /// </remarks>
    /// <returns>True when the entrance is still on live visuals, false when it was restarted.</returns>
    private static bool PlayedOnLiveVisuals(State state)
    {
        var played = state.PlayedOn!;
        var stale = false;

        for (var i = 0; i < played.Length; i++)
        {
            if (state.Target(i) is not { } element || !ReferenceEquals(VisualOf(element), played[i]))
            {
                stale = true;
                break;
            }
        }

        if (!stale)
        {
            state.PlayedOn = null;
            state.Restarts = 0;
            return true;
        }

        if (state.Restarts >= RestartLimit)
        {
            StartupLog.Step($"entrance: the content kept being rebuilt, leaving it to settle ({state.Restarts})");
            state.PlayedOn = null;
            state.Restarts = 0;
            return true;
        }

        state.Restarts++;
        StartupLog.Step($"entrance: the content was rebuilt under the entrance, playing it again ({state.Restarts})");

        state.PlayedOn = null;
        state.Animated = 0;
        state.Pending = true;
        return false;
    }

    private static Visual VisualOf(UIElement element) =>
        ElementCompositionPreview.GetElementVisual(element);
}
