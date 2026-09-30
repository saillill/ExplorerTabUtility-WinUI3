using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExplorerTabUtility.Interop.Com;

/// <summary>
/// Replacement for the tlbimp-generated <c>SHDocVw.ShellWindows</c> coclass wrapper.
/// <para>
/// Instantiates the shell's <c>ShellWindows</c> object by CLSID and exposes only the members
/// this app needs, all through IDispatch late binding. <c>WindowRegistered</c> — the primary
/// trigger for forcing new Explorer windows into tabs — is delivered through a COM
/// connection point rather than an imported event interface.
/// </para>
/// </summary>
public sealed class ShellWindows : IDisposable
{
    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    private readonly object _rcw;
    private readonly dynamic _dyn;
    private readonly ExplorerComEventSink _sink = new();

    private IDisposable? _connectionToken;
    private bool _disposed;

    public ShellWindows()
    {
        var type = Type.GetTypeFromCLSID(ShellWindowsClsid, throwOnError: true)!;
        _rcw = Activator.CreateInstance(type)!;
        _dyn = _rcw;

        _sink.WindowRegisteredHandler = cookie => WindowRegistered?.Invoke(cookie);
        _sink.WindowRevokedHandler = cookie => WindowRevoked?.Invoke(cookie);

        _connectionToken = ComEventHelper.AdviseAll(_rcw, _sink);
    }

    /// <summary>Raised when Explorer registers a new shell window.</summary>
    public event Action<int>? WindowRegistered;

    /// <summary>Raised when Explorer revokes a shell window.</summary>
    public event Action<int>? WindowRevoked;

    public int Count => (int)_dyn.Count;

    /// <summary>Returns the window at <paramref name="index"/>, or <c>null</c> if the slot is not an Explorer window.</summary>
    public ExplorerWindow? Item(int index)
    {
        try
        {
            return ExplorerWindow.Wrap((object?)_dyn.Item(index));
        }
        catch (Exception ex)
        {
            // At minimum log it. This catch used to swallow everything silently, which is why the Wrap
            // race degrading into "new windows no longer fold into tabs" left no trace at all (AUD-05).
            Debug.WriteLine($"ShellWindows.Item({index}) failed: {ex}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _connectionToken?.Dispose();
        _connectionToken = null;

        try { Marshal.ReleaseComObject(_rcw); } catch { /* already released */ }
    }
}
