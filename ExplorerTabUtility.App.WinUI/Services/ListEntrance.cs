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
/// The entrance has to start at the moment the items appear, and for a list that moment is not "when they
/// were added": an <c>ItemsControl</c> generates its item containers during the layout pass that follows,
/// so at the moment of the add there is nothing yet to animate. What the two events share is the layout
/// pass, which is what this waits for.
/// </para>
/// <para>
/// Deliberately not <c>ItemContainerTransitions</c>. That is the platform's own item entrance, and it is
/// what this app used before the stagger moved into <see cref="Motion"/>. Two things it cannot do decided
/// the move: it has no property for the interval between items — so the pace was the runtime's choice, and
/// what the runtime chose read as the items arriving together — and it cannot stop the interval from
/// growing, so a long list would have kept the wave running well past the point where the user was already
/// looking at the content. Both are the entrance's own business, and both are why a shared behavior has
/// them.
/// </para>
/// <para>
/// Items that arrived after the first wave get an entrance of their own: what is animated is the run of
/// items that has not been animated yet, and the stagger is indexed from the start of <em>that</em> run. One
/// card added by hand therefore enters alone, and a list rebuilt in one go enters as a wave.
/// </para>
/// <para>
/// One subscription per control, kept for the control's lifetime. The handler's first act is a boolean that
/// is false except during the one layout pass that matters, and <c>LayoutUpdated</c> is otherwise an empty
/// call.
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

        var state = States.GetValue(list, static l =>
        {
            var created = new State();
            l.LayoutUpdated += (_, _) => Play(l, created);
            return created;
        });

        state.Pending = true;
    }

    /// <summary>
    /// Forgets what has been animated, for a list whose contents are about to be replaced wholesale.
    /// </summary>
    /// <remarks>
    /// Needed because the two cases are indistinguishable from the item count alone: one item fewer means
    /// either "something was removed, nothing left to animate" or "the list was cleared and is being filled
    /// again, everything is new". Only the caller knows which it is.
    /// </remarks>
    public static void Reset(ItemsControl list)
    {
        if (list is null) return;
        if (States.TryGetValue(list, out var state)) state.Animated = 0;
    }

    /// <summary>
    /// Plays the entrance again for a list whose items are already there — the page is being (re)entered and
    /// the content is arriving on screen for the second, third or tenth time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because <see cref="Queue"/> alone made the entrance a once-per-process event. That is
    /// what the trigger it replaced did too — the platform's item transition only fires for items a control
    /// takes on — and it is wrong for a cached page: navigating away and back does not remove the cards, so
    /// the list is never "given" its items again, nothing animates, and the only way to ever see this effect
    /// was to restart the app. Which is not how a tray-resident app is used, and is exactly how it was
    /// being reported as missing.
    /// </para>
    /// <para>
    /// Deliberately routed through the same pending flag rather than animating here and now: this is called
    /// from a page's navigation handler, which runs before the page is back in the visual tree, and an
    /// element that is not in the tree has no visual to animate. Waiting for the layout pass that follows
    /// the navigation is both correct and free.
    /// </para>
    /// </remarks>
    public static void Replay(ItemsControl list)
    {
        Reset(list);
        Queue(list);
    }

    private static void Play(ItemsControl list, State state)
    {
        if (!state.Pending) return;

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

        var animated = false;

        for (var i = state.Animated; i < count; i++)
        {
            if (list.ContainerFromIndex(i) is not UIElement container) continue;

            Motion.PosterEnter(container, i - state.Animated);
            animated = true;
        }

        // Nothing realised yet: leave the flag set and try again on the next layout pass. That is the
        // normal case for the first pass after items are added, and the retry is what makes this work
        // without knowing when the panel will get around to generating its containers. It also means an
        // item that is never realised — one a virtualising panel keeps below the fold — is animated
        // whenever it first *is* realised, i.e. when it is scrolled into view, rather than not at all.
        if (!animated) return;

        state.Animated = count;
        state.Pending = false;
    }
}
