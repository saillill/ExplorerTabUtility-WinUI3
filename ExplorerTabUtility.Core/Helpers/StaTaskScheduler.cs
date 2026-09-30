using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerTabUtility.Helpers;

public sealed class StaTaskScheduler : TaskScheduler, IDisposable
{
    private readonly Thread _staThread;
    private readonly BlockingCollection<Task> _tasks = new();
    private bool _disposed;

    public StaTaskScheduler()
    {
        _staThread = new Thread(Run)
        {
            IsBackground = true,
            Name = "STA Thread"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    private void Run()
    {
        foreach (var task in _tasks.GetConsumingEnumerable())
        {
            try
            {
                TryExecuteTask(task);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in STA thread: {ex}");
            }
        }
    }

    // Called by the TPL to queue a task
    protected override void QueueTask(Task task)
    {
        _tasks.Add(task);
    }

    // (Optional) TPL calls this to see if we can run inline
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        // Since this scheduler only wants tasks on its dedicated thread, we *generally*
        // disallow inlining unless this is the STA thread itself.
        if (Thread.CurrentThread == _staThread)
        {
            return TryExecuteTask(task);
        }
        return false;
    }

    protected override IEnumerable<Task> GetScheduledTasks()
    {
        return _tasks.ToArray();
    }

    public void Dispose()
    {
        // Idempotent: a second call would run CompleteAdding() on an already-disposed
        // BlockingCollection and throw ObjectDisposedException (AUD-04).
        if (_disposed) return;
        _disposed = true;

        _tasks.CompleteAdding();

        // Bounded join. The STA thread can be blocked waiting on the UI thread (for instance the
        // "restore previous windows?" dialog), and Dispose runs on the UI thread — an unbounded Join
        // is the two-way wait that hangs the exit with a leftover tray icon (AUD-13). Wait briefly,
        // then let the (IsBackground) thread be reclaimed with the process.
        _staThread.Join(TimeSpan.FromSeconds(5));

        _tasks.Dispose();
    }
}