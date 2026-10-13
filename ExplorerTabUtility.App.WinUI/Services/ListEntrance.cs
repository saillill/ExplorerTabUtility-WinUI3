using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// Gives an <see cref="ItemsControl"/>'s items their entrance as they arrive.
/// </summary>
/// <remarks>
/// <para>
/// The entrance has to start at the moment the items appear on screen, and for a list that moment is not
/// "when they were added": an <c>ItemsControl</c> generates its item containers during the layout pass that
/// follows, so at the moment of the add there is nothing yet to animate. What the two share is the layout
/// pass, which is what the pending flag waits for.
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
        /// <summary>Set when the list has taken on items, cleared once a pass has animated them.</summary>
        public bool Pending;

        /// <summary>
        /// How many leading items already have their entrance. A new item's position in its own batch is its
        /// index minus this, which is what keeps a late arrival from inheriting the whole list's delays.
        /// </summary>
        public int Animated;
    }

    /// <summary>Per-control state. Weak so a discarded page does not keep its list alive.</summary>
    private static readonly ConditionalWeakTable<ItemsControl, State> States = new();

    /// <summary>
    /// Marks <paramref name="list"/> as having just taken on items, so they are given their entrance on the
    /// next layout pass.
    /// </summary>
    public static void Queue(ItemsControl list)
    {
        if (list is null) return;
        StateOf(list).Pending = true;
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
    /// It plays on the spot as well as queueing, because one of the two callers has no layout pass coming:
    /// showing a window that is already laid out does not invalidate layout, so waiting for a pass would
    /// leave the flag set forever. If the list is not ready at this moment — items handed over during a
    /// navigation, whose containers do not exist yet — the attempt does nothing and the pass plays instead.
    /// </para>
    /// </remarks>
    public static void Replay(ItemsControl list)
    {
        if (list is null) return;

        Reset(list);
        Queue(list);
        Play(list, StateOf(list));
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
        if (States.TryGetValue(list, out var state)) state.Animated = 0;
    }

    private static State StateOf(ItemsControl list) =>
        States.GetValue(list, static l =>
        {
            var created = new State();
            l.LayoutUpdated += (_, _) => Play(l, created);
            return created;
        });

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

    private static void Play(ItemsControl list, State state)
    {
        if (!state.Pending) return;

        // A list that is not in the tree must not be animated: its elements have no visual yet, or — worse —
        // one that is about to be replaced when the page is re-attached. The item elements of a list that is
        // being handed over again do exist at that moment, which is exactly why this is checked here rather
        // than left to the target lookup. Waiting for the pass that follows re-attachment both puts the list
        // back on screen and gives it its final containers.
        if (!list.IsLoaded) return;

        var count = list.Items.Count;

        // Items can be removed at any time, and the watermark cannot point past the end. Clamping is what
        // keeps the meaning of the watermark correct after a removal: the first N surviving items are still
        // the ones that have been animated.
        if (state.Animated > count) state.Animated = count;

        if (count == 0)
        {
            // Nothing to animate. Cleared rather than left set, or every later layout pass would come back
            // to look at an empty list.
            state.Pending = false;
            return;
        }

        var targets = new UIElement[count];

        for (var i = 0; i < count; i++)
        {
            // Not realised yet — the containers are generated during this very pass, so the ones that are
            // missing will be there for the next one. Leaving the flag set is what retries.
            if (Target(list, i) is not { } element) return;

            targets[i] = element;
        }

        for (var i = state.Animated; i < count; i++)
            Motion.PosterEnter(targets[i], i - state.Animated);

        state.Animated = count;
        state.Pending = false;
    }
}
