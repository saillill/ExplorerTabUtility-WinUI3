using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// The app's motion vocabulary, and the rules that go with it.
/// <para>
/// <b>The durations are a closed set.</b> Only the three control durations the Fluent guidance allows are used,
/// under the names the guidance gives them: <see cref="ControlFasterDuration"/> (83ms),
/// <see cref="ControlFastDuration"/> (167ms) and <see cref="ControlNormalDuration"/> (250ms). There is no
/// free-form duration anywhere: a new animation picks one of these or it is not a control animation.
/// </para>
/// <para>
/// <b>The curves are a closed set too</b>, all built through
/// <see cref="Compositor.CreateCubicBezierEasingFunction"/> so they are the guidance's own curves rather than
/// something that looks close:
/// </para>
/// <list type="bullet">
/// <item><see cref="EaseEnter"/> — (0, 0, 0, 1) — for things entering the screen.</item>
/// <item><see cref="EaseMove"/> — (0.55, 0.55, 0, 1) — for things that move.</item>
/// <item><see cref="EaseExit"/> — (1, 0, 1, 1) — for things leaving. <b>Exit must be paired with a fade</b>: the
/// curve alone decelerates a disappearing element in a way the guidance explicitly rules out.</item>
/// <item>Fades are <b>linear</b> and use <see cref="ControlFasterDuration"/>. <see cref="FadeIn"/> and
/// <see cref="FadeOut"/> are the whole of that rule.</item>
/// </list>
/// <para>
/// <b>Existing controls come first.</b> Before reaching for anything in this file, the question is whether a
/// platform control already does it: <c>NavigationThemeTransition</c> on a <c>Frame</c>,
/// <c>EntranceThemeTransition</c>/<c>RepositionThemeTransition</c> on a panel, <c>AnimatedIcon</c>,
/// <c>ConnectedAnimation</c>, the implicit animation an <c>ItemsRepeater</c> provides. Those already follow the
/// guidance, already scale with the system's animation settings, and are not this file's business. What is left
/// for this file is what no control provides.
/// </para>
/// <para>
/// <b>Reduced motion is not optional.</b> Every helper here reads <see cref="AnimationsEnabled"/> and, when the
/// system setting is off, applies the final value immediately instead of animating. The framework's own
/// transitions do the same by themselves.
/// </para>
/// </summary>
internal static class Motion
{
    /// <summary>The guidance's <c>ControlFasterAnimationDuration</c>: 83ms.</summary>
    public static readonly TimeSpan ControlFasterDuration = TimeSpan.FromMilliseconds(83);

    /// <summary>The guidance's <c>ControlFastAnimationDuration</c>: 167ms.</summary>
    public static readonly TimeSpan ControlFastDuration = TimeSpan.FromMilliseconds(167);

    /// <summary>The guidance's <c>ControlNormalAnimationDuration</c>: 250ms.</summary>
    public static readonly TimeSpan ControlNormalDuration = TimeSpan.FromMilliseconds(250);

    private static readonly UISettings Settings = new();

    /// <summary>
    /// Whether the system allows animation at all — Windows' Settings &gt; Accessibility &gt; Visual effects &gt;
    /// Animation effects. Read per call rather than cached: it costs nothing, and the user can change it while
    /// the app is running.
    /// </summary>
    public static bool AnimationsEnabled => Settings.AnimationsEnabled;

    /// <summary>Entering the screen: (0, 0, 0, 1).</summary>
    public static CubicBezierEasingFunction EaseEnter(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.0f, 0.0f), new Vector2(0.0f, 1.0f));

    /// <summary>Moving: (0.55, 0.55, 0, 1).</summary>
    public static CubicBezierEasingFunction EaseMove(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.55f, 0.55f), new Vector2(0.0f, 1.0f));

    /// <summary>
    /// Leaving the screen: (1, 0, 1, 1). Never use this on its own — an exit is a curve <em>and</em> a fade;
    /// see <see cref="FadeOut"/>.
    /// </summary>
    public static CubicBezierEasingFunction EaseExit(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(1.0f, 0.0f), new Vector2(1.0f, 1.0f));

    /// <summary>
    /// Fades an element in from transparent, linearly, over <see cref="ControlFasterDuration"/>.
    /// </summary>
    public static void FadeIn(UIElement element)
    {
        element.Opacity = 1;

        if (!AnimationsEnabled)
        {
            // Also the cleanup path: whatever animation was running is cancelled by setting the value.
            ElementCompositionPreview.GetElementVisual(element).Opacity = 1;
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();

        // Linear by construction: a fade with an easing curve is not the guidance's fade.
        animation.InsertKeyFrame(0, 0);
        animation.InsertKeyFrame(1, 1);
        animation.Duration = ControlFasterDuration;

        visual.StartAnimation(nameof(visual.Opacity), animation);
    }

    /// <summary>
    /// Fades an element out, linearly over <see cref="ControlFasterDuration"/>. This is the fade the exit curve
    /// requires; nothing here should animate an element away without it.
    /// </summary>
    public static void FadeOut(UIElement element, Action? onCompleted = null)
    {
        if (!AnimationsEnabled)
        {
            ElementCompositionPreview.GetElementVisual(element).Opacity = 0;
            element.Opacity = 0;
            onCompleted?.Invoke();
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, 0);
        animation.Duration = ControlFasterDuration;

        if (onCompleted is not null)
        {
            // A scoped batch, not the end of the animation: the callback has to run after the frame that shows
            // the final value, or the hide lands a frame early and can be seen.
            using var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (_, _) => onCompleted();
            visual.StartAnimation(nameof(visual.Opacity), animation);
            batch.End();
            return;
        }

        visual.StartAnimation(nameof(visual.Opacity), animation);
    }
}
