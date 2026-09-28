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
        Cache.Add(rcw, wrapper);
        return wrapper;
    }

    public object ComObject => _rcw;

    // ---- IWebBrowser2 surface -------------------------------------------------

    public int HWND => (int)_dyn.HWND;

    public string LocationURL => (string?)_dyn.LocationURL ?? string.Empty;

    public string LocationName => (string?)_dyn.LocationName ?? string.Empty;

    /// <summary>The <c>ShellFolderView</c> document, or <c>null</c> for non-fileystem folders.</summary>
    public object? Document
    {
        get
        {
            try { return (object?)_dyn.Document; }
            catch { return null; }
        }
    }

    public void Navigate2(object target) => _dyn.Navigate2(target);

    public void GoBack() => _dyn.GoBack();

    public void GoForward() => _dyn.GoForward();

    public void Quit() => _dyn.Quit();

    /// <summary>COM property bag used by the app to mark windows it has already seen.</summary>
    public object? GetProperty(string name)
    {
        try { return (object?)_dyn.GetProperty(name); }
        catch { return null; }
    }

    public void PutProperty(string name, object value) => _dyn.PutProperty(name, value);

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
    public bool TryGetTabHandle(out nint handle)
    {
        handle = 0;

        if (_rcw is not IServiceProvider provider) return false;

        try
        {
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
        if (_disposed || _connectionToken is not null) return;
        _connectionToken = ComEventHelper.AdviseAll(_rcw, _sink);
    }

    // ---- Identity ------------------------------------------------------------

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(_rcw);

    public override bool Equals(object? obj) => obj is ExplorerWindow other && ReferenceEquals(_rcw, other._rcw);

    public override string ToString() => $"ExplorerWindow(0x{HWND:X})";

    // ---- Lifetime ------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _onQuit = null;
        _navigateComplete2 = null;

        _connectionToken?.Dispose();
        _connectionToken = null;

        try { Marshal.ReleaseComObject(_rcw); } catch { /* already released */ }
    }
}
