using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ExplorerTabUtility.Interop.Com;

// ---------------------------------------------------------------------------
// COM connection-point plumbing.
//
// We deliberately do NOT hand-declare the ShellWindows / IWebBrowser2 vtables:
// getting a single vtable slot or IID wrong produces runtime-only failures.
// Instead every member call goes through `dynamic` (pure IDispatch late binding,
// no metadata required), and this file only carries the three well-known
// IUnknown-derived interfaces needed to hook up COM events.
//
// The event source interfaces themselves (DIID_DShellWindowsEvents /
// DIID_DWebBrowserEvents2) are found by enumerating the object's connection
// points rather than by looking them up with a hard-coded IID.
// ---------------------------------------------------------------------------

[ComImport]
[Guid("B196B284-BAB4-101A-B69C-00AA00341D07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConnectionPointContainer
{
    [PreserveSig] int EnumConnectionPoints(out IEnumConnectionPoints? ppEnum);
    [PreserveSig] int FindConnectionPoint(ref Guid riid, out IConnectionPoint? ppCP);
}

[ComImport]
[Guid("B196B285-BAB4-101A-B69C-00AA00341D07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumConnectionPoints
{
    [PreserveSig] int Next(uint cConnections, out IConnectionPoint? rgpcn, out uint pcFetched);
    [PreserveSig] int Skip(uint cConnections);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumConnectionPoints? ppEnum);
}

[ComImport]
[Guid("B196B286-BAB4-101A-B69C-00AA00341D07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConnectionPoint
{
    [PreserveSig] int GetConnectionInterface(out Guid pIID);
    [PreserveSig] int GetConnectionPointContainer(out IConnectionPointContainer? ppCPC);
    [PreserveSig] int Advise([MarshalAs(UnmanagedType.Interface)] object pUnkSink, out uint pdwCookie);
    [PreserveSig] int Unadvise(uint dwCookie);
    [PreserveSig] int EnumConnections(out nint ppEnum);
}

/// <summary>
/// Managed COM event sink shared by <c>ShellWindows</c> and <c>InternetExplorer</c>.
/// <para>
/// DISPIDs come from the two Shell dispinterfaces and do not collide, so one sink
/// type can serve both sources:
/// <c>DShellWindowsEvents</c> — WindowRegistered=200, WindowRevoked=201;
/// <c>DWebBrowserEvents2</c> — NavigateComplete2=252, OnQuit=253.
/// </para>
/// <para>
/// Events the sink does not declare make the CLR return DISP_E_MEMBERNOTFOUND from
/// IDispatch::Invoke, which event sources ignore — the same contract tlbimp-generated
/// sinks have always relied on.
/// </para>
/// </summary>
[ComVisible(true)]
[Guid("7C4A1E5D-9B2F-4C6A-8E31-5D0A7B46C918")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IExplorerComEventSink
{
    [DispId(200)] void WindowRegistered(int lCookie);
    [DispId(201)] void WindowRevoked(int lCookie);
    [DispId(252)] void NavigateComplete2([MarshalAs(UnmanagedType.IDispatch)] object? pDisp,
        [MarshalAs(UnmanagedType.Struct)] object? url);
    [DispId(253)] void OnQuit();
}

/// <summary>
/// Default implementation. Consumers just assign the delegates they care about;
/// the rest cost nothing.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ExplorerComEventSink : IExplorerComEventSink
{
    public Action<int>? WindowRegisteredHandler { get; set; }
    public Action<int>? WindowRevokedHandler { get; set; }
    public Action<object?, object?>? NavigateComplete2Handler { get; set; }
    public Action? OnQuitHandler { get; set; }

    public void WindowRegistered(int lCookie) => WindowRegisteredHandler?.Invoke(lCookie);
    public void WindowRevoked(int lCookie) => WindowRevokedHandler?.Invoke(lCookie);
    public void NavigateComplete2(object? pDisp, object? url) => NavigateComplete2Handler?.Invoke(pDisp, url);
    public void OnQuit() => OnQuitHandler?.Invoke();
}

/// <summary>
/// Advises a sink against every connection point exposed by a COM object and
/// returns a token that unadvises them all.
/// </summary>
internal static class ComEventHelper
{
    public static IDisposable? AdviseAll(object comObject, ExplorerComEventSink sink)
    {
        if (comObject is not IConnectionPointContainer container)
            return null;

        if (container.EnumConnectionPoints(out var enumPoints) != 0 || enumPoints is null)
            return null;

        var advised = new List<IConnectionPoint>();
        var cookies = new List<uint>();

        try
        {
            while (enumPoints.Next(1, out var point, out var fetched) == 0 && fetched == 1 && point is not null)
            {
                if (point.Advise(sink, out var cookie) == 0)
                {
                    advised.Add(point);
                    cookies.Add(cookie);
                }
                else
                {
                    Marshal.ReleaseComObject(point);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumPoints);
        }

        if (advised.Count == 0)
            return null;

        return new ConnectionToken(advised, cookies);
    }

    private sealed class ConnectionToken(List<IConnectionPoint> points, List<uint> cookies) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (var i = 0; i < points.Count; i++)
            {
                try { points[i].Unadvise(cookies[i]); } catch { /* object already gone */ }
                try { Marshal.ReleaseComObject(points[i]); } catch { /* ignore */ }
            }

            points.Clear();
            cookies.Clear();
        }
    }
}
