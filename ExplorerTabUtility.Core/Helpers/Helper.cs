using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ExplorerTabUtility.Interop;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.WinAPI;
using H.Hooks;

namespace ExplorerTabUtility.Helpers;

public static class Helper
{
    // Cached Ctrl+Shift probe. Written from hook / pool threads and read from several others, so both
    // fields are volatile: without a barrier the JIT may hoist the read out of a loop and keep acting
    // on a stale verdict. A lock would be overkill here — this is only a 1-second cache, so a stale
    // read is self-correcting (AUD-23).
    private static volatile int _lastCtrlShiftCheckAt;
    private static volatile bool _lastCtrlShiftCheckValue;
    public static readonly ConcurrentDictionary<nint, RECT?> HiddenWindows = new();

    public static T DoUntilNotDefault<T>(Func<T> action, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        return DoUntilCondition(
            action,
            result => !EqualityComparer<T?>.Default.Equals(result, default),
            timeMs,
            sleepMs,
            cancellationToken);
    }
    public static T DoUntilCondition<T>(Func<T> action, Predicate<T> predicate, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        var startTicks = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested && !IsTimeUp(startTicks, timeMs))
        {
            var result = action();
            if (predicate(result))
                return result;

            Thread.Sleep(sleepMs);
        }

        return action();
    }
    public static Task<T> DoUntilNotDefaultAsync<T>(Func<T> action, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        return DoUntilConditionAsync(
            action,
            result => !EqualityComparer<T?>.Default.Equals(result, default),
            timeMs,
            sleepMs,
            cancellationToken);
    }
    public static async Task<T> DoUntilConditionAsync<T>(Func<T> action, Predicate<T> predicate, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        var startTicks = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested && !IsTimeUp(startTicks, timeMs))
        {
            var result = action();
            if (predicate(result))
                return result;

            await Task.Delay(sleepMs);
        }

        return action();
    }

    public static bool IsTimeUp(long startTicks, int timeMs)
    {
        // Every target framework this solution builds for satisfies NET7_0_OR_GREATER, so the former
        // #if/#else fallback to a hand-rolled GetElapsedTime helper was unreachable dead code (S0-5).
        var elapsedTime = Stopwatch.GetElapsedTime(startTicks);
        return elapsedTime.TotalMilliseconds >= timeMs;
    }
    public static string HotKeysToString(this IEnumerable<Key> keys, bool isDoubleClick = false)
    {
        var text = string.Join(" + ", keys.Select(k => k.ToDisplayString()));
        if (isDoubleClick) text += "_DBL";
        return text;
    }
    public static string ToDisplayString(this Key key)
    {
        return key switch
        {
            Key.Add => "+",
            Key.Subtract => "-",
            Key.Multiply => "*",
            Key.Divide => "/",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.Decimal or Key.OemPeriod => "DOT",
            Key.Oem1 => ";",
            Key.Oem2 => "/",
            Key.Oem3 => "Tilde",
            Key.Oem4 => "[",
            Key.Oem5 => "\\",
            Key.Oem6 => "]",
            Key.Oem7 => "Quote",
            Key.Escape => "ESC",
            Key.CapsLock => "CAPS",
            Key.PageUp => "PgUp",
            Key.PageDown => "PgDn",
            Key.PrintScreen => "PrtSc",

            >= Key.NumPad0 and <= Key.NumPad9 => key.ToString().Replace("NumPad", "Num"),
            >= Key.D0 and <= Key.D9 => key.ToString().Replace("D", ""),

            // Mouse buttons
            Key.MouseLeft or Key.LButton => "LMB",
            Key.MouseRight or Key.RButton => "RMB",
            Key.MouseMiddle or Key.MButton => "MMB",
            Key.MouseXButton1 => "X1",
            Key.MouseXButton2 => "X2",

            // Default case
            _ => key.ToFixedString().Replace("Button", "")
                .Replace("Mouse", "")
                .Replace("Key", "")
                .Trim()
        };
    }

    public static bool IsExplorerEmptySpace(PixelPoint point)
    {
        var hr = WinApi.AccessibleObjectFromPoint(point, out var accObj, out var childId);

        // AccessibleObjectFromPoint hands back a COM object. Without an explicit release the runtime
        // holds that reference until the RCW is finalized; this runs on the mouse-navigation hot path,
        // so the stable fix is to release it deterministically here (AUD-11).
        try
        {
            if (hr != 0 || childId is not 0 || accObj is null) return false;

            var role = accObj.get_accRole(0);
            return role is 0x21; //IAccessible.Role:list (ROLE_SYSTEM_LIST 0x21)
        }
        finally
        {
            if (accObj is not null) Marshal.ReleaseComObject(accObj);
        }
    }
    public static bool IsFileExplorerWindow(nint window)
    {
        return window != 0 && WinApi.IsWindowHasClassName(window, "CabinetWClass");
    }
    public static bool IsFileExplorerForeground(out nint foregroundWindow)
    {
        foregroundWindow = WinApi.GetForegroundWindow();
        return IsFileExplorerWindow(foregroundWindow);
    }
    public static Task<nint> ListenForNewExplorerWindowAsync(IReadOnlyCollection<nint> currentWindows, int searchTimeMs = 1000)
    {
        return DoUntilNotDefaultAsync(() =>
                GetAllExplorerWindows()
                    .Except(currentWindows)
                    .FirstOrDefault(),
            searchTimeMs);
    }

    public static nint ListenForNewExplorerTab(IReadOnlyCollection<nint> currentTabs, int searchTimeMs = 1000)
    {
        return DoUntilNotDefault(() =>
                GetAllExplorerTabs()
                    .Except(currentTabs)
                    .FirstOrDefault(),
            searchTimeMs);
    }
    public static Task<nint> ListenForNewExplorerTabAsync(IReadOnlyCollection<nint> currentTabs, int searchTimeMs = 1000)
    {
        return DoUntilNotDefaultAsync(() =>
                GetAllExplorerTabs()
                    .Except(currentTabs)
                    .FirstOrDefault(),
            searchTimeMs);
    }
    public static Task<nint> ListenForNewExplorerTabAsync(nint window, IReadOnlyCollection<nint> currentTabs, int searchTimeMs = 1000)
    {
        return DoUntilNotDefaultAsync(() =>
                GetAllExplorerTabs(window)
                    .Except(currentTabs)
                    .FirstOrDefault(),
            searchTimeMs);
    }
    public static List<nint> GetAllExplorerTabs()
    {
        var tabs = new List<nint>();

        foreach (var window in GetAllExplorerWindows())
            tabs.AddRange(GetAllExplorerTabs(window));

        return tabs;
    }
    public static IEnumerable<nint> GetAllExplorerTabs(nint window)
    {
        return WinApi.FindAllWindowsEx("ShellTabWindowClass", window);
    }
    public static IEnumerable<nint> GetAllExplorerWindows()
    {
        return WinApi.FindAllWindowsEx("CabinetWClass");
    }
    /// <summary>
    /// Finds the main (taskbar-owning) explorer.exe process. The caller owns the returned
    /// <see cref="Process"/> and must dispose it — reading <c>StartTime</c> opens a process handle.
    /// </summary>
    public static Process? GetMainExplorerProcess()
    {
        Process? best = null;
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var expectedPath = System.IO.Path.Combine(windowsFolder, "explorer.exe");
        var bestStart = DateTime.MaxValue;

        foreach (var hWnd in WinApi.FindAllWindowsEx("Shell_TrayWnd")) // Taskbar
        {
            if (WinApi.GetWindowThreadProcessId(hWnd, out var pid) <= 0) continue;

            var processPath = WinApi.GetProcessPath((int)pid);
            if (!string.Equals(processPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            Process? proc = null;
            try
            {
                // Pick the earliest start
                proc = Process.GetProcessById((int)pid);
                if (proc.StartTime < bestStart)
                {
                    bestStart = proc.StartTime;
                    best?.Dispose(); // the previous candidate lost — release its handle
                    best = proc;
                    proc = null;     // ownership moved to `best`
                }
            }
            catch { /* The Process might have terminated */ }
            finally
            {
                proc?.Dispose();
            }
        }
        return best;
    }
    
    public static void UpdateWindowLayered(nint hWnd, bool remove)
    {
        var exStyle = WinApi.GetWindowLong(hWnd, WinApi.GWL_EXSTYLE);
        var isLayered = (exStyle & WinApi.WS_EX_LAYERED) != 0;
        
        if (remove && isLayered) // Remove
            WinApi.SetWindowLong(hWnd, WinApi.GWL_EXSTYLE, exStyle & ~WinApi.WS_EX_LAYERED);
        
        if (!remove && !isLayered) // Add
            WinApi.SetWindowLong(hWnd, WinApi.GWL_EXSTYLE, exStyle | WinApi.WS_EX_LAYERED);
    }
    public static void HideWindow(nint hWnd, bool keepTheme = false)
    {
        // Deliberately NOT ConcurrentDictionary.GetOrAdd: the value factory here performs real side
        // effects (moving the window off-screen, or flipping its layered/alpha attributes), and
        // GetOrAdd may run that factory more than once when several threads miss the key at the same
        // moment — i.e. the window got moved / made transparent twice. Check first, do the work, then
        // TryAdd; a lost race simply keeps whatever the winner already recorded (AUD-19).
        if (HiddenWindows.ContainsKey(hWnd)) return;

        RECT? originalPos = null;

        if (keepTheme)
        {
            WinApi.GetWindowRect(hWnd, out var rect);
            originalPos = rect;

            // Move it off-screen
            const uint flags = WinApi.SWP_HIDEWINDOW | WinApi.SWP_NOSIZE | WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE | WinApi.SWP_FRAMECHANGED;
            WinApi.SetWindowPos(hWnd, 0, -32_000, -32_000, 0, 0, flags);
        }
        else
        {
            // Set the transparency (alpha value) of the window (0 = transparent, 255 = opaque)
            UpdateWindowLayered(hWnd, remove: false);
            WinApi.SetLayeredWindowAttributes(hWnd, 0, 0, WinApi.LWA_ALPHA);
        }

        HiddenWindows.TryAdd(hWnd, originalPos);
    }
    public static bool ShowWindow(nint hWnd, bool removeCache)
    {
        if (!HiddenWindows.TryGetValue(hWnd, out var originalPos))
            return false;

        if (removeCache)
            HiddenWindows.TryRemove(hWnd, out _);

        if (originalPos != null) // keep theme
        {
            const uint flags = WinApi.SWP_SHOWWINDOW | WinApi.SWP_NOSIZE | WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE | WinApi.SWP_FRAMECHANGED;
            WinApi.SetWindowPos(hWnd, 0, originalPos.Value.Left, originalPos.Value.Top, 0, 0, flags);
            return true;
        }

        WinApi.SetLayeredWindowAttributes(hWnd, 0, 255, WinApi.LWA_ALPHA);
        return true;
    }

    public static bool IsCtrlShiftDown()
    {
        if (_lastCtrlShiftCheckValue && Environment.TickCount - _lastCtrlShiftCheckAt < 1_000)
            return true;
        
        _lastCtrlShiftCheckValue =
            (KeyboardSimulator.IsKeyPressed((int)VirtualKey.LeftControl) || KeyboardSimulator.IsKeyPressed((int)VirtualKey.RightControl)) &&
               (KeyboardSimulator.IsKeyPressed((int)VirtualKey.LeftShift) || KeyboardSimulator.IsKeyPressed((int)VirtualKey.RightShift));
        
        _lastCtrlShiftCheckAt = Environment.TickCount;
        return _lastCtrlShiftCheckValue;
    }
    public static void BypassWinForegroundRestrictions()
    {
        // Simulate a key press to bypass the Foreground restriction
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow#remarks
        KeyboardSimulator.SendKeyPress(VirtualKey.F23);
    }

    /// <summary>
    /// Normalizes a location string for comparison, persistence and navigation.
    /// <para>
    /// Note: the <c>file:///C:/…</c> URL form that <c>GetLocation</c> reads back from a window is
    /// intentionally <b>not</b> rewritten here. It is fed to the Shell's <c>ParseDisplayName</c> via
    /// <c>Navigate2</c>, which tolerates the resulting <c>file:\\\C:\…</c> shape; changing that shape
    /// must be verified against a real Explorer before it is touched (AUD-17). This is also why the
    /// <c>file:</c> scheme is deliberately excluded from the URL pass-through below.
    /// </para>
    /// </summary>
    public static string NormalizeLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return location;

        // Web-style URLs are returned verbatim: the file-path normalisation below (back-slash
        // rewriting in particular) would turn "https://host/path" into "https:\\host\path" and break
        // the very URL that Open()'s StartsWith("http") test then hands to ShellExecute (AUD-07).
        if (IsNonFileUrl(location))
            return location.Trim();

        if (location.IndexOf('%') > -1)
            location = Environment.ExpandEnvironmentVariables(location);

        if (location.StartsWith("::", StringComparison.Ordinal))
            location = $"shell:{location}";

        else if (location.StartsWith("{", StringComparison.Ordinal))
            location = $"shell:::{location}";

        // Strip surrounding whitespace/quotes on both ends — these never carry path meaning. But
        // strip the separators from the END only: a leading "\\" is the UNC marker and "\\?\" the
        // extended-length prefix, so trimming the front destroys them (AUD-03).
        location = location.Trim(' ', '\t', '\r', '\n', '\'', '"');
        location = location.TrimEnd('/', '\\');

        // A bare "C:" means "current directory on drive C", not its root; restore the trailing
        // separator so the meaning does not depend on the process working directory.
        if (location.Length == 2 && char.IsLetter(location[0]) && location[1] == ':')
            location += '\\';

        return location.Replace('/', '\\');
    }

    /// <summary>
    /// True for <c>scheme://…</c> URLs whose scheme is a web/network one — everything except
    /// <c>file:</c>, which must keep the legacy path normalisation (AUD-17).
    /// </summary>
    private static bool IsNonFileUrl(string location)
    {
        var separator = location.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0) return false;

        // A scheme is ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ); reject anything else so a Windows
        // path that merely happens to contain "://" is not mistaken for a URL.
        if (!char.IsLetter(location[0])) return false;
        for (var i = 1; i < separator; i++)
            if (!(char.IsLetterOrDigit(location[i]) || location[i] is '+' or '-' or '.'))
                return false;

        return !location.AsSpan(0, separator).Equals("file", StringComparison.OrdinalIgnoreCase);
    }
    public static string GetDefaultExplorerLocation(ShellPathComparer? shellPathComparer = null)
    {
        var id = RegistryManager.GetDefaultExplorerLaunchId();
        var location = id switch
        {
            2 => "shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}",// Home, Quick Access
            3 => "shell:::{088E3905-0323-4B02-9826-5D99428E115F}",// Downloads
            4 => "shell:::{018D5C66-4533-4307-9B53-224DE2ED1FE6}",// OneDrive
            _ => "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}" // This PC
        };

        if (shellPathComparer == null)
            return location;

        var pidl = shellPathComparer.GetPidlFromPath(location);
        var path = ShellPathComparer.GetPathFromPidl(pidl); //SIGDN_URL: Downloads -> file:///C:/Users/Username/Downloads
        Marshal.FreeCoTaskMem(pidl);

        return NormalizeLocation(path ?? location);
    }

    public static string GetExecutablePath()
    {
        var processName = Process.GetCurrentProcess().MainModule?.FileName;
        return processName is { Length: > 0 } ? processName : $"{AppDomain.CurrentDomain.FriendlyName}.exe";
    }

    public static async Task<List<SupporterInfo>> GetSupporters()
    {
        try
        {
            // Session cache (ENH-01): the sponsors SVG is identical for the whole run, so reopening
            // the About page must not fetch it again.
            var svgBytes = await HttpByteCache.GetBytesAsync("https://cdn.jsdelivr.net/gh/w4po/sponsors/sponsors.svg");
            var svgContent = System.Text.Encoding.UTF8.GetString(svgBytes);

            var supporters = new List<SupporterInfo>();
            var xmlDoc = new System.Xml.XmlDocument();
            xmlDoc.LoadXml(svgContent);

            // Find all <a> elements (supporters)
            var linkNodes = xmlDoc.GetElementsByTagName("a");

            foreach (System.Xml.XmlNode linkNode in linkNodes)
            {
                if (linkNode is not System.Xml.XmlElement linkElement)
                    continue;

                var href = linkElement.GetAttribute("href");
                var id = linkElement.GetAttribute("id");

                // Find the image element inside the link
                var imageElements = linkElement.GetElementsByTagName("image");
                if (imageElements.Count <= 0 || imageElements[0] is not System.Xml.XmlElement imageElement)
                    continue;

                var imageUrl = imageElement.GetAttribute("href");

                supporters.Add(new SupporterInfo
                {
                    Name = string.IsNullOrWhiteSpace(id) ? "Unknown" : id,
                    // Null rather than "": NavigateUri cannot convert an empty string to a Uri.
                    ProfileUrl = string.IsNullOrEmpty(href) ? null : href,
                    ImageUrl = string.IsNullOrEmpty(imageUrl) ? string.Empty : imageUrl
                });
            }

            return supporters;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error parsing SVG: {ex.Message}");
            return [];
        }
    }
}