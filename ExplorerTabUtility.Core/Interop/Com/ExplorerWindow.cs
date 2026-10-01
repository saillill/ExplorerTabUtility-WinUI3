using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ExplorerTabUtility.Interop.Com;

/// <summary>
/// Replacement for the tlbimp-generated <c>SHDocVw.InternetExplorer</c> plus the parts of
/// <c>Shell32.ShellFolderView</c> / <c>Folder</c> / <c>Folder2</c> the app actually used.
/// <para>
/// Member access is pure IDispatch late binding via <c>dynamic</c> — no hand-declared vtables
/// and no type-library import, so the project builds with the .NET SDK alone.
/// Events are hooked through <see cref="ComEventHelper"/>.
/// </para>
/// <para>
/// Instances are interned by underlying COM identity (<see cref="ConditionalWeakTable{TKey,TValue}"/>),
/// which preserves the old RCW-based dictionary-key semantics that
/// <c>ExplorerWatcher</c> relies on.
/// </para>
/// </summary>
public sealed class ExplorerWindow : IDisposable
{
    private static readonly ConditionalWeakTable<object, ExplorerWindow> Cache = new();

    private readonly object _rcw;
    private readonly dynamic _dyn;
    private readonly ExplorerComEventSink _sink = new();

    private IDisposable? _connectionToken;
    private Action? _onQuit;
    private Action<object?, object?>? _navigateComplete2;
    private bool _disposed;

    /// <summary>Serializes event advising and teardown so a connection point is never advised twice.</summary>
    private readonly object _eventsLock = new();

    /// <summary>
    /// The late-bound view of the COM object. Every member below goes through this property rather than
    /// using <see cref="_dyn"/> directly, so a wrapper used after <see cref="Dispose"/> fails with a
    /// stated reason.
    /// </summary>
    /// <remarks>
    /// <see cref="Dispose"/> calls <c>Marshal.ReleaseComObject</c>, after which the RCW has no reference
    /// of its own left: the runtime then reports any further call as <c>InvalidComObjectException</c>,
    /// which says nothing about what actually happened. The hotkey paths catch broadly, so that turned
    /// into "the hotkey did nothing" with no trace at all. <see cref="ObjectDisposedException"/> names
    /// the real cause, and <see cref="IsDisposed"/> lets a caller check first.
    /// </remarks>
    private dynamic Com => _disposed
        ? throw new ObjectDisposedException(nameof(ExplorerWindow), "the shell window has been released")
        : _dyn;

    /// <summary>True once <see cref="Dispose"/> has released the underlying COM object.</summary>
    public bool IsDisposed => _disposed;

    private ExplorerWindow(object rcw)
    {
        _rcw = rcw;
        _dyn = rcw;

        _sink.OnQuitHandler = () => _onQuit?.Invoke();
        _sink.NavigateComplete2Handler = (pDisp, url) => _navigateComplete2?.Invoke(pDisp, url);
    }

    /// <summary>Wraps a raw COM dispatch pointer/RCW, returning the same instance for the same COM object.</summary>
    public static ExplorerWindow? Wrap(object? rcw)
    {
        if (rcw is null) return null;

        if (Cache.TryGetValue(rcw, out var existing))
        {
            if (!existing._disposed) return existing;
            Cache.Remove(rcw);
        }

        var wrapper = new ExplorerWindow(rcw);

        try
        {
            Cache.Add(rcw, wrapper);
            return wrapper;
        }
        catch (ArgumentException)
        {
            // TryGetValue → Add is not atomic, and ConditionalWeakTable.Add throws ArgumentException when
            // the key already exists. If a concurrent Wrap inserted the same COM object first, read the
            // winner back and return it — the equivalent of GetOrAdd. Letting this exception escape (it
            // surfaced as `null` from ShellWindows.Item) is exactly what silently dropped the window so
            // that new windows stopped folding into tabs (AUD-05).
            if (Cache.TryGetValue(rcw, out var winner) && !winner._disposed)
                return winner;

            // The winner was disposed in the meantime; retry once so we never return null for a live COM object.
            Cache.Remove(rcw);
            try { Cache.Add(rcw, wrapper); }
            catch (ArgumentException) { /* raced again — still return the usable wrapper */ }
            return wrapper;
        }
    }

    public object ComObject => _rcw;

    // ---- IWebBrowser2 surface -------------------------------------------------

    public int HWND => (int)Com.HWND;

    public string LocationURL => (string?)Com.LocationURL ?? string.Empty;

    public string LocationName => (string?)Com.LocationName ?? string.Empty;

    /// <summary>The <c>ShellFolderView</c> document, or <c>null</c> for non-fileystem folders.</summary>
    public object? Document
    {
        get
        {
            try { return (object?)Com.Document; }
            catch { return null; }
        }
    }

    public void Navigate2(object target) => Com.Navigate2(target);

    public void GoBack() => Com.GoBack();

    public void GoForward() => Com.GoForward();

    public void Quit() => Com.Quit();

    /// <summary>COM property bag used by the app to mark windows it has already seen.</summary>
    public object? GetProperty(string name)
    {
        try { return (object?)Com.GetProperty(name); }
        catch { return null; }
    }

    public void PutProperty(string name, object value) => Com.PutProperty(name, value);

    // ---- ShellFolderView helpers (what Shell32 previously provided) ------------

    public string[]? GetSelectedItemNames()
    {
        dynamic? document = Document;
        if (document is null) return null;

        dynamic items = document.SelectedItems();
        if (items is null) return null;

        var count = (int)items.Count;
        if (count == 0) return null;

        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            dynamic item = items.Item(i);
            result[i] = (string)item.Name;
        }

        return result;
    }

    public void SelectItemsByName(string[]? names)
    {
        if (names is null || names.Length == 0) return;

        dynamic? document = Document;
        if (document is null) return;

        dynamic folder = document.Folder;

        foreach (var name in names)
        {
            try
            {
                object? item = folder.ParseName(name);
                if (item is null) continue;

                document.SelectItem(item, 1);
            }
            catch
            {
                // Missing/renamed item — nothing to select.
            }
        }
    }

    /// <summary>
    /// <c>Folder2.Self.Path</c>. Used for shell namespaces (This PC, Recycle Bin, …) where
    /// <see cref="LocationURL"/> is empty. Late binding reaches <c>Self</c> without needing
    /// a separate <c>Folder2</c> interface declaration.
    /// </summary>
    public string? GetDocumentFolderSelfPath()
    {
        try
        {
            dynamic? document = Document;
            if (document is null) return null;

            dynamic folder = document.Folder;
            dynamic self = folder.Self;
            return (string?)self.Path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// QIs the browser for <c>IServiceProvider</c> and resolves the tab host window
    /// (<c>IShellBrowser::GetWindow</c>). Replaces the old IServiceProvider cast chain.
    /// </summary>
    /// <remarks>
    /// Everything that touches COM sits inside the guard, including the <c>is</c> test: an <c>is</c>
    /// against a COM interface is itself a QueryInterface, and on an object whose server has gone (the
    /// Explorer process died) or whose RCW has been released that throws rather than answering. A method
    /// named <c>Try…</c> must not throw, so both conditions report "no tab handle" instead.
    /// </remarks>
    public bool TryGetTabHandle(out nint handle)
    {
        handle = 0;

        if (_disposed) return false;

        try
        {
            if (_rcw is not IServiceProvider provider) return false;

            var serviceGuid = typeof(IShellBrowser).GUID;
            provider.QueryService(ref serviceGuid, ref serviceGuid, out var shellBrowser);
            if (shellBrowser is null) return false;

            try
            {
                return shellBrowser.GetWindow(out handle) == 0 && handle != 0;
            }
            finally
            {
                Marshal.ReleaseComObject(shellBrowser);
            }
        }
        catch
        {
            handle = 0;
            return false;
        }
    }

    // ---- Events --------------------------------------------------------------

    public event Action? OnQuit
    {
        add { _onQuit += value; EnsureEventsAdvised(); }
        remove { _onQuit -= value; }
    }

    public event Action<object?, object?>? NavigateComplete2
    {
        add { _navigateComplete2 += value; EnsureEventsAdvised(); }
        remove { _navigateComplete2 -= value; }
    }

    private void EnsureEventsAdvised()
    {
        // Whole read/advise/assign is atomic. A bare check-then-act let two subscriptions arriving from
        // different threads both see a null token, both call AdviseAll, and the second overwrite the
        // first — so the first connection point was never Unadvised (a COM reference leaked on every
        // repeat) (AUD-05).
        lock (_eventsLock)
        {
            if (_disposed || _connectionToken is not null) return;

            var previous = _connectionToken;
            _connectionToken = ComEventHelper.AdviseAll(_rcw, _sink);

            // Defensive: never leave a prior token unadvised.
            previous?.Dispose();
        }
    }

    // ---- Identity ------------------------------------------------------------

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(_rcw);

    public override bool Equals(object? obj) => obj is ExplorerWindow other && ReferenceEquals(_rcw, other._rcw);

    public override string ToString() => _disposed
        ? "ExplorerWindow(disposed)"
        : $"ExplorerWindow(0x{HWND:X})";

    // ---- Lifetime ------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;

        // Release the connection token under the same lock EnsureEventsAdvised uses, so advising cannot
        // be re-entered (and leak) after teardown has started (AUD-05).
        lock (_eventsLock)
        {
            if (_disposed) return;
            _disposed = true;

            _onQuit = null;
            _navigateComplete2 = null;

            _connectionToken?.Dispose();
            _connectionToken = null;
        }

        try { Marshal.ReleaseComObject(_rcw); } catch { /* already released */ }
    }
}
