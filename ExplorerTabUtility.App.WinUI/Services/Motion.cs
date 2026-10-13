using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// The app's motion vocabulary: what may animate, how far, how fast, with which curve, and through which
/// channel. Everything animated from code goes through a behavior named here.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tokens are a closed set, and this file is the only place they exist.</b> Three durations —
/// <see cref="ControlFasterDuration"/> (83ms), <see cref="ControlFastDuration"/> (167ms) and
/// <see cref="ControlNormalDuration"/> (250ms) — and four curves, all built through
/// <see cref="Compositor.CreateCubicBezierEasingFunction"/> so they are the guidance's own curves rather
/// than something that looks close: entrance (0,0,0,1), move (0.55,0.55,0,1), exit (1,0,1,1) and linear.
/// A new effect picks from these or it is not an effect this app performs. The set is declared whole
/// even where no behavior below happens to use one of them yet: it is a vocabulary, and a vocabulary
/// that only contains what is already said is not one. Distances are tokens for the same reason —
/// scattered literals drift apart, tokens do not.
/// </para>
/// <para>
/// <b>Callers name a behavior, never a curve, a duration or a distance.</b> The four behaviors below are
/// the whole interface: <see cref="FadeIn"/>, <see cref="FadeOut"/>, <see cref="PosterEnter"/> and
/// <see cref="DropdownEnter"/>. Two screens that both need "content arriving" call the same name, so
/// their timing agrees by construction rather than by two call sites happening to be written alike.
/// </para>
/// <para>
/// <b>Displacement goes through the <c>Translation</c> property-set member, never <c>Visual.Offset</c>.</b>
/// Offset belongs to layout: an Offset animation keeps filling the value after it ends, and a later
/// layout pass re-asserts its own value over it, which is how an element ends up stranded. Translation is
/// a post-layout translate — it stacks on top of the layout result instead of replacing it, so it needs
/// no baseline coordinate and survives relayout. That last property is also why an entrance can be
/// started for an element that has not been measured yet.
/// </para>
/// <para>
/// <b>Platform controls come first.</b> Before adding anything here, the question is whether a control
/// already does it: <c>NavigationThemeTransition</c> on a <c>Frame</c>, <c>AnimatedIcon</c>,
/// <c>ConnectedAnimation</c>, or the disclosure motion <c>SettingsExpander</c> performs for itself. Those
/// already follow the guidance and already answer to the system's animation setting. What is left for
/// this file is what no control provides.
/// </para>
/// <para>
/// <b>Reduced motion is not optional.</b> Every behavior reads <see cref="AnimationsEnabled"/> and, when
/// the system setting is off, writes the final value immediately — and still invokes its completion
/// callback, because a caller that hides an element from that callback would otherwise wait forever.
/// </para>
/// <para>
/// <b>Known deviation, deliberately not repaired:</b> <c>SettingsExpander</c> animates its own expansion
/// over 333ms with the entrance spline, which is not one of the three durations above. It is a third-party
/// control's internal timing; changing it would mean taking over its template, which costs far more than
/// the one irregular number does.
/// </para>
/// </remarks>
internal static class Motion
{
    // ---- Tokens: durations ---------------------------------------------------

    /// <summary>83ms. Fades, and anything leaving the screen.</summary>
    public static readonly TimeSpan ControlFasterDuration = TimeSpan.FromMilliseconds(83);

    /// <summary>167ms. Something already on screen changing place.</summary>
    public static readonly TimeSpan ControlFastDuration = TimeSpan.FromMilliseconds(167);

    /// <summary>250ms. Content arriving: the entrance, and the staggered entrance in particular.</summary>
    public static readonly TimeSpan ControlNormalDuration = TimeSpan.FromMilliseconds(250);

    // ---- Tokens: distances ---------------------------------------------------

    /// <summary>
    /// How far a card or a grid cell rises as it enters.
    /// </summary>
    /// <remarks>
    /// This one is measured rather than chosen. The first version of this animation used a much smaller
    /// offset and was reported as "no animation": a few pixels plus a fade reads as a plain fade, and the
    /// offset is what the eye actually catches. The guidance's own figure for a page transition is a 150px
    /// slide; scaled down to a card-sized element that lands in the 40-50 range.
    /// </remarks>
    private const float ContentEnterOffset = 48f;

    // ---- Tokens: stagger -----------------------------------------------------

    /// <summary>Interval between one element and the next in a staggered entrance.</summary>
    /// <remarks>
    /// Below about 20ms the eye stops resolving the elements as separate arrivals and reads one movement
    /// again, which is the effect this exists to avoid; above about 50ms it stops being a wave and becomes
    /// a queue.
    /// </remarks>
    private const double StaggerStepMs = 20;

    /// <summary>
    /// The highest step index that still earns its own delay.
    /// </summary>
    /// <remarks>
    /// A cap is not an optimization, it is what keeps the effect readable: without one, the 40th element
    /// would not begin until 800ms after the first, by which time the user is already looking at it. That
    /// reads as a stall rather than a wave. Past the cap the remaining elements enter together with the
    /// last stepped one, so the whole thing settles within 160ms of delay plus one duration.
    /// </remarks>
    private const int StaggerCap = 8;

    /// <summary>
    /// The vertical scale a dropdown starts from, so its bottom edge travels roughly a tenth of the
    /// panel's height as it opens — the same order as the offsets above, which is what makes it visible.
    /// </summary>
    private const float DropdownScaleFrom = 0.9f;

    // ---- Composition channels ------------------------------------------------

    /// <summary>Plain composition properties: naming them by string is what <c>StartAnimation</c> takes.</summary>
    private const string OpacityChannel = "Opacity";
    private const string ScaleChannel = "Scale";

    /// <summary>
    /// The render-time translate channel, published by <see cref="ElementCompositionPreview.SetIsTranslationEnabled"/>.
    /// </summary>
    private const string TranslationChannel = "Translation";

    // ---- System animation setting -------------------------------------------

    private static UISettings? _settings;
    private static bool _settingsUnavailable;

    /// <summary>
    /// Whether the system allows animation — Windows' Settings &gt; Accessibility &gt; Visual effects &gt;
    /// Animation effects.
    /// </summary>
    /// <remarks>
    /// The <em>value</em> is read per call rather than cached, because the user can change it while the app
    /// is running and the next animation should already respect it. The <see cref="UISettings"/> object
    /// itself is kept, both because it is the thing that tracks the change and because constructing it can
    /// throw in a restricted context — once, at that point, rather than on every animation. A failure here
    /// resolves to "animations are allowed": the direction of failure has to be a missing animation, never a
    /// missing app.
    /// </remarks>
    public static bool AnimationsEnabled
    {
        get
        {
            if (_settingsUnavailable) return true;

            try
            {
                _settings ??= new UISettings();
                return _settings.AnimationsEnabled;
            }
            catch (Exception ex)
            {
                _settingsUnavailable = true;
                StartupLog.Fail("Motion: UISettings unavailable, animations left on", ex);
                return true;
            }
        }
    }

    // ---- Curves, cached per compositor --------------------------------------

    /// <summary>
    /// The four easing functions, created once per <see cref="Compositor"/>.
    /// </summary>
    /// <remarks>
    /// Building a <c>CubicBezierEasingFunction</c> per animation is pure allocation — the compositor is
    /// stable for the life of a window, so the same four objects are reused for every animation in it. A
    /// weak table rather than a dictionary because the compositor lives and dies with its window and must
    /// not be pinned by a cache that outlives it.
    /// </remarks>
    private sealed class EasingSet
    {
        public EasingSet(Compositor compositor)
        {
            Entrance = compositor.CreateCubicBezierEasingFunction(new Vector2(0.0f, 0.0f), new Vector2(0.0f, 1.0f));
            Move = compositor.CreateCubicBezierEasingFunction(new Vector2(0.55f, 0.55f), new Vector2(0.0f, 1.0f));
            Exit = compositor.CreateCubicBezierEasingFunction(new Vector2(1.0f, 0.0f), new Vector2(1.0f, 1.0f));
            Linear = compositor.CreateCubicBezierEasingFunction(new Vector2(0.0f, 0.0f), new Vector2(1.0f, 1.0f));
        }

        public CompositionEasingFunction Entrance { get; }
        public CompositionEasingFunction Move { get; }
        public CompositionEasingFunction Exit { get; }
        public CompositionEasingFunction Linear { get; }
    }

    private static readonly ConditionalWeakTable<Compositor, EasingSet> EasingSets = new();

    private static EasingSet Easing(Compositor compositor) =>
        EasingSets.GetValue(compositor, static c => new EasingSet(c));

    // ---- Behaviors -----------------------------------------------------------

    /// <summary>
    /// Fades an element in from transparent over <see cref="ControlFasterDuration"/>.
    /// </summary>
    /// <remarks>
    /// Linear: an easing curve applied to opacity alone has nothing to ease — there is no distance to
    /// accelerate over. The base value is set to opaque <em>before</em> the animation is installed, so an
    /// animation that fails to start leaves an element that is visible rather than one that never appears.
    /// </remarks>
    public static void FadeIn(UIElement element, Action? completed = null)
    {
        Visual visual;
        try
        {
            visual = VisualOf(element);
        }
        catch (Exception ex)
        {
            StartupLog.Fail("Motion.FadeIn: no visual", ex);
            completed?.Invoke();
            return;
        }

        element.Opacity = 1;

        if (!AnimationsEnabled)
        {
            Settled(visual, opacity: 1f);
            completed?.Invoke();
            return;
        }

        try
        {
            var compositor = visual.Compositor;
            visual.StopAnimation(OpacityChannel);
            visual.Opacity = 1f;

            Batch(compositor, () =>
            {
                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = ControlFasterDuration;
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(1f, 1f);
                visual.StartAnimation(OpacityChannel, fade);
            }, completed);
        }
        catch (Exception ex)
        {
            StartupLog.Fail("Motion.FadeIn", ex);
            Settled(visual, opacity: 1f);
            completed?.Invoke();
        }
    }

    /// <summary>
    /// Fades an element out over <see cref="ControlFasterDuration"/>, reporting completion so the caller can
    /// hide it afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason this reports completion with a scoped batch rather than at the end of the animation: the
    /// caller's next step is almost always to change <c>Visibility</c>, and doing that at the start rather
    /// than after the last frame shows the element disappearing mid-fade.
    /// </para>
    /// <para>
    /// The start value is deliberately left implicit — the keyframe is only written at the end. An exit that
    /// was interrupted by a re-entry then fades from wherever it actually is instead of jumping to opaque
    /// first. The cost is that a completed fade-out leaves the composite value at 0, so an element that is
    /// shown again later must be faded in rather than simply un-hidden.
    /// </para>
    /// <para>
    /// No call site today: the window's own hide — the only thing in this app that leaves the screen — is
    /// documented as strictly synchronous (see <c>MainWindow.HideToTray</c>), so it cannot wait 83ms for a
    /// fade. This stays as the exit half of the vocabulary, and is what an in-window panel would use.
    /// </para>
    /// </remarks>
    public static void FadeOut(UIElement element, Action? completed = null)
    {
        Visual visual;
        try
        {
            visual = VisualOf(element);
        }
        catch (Exception ex)
        {
            StartupLog.Fail("Motion.FadeOut: no visual", ex);
            completed?.Invoke();
            return;
        }

        if (!AnimationsEnabled)
        {
            Settled(visual, opacity: 0f);
            element.Opacity = 0;
            completed?.Invoke();
            return;
        }

        try
        {
            var compositor = visual.Compositor;
            visual.StopAnimation(OpacityChannel);

            Batch(compositor, () =>
            {
                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = ControlFasterDuration;
                fade.InsertKeyFrame(1f, 0f);
                visual.StartAnimation(OpacityChannel, fade);
            }, completed);
        }
        catch (Exception ex)
        {
            StartupLog.Fail("Motion.FadeOut", ex);
            completed?.Invoke();
        }
    }

    /// <summary>
    /// The entrance for content that arrives as a block — a card, a row, a grid cell — staggered by the
    /// element's position in that block. This is the behavior to call; there is nothing to configure.
    /// </summary>
    /// <param name="index">
    /// The element's position in its block. The pacing is derived from it here rather than passed in, which
    /// is the difference between "both screens use the same stagger" and "both screens computed a delay".
    /// </param>
    /// <remarks>
    /// Past <see cref="StaggerCap"/> the delay stops growing, so the tail of a long list arrives together
    /// instead of keeping the wave running.
    /// </remarks>
    public static void PosterEnter(UIElement element, int index, Action? completed = null)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Min(index, StaggerCap) * StaggerStepMs);
        SlideCore(element, 0f, ContentEnterOffset, ControlNormalDuration, delay, completed);
    }

    /// <summary>
    /// The entrance for a dropdown, on an element whose top edge should stay put while it opens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written to be driven from <c>Popup.Opened</c>, because the reveal the framework gives a
    /// <c>ComboBox</c> popup is a <c>PopupThemeTransition</c> with no duration, no curve and no
    /// configuration: the only way to give a dropdown this app's timing is to take the reveal over. What is
    /// <em>not</em> taken over is the control template — the transition is cleared and the animation started
    /// from the popup's own event, which is a fraction of the cost of owning a template that has to be
    /// re-checked against every framework update.
    /// </para>
    /// <para>
    /// The centre point is the top edge's midpoint so the panel grows downwards out of the control it
    /// belongs to. With the default origin — the top-left corner — the panel appears to fly in from the
    /// corner instead. A panel that has not been measured has no meaningful centre, and scaling it about the
    /// origin is worse than not scaling it at all, so that case just shows the content.
    /// </para>
    /// </remarks>
    public static void DropdownEnter(UIElement element, Action? completed = null)
    {
        Visual visual;
        try
        {
            visual = VisualOf(element);
        }
        catch (Exception ex)
        {
            StartupLog.Fail("Motion.DropdownEnter: no visual", ex);
            completed?.Invoke();
            return;
        }

        if (!AnimationsEnabled)
        {
            Settled(visual, opacity: 1f);
            visual.StopAnimation(ScaleChannel);
            visual.Scale = Vector3.One;
            completed?.Invoke();
            return;
        }

        try
        {
            var compositor = visual.Compositor;
            var size = visual.Size;

            // A panel that has not been measured has no meaningful centre, and scaling it about the default
            // origin is worse than not scaling it: it flies out of its own corner. The fade needs no size,
            // so that is what is left in that case.
            var scalable = size.X > 0f && size.Y > 0f;
            var from = new Vector3(1f, DropdownScaleFrom, 1f);

            visual.StopAnimation(ScaleChannel);
            visual.StopAnimation(OpacityChannel);

            if (scalable)
            {
                // The top edge's midpoint, so the panel grows downwards out of the control it belongs to.
                visual.CenterPoint = new Vector3(size.X / 2f, 0f, 0f);
                visual.Scale = from;
            }
            else
            {
                visual.Scale = Vector3.One;
                StartupLog.Step("Motion: dropdown had no measured size, fading it in unscaled");
            }

            // Parked rather than left to the keyframes: the frame this is installed on must not show the
            // panel already open.
            visual.Opacity = 0f;

            Batch(compositor, () =>
            {
                // The same curve for both channels, as everywhere else here. On the unscaled path it is
                // applied to opacity alone, where it has nothing to ease — which is harmless, unlike
                // splitting the two channels across two curves.
                var ease = Easing(compositor).Entrance;

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = ControlNormalDuration;
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(1f, 1f, ease);
                visual.StartAnimation(OpacityChannel, fade);

                if (!scalable) return;

                var scale = compositor.CreateVector3KeyFrameAnimation();
                scale.Duration = ControlNormalDuration;
                scale.InsertKeyFrame(0f, from);
                scale.InsertKeyFrame(1f, Vector3.One, ease);
                visual.StartAnimation(ScaleChannel, scale);
            }, completed);
        }
        catch (Exception ex)
        {
            StartupLog.Fail("Motion.DropdownEnter", ex);
            Settled(visual, opacity: 1f);
            visual.Scale = Vector3.One;
            completed?.Invoke();
        }
    }

    // ---- Implementation ------------------------------------------------------

    /// <summary>
    /// The one implementation behind every entrance: from "invisible, offset" to "opaque, in place".
    /// </summary>
    /// <remarks>
    /// Both axes travel inside a single animation and both properties start inside a single batch. The
    /// alternative — separate timelines — lets the fade and the movement land on different compositor ticks,
    /// which reads as the element lighting up first and moving afterwards.
    /// </remarks>
    private static void SlideCore(UIElement element, float fromX, float fromY, TimeSpan duration, TimeSpan delay, Action? completed)
    {
        Visual visual;
        try
        {
            visual = VisualOf(element);
        }
        catch (Exception ex)
        {
            StartupLog.Fail("Motion.SlideCore: no visual", ex);
            completed?.Invoke();
            return;
        }

        if (!AnimationsEnabled)
        {
            Settled(visual, opacity: 1f);
            Park(visual, 0f, 0f);
            completed?.Invoke();
            return;
        }

        try
        {
            // From a clean state: an element being entered a second time must not start from wherever the
            // previous animation left it.
            visual.StopAnimation(OpacityChannel);
            visual.StopAnimation(TranslationChannel);
            Park(visual, fromX, fromY);

            // ★ The delay is not a licence to be visible. StopAnimation restores the framework's own value
            // — opaque — and an animation carrying a DelayTime does not write its property until the delay
            // has elapsed. Without this line every element after the first is drawn solid at its offset and
            // then snaps to transparent to begin fading, which is exactly the flash the stagger is meant to
            // remove. This is the single most important line in the behavior.
            visual.Opacity = 0f;

            Batch(visual.Compositor, () =>
            {
                var ease = Easing(visual.Compositor).Entrance;

                var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = duration;
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(1f, 1f, ease);
                if (delay > TimeSpan.Zero) fade.DelayTime = delay;
                visual.StartAnimation(OpacityChannel, fade);

                var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
                slide.Duration = duration;
                slide.InsertKeyFrame(0f, new Vector3(fromX, fromY, 0f));
                slide.InsertKeyFrame(1f, Vector3.Zero, ease);
                if (delay > TimeSpan.Zero) slide.DelayTime = delay;
                visual.StartAnimation(TranslationChannel, slide);
            }, completed);
        }
        catch (Exception ex)
        {
            // Transparent-at-an-offset is the one failure the user cannot work around by looking at the
            // screen, so it is undone explicitly before giving up.
            StartupLog.Fail("Motion.SlideCore", ex);

            Settled(visual, opacity: 1f);
            Park(visual, 0f, 0f);
            completed?.Invoke();
        }
    }

    /// <summary>
    /// Groups the animations started inside <paramref name="start"/> into one batch and reports
    /// <paramref name="completed"/> once the whole batch has finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes a completion callback land after the last frame that shows the final value rather
    /// than at the end of the first animation to finish — the difference between a hide that follows the
    /// fade and a hide that cuts it short.
    /// </para>
    /// <para>
    /// The handler is attached before <c>End()</c>: a batch whose animations are all instantaneous completes
    /// the moment <c>End()</c> runs, and a handler attached after that would never be called. And
    /// <c>End()</c> runs in a <c>finally</c> — a batch left open behind an exception stays open.
    /// </para>
    /// </remarks>
    private static void Batch(Compositor compositor, Action start, Action? completed)
    {
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        if (completed is not null)
            batch.Completed += (_, _) => completed();

        try
        {
            start();
        }
        finally
        {
            batch.End();
        }
    }

    /// <summary>
    /// The element's visual, with the translate channel published.
    /// </summary>
    /// <remarks>
    /// Two steps that both have to happen before any slide animation, and neither of which is done for you:
    /// <c>SetIsTranslationEnabled</c> makes the framework publish the post-layout translate channel, and the
    /// property-set member has to exist before an animation can target it. Inserting it twice is harmless,
    /// so it is only inserted when it is absent. Note that <c>TryGetVector3</c> returns a
    /// <c>CompositionGetValueStatus</c> and not a <c>bool</c> — it does not test as one.
    /// </remarks>
    private static Visual VisualOf(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        if (visual.Properties.TryGetVector3(TranslationChannel, out _) != CompositionGetValueStatus.Succeeded)
            visual.Properties.InsertVector3(TranslationChannel, Vector3.Zero);

        return visual;
    }

    private static void Park(Visual visual, float x, float y) =>
        visual.Properties.InsertVector3(TranslationChannel, new Vector3(x, y, 0f));

    /// <summary>
    /// The reduced-motion and failure path: stop everything and leave the element in its final state.
    /// </summary>
    private static void Settled(Visual visual, float opacity)
    {
        try
        {
            visual.StopAnimation(OpacityChannel);
            visual.Opacity = opacity;
        }
        catch (Exception ex)
        {
            StartupLog.Fail("Motion: could not settle the element", ex);
        }
    }
}
