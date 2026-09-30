using System;

namespace ExplorerTabUtility.Abstractions;

/// <summary>
/// Marshals work onto the UI thread and reports OS session shutdown.
/// <para>
/// Replaces the Core library's former direct use of <c>SynchronizationContext</c> and
/// <c>System.Windows.Application.Current.SessionEnding</c>. The WPF shell wraps
/// <c>Dispatcher</c>; the WinUI shell wraps <c>DispatcherQueue</c> and
/// <c>SystemEvents.SessionEnding</c>.
/// </para>
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the calling thread is already the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Queues <paramref name="action"/> to run on the UI thread. Must never block.</summary>
    void Post(Action action);

    /// <summary>
    /// Queues UI-touching work like <see cref="Post"/>, but drops it when the queue is already
    /// shutting down instead of running it inline on a background thread.
    /// <para>
    /// Use this for anything that touches XAML (window visibility, menus, theme). The inline
    /// fallback <see cref="Post"/> provides is only safe for shutdown-critical, toolkit-free work
    /// such as flushing settings — running XAML code inline on a hook/pool thread violates the
    /// toolkit's thread affinity and throws a (potentially fatal) COMException.
    /// </para>
    /// </summary>
    void TryPost(Action action) => Post(action);

    /// <summary>
    /// Raised when Windows is ending the session (logoff / shutdown).
    /// Implementations must raise this at most once per session.
    /// </summary>
    event EventHandler? SessionEnding;
}
