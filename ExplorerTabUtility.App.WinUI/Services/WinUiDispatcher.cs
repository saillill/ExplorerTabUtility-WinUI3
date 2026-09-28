using System;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using ExplorerTabUtility.Abstractions;

namespace ExplorerTabUtility.App.Services;

/// <summary>
/// <see cref="IUiDispatcher"/> over the WinUI <see cref="DispatcherQueue"/>.
/// <para>
/// Replaces the Core library's old <c>SynchronizationContext.Current</c> capture and
/// <c>Application.Current.SessionEnding</c> subscription.
/// </para>
/// </summary>
public sealed class WinUiDispatcher : IUiDispatcher, IDisposable
{
    private readonly DispatcherQueue _queue;

    public WinUiDispatcher(DispatcherQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public void Post(Action action)
    {
        if (action is null) return;

        // TryEnqueue returns false only while the queue is shutting down. Run inline then, so
        // shutdown work (flush settings, release hooks) is not silently dropped.
        if (!_queue.TryEnqueue(() => action()))
            action();
    }

    public event EventHandler? SessionEnding;

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        => SessionEnding?.Invoke(this, EventArgs.Empty);

    public void Dispose() => SystemEvents.SessionEnding -= OnSessionEnding;
}
