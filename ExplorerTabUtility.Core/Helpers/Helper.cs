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

    /// <summary>
    /// How long a positive Ctrl+Shift reading is remembered after the keys themselves are released.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gesture and the window it asks for are separated in time: the window appears up to ~2.5 s later
    /// (the watcher polls for it), and by then the user has usually let go of the keys — so without a
    /// memory the "force this one open as a window" gesture would simply be lost. The price is the other
    /// side of the same coin: any <em>other</em> window opened within this window of the gesture is also
    /// forced open as a window instead of folding into a tab.
    /// </para>
    /// <para>
    /// Shorter risks losing the gesture when Explorer is slow; "consume it on first use" is not an option
    /// because two different handlers (<c>OnShellWindowRegistered</c> and <c>OnWindowShown</c>) ask about
    /// the same window and have to agree on the answer.
    /// </para>
    /// </remarks>
    private const int CtrlShiftMemoryMs = 1_000;

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
    /// <summary>
    /// Hides a window until <see cref="ShowWindow"/> puts it back.
    /// </summary>
    /// <param name="hWnd">The window to hide.</param>
    /// <param name="keepTheme">
    /// Hides by moving the window off-screen instead of fading it out. The upstream WPF build exposed this
    /// as an "I have theme issues" setting: a custom File Explorer theme can misbehave when the window is
    /// made transparent, but it survives being parked at −32000. <b>Not reachable from the WinUI shell</b>
    /// — no settings entry passes <c>true</c>, and the README says so. It is kept (rather than deleted)
    /// because the mechanism itself is upstream's answer to a real problem; exposing it again means adding
    /// a settings toggle and forwarding it from the two call sites in <c>ExplorerWatcher</c>.
    /// </param>
    /// <summary>Where the keep-theme hiding mode parks a window so it is off every screen.</summary>
    private const int OffScreenCoordinate = -32_000;

    public static void HideWindow(nint hWnd, bool keepTheme = false)
    {
        // Deliberately NOT ConcurrentDictionary.GetOrAdd: the value factory here performs real side
        // effects (moving the window off-screen, or flipping its layered/alpha attributes), and
        // GetOrAdd may run that factory more than once when several threads miss the key at the same
        // moment — i.e. the window got moved / made transparent twice. Check first, do the work, then
        // TryAdd; a lost race simply keeps whatever the winner already recorded (AUD-19).
        //
        // The entry is verified rather than trusted. This cache deliberately survives a shell teardown
        // (that is what lets a window stranded by a crash be restored — see
        // ExplorerWatcher.InitializeShellObjects), and Windows recycles window handles, so a stale entry
        // can match a brand new window: the hide would then be skipped and that window would never fold
        // into a tab. Anything not actually hidden is treated as stale and re-hidden.
        if (HiddenWindows.TryGetValue(hWnd, out var recorded) && IsStillHidden(hWnd, recorded))
            return;

        HiddenWindows.TryRemove(hWnd, out _);

        RECT? originalPos = null;

        if (keepTheme)
        {
            WinApi.GetWindowRect(hWnd, out var rect);
            originalPos = rect;

            // Move it off-screen
            const uint flags = WinApi.SWP_HIDEWINDOW | WinApi.SWP_NOSIZE | WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE | WinApi.SWP_FRAMECHANGED;
            WinApi.SetWindowPos(hWnd, 0, OffScreenCoordinate, OffScreenCoordinate, 0, 0, flags);
        }
        else
        {
            // Set the transparency (alpha value) of the window (0 = transparent, 255 = opaque)
            UpdateWindowLayered(hWnd, remove: false);
            WinApi.SetLayeredWindowAttributes(hWnd, 0, 0, WinApi.LWA_ALPHA);
        }

        HiddenWindows.TryAdd(hWnd, originalPos);
    }

    /// <summary>
    /// True when a window recorded as hidden is still in the hidden state this app put it in.
    /// </summary>
    /// <remarks>
    /// The two hiding modes leave different fingerprints, and the recorded value says which one was used:
    /// a non-null <paramref name="recordedPosition"/> means the window was parked off-screen, otherwise it
    /// was made transparent, which needs <c>WS_EX_LAYERED</c>. A window that shows neither is a different
    /// window wearing a recycled handle.
    /// </remarks>
    private static bool IsStillHidden(nint hWnd, RECT? recordedPosition)
    {
        if (recordedPosition is not null)
        {
            if (!WinApi.GetWindowRect(hWnd, out var current)) return false;
            return current.Left <= OffScreenCoordinate || current.Top <= OffScreenCoordinate;
        }

        return (WinApi.GetWindowLong(hWnd, WinApi.GWL_EXSTYLE) & WinApi.WS_EX_LAYERED) != 0;
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
        // The positive result is deliberately sticky for CtrlShiftMemoryMs — see the constant's remarks for
        // why the gesture needs a memory and what it costs.
        if (_lastCtrlShiftCheckValue && Environment.TickCount - _lastCtrlShiftCheckAt < CtrlShiftMemoryMs)
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

        // Trim FIRST — before the "is this a URL / a shell path?" tests rather than after them.
        // Both edge characters are things a paste brings along, and every test below looks at the first
        // character of the string: with the trim at the end, "  https://host/x  " failed the URL test
        // (its first character is a space), fell through to the path rules and was rewritten into
        // "https:\\host\x" — a URL destroyed by padding. "  {GUID}  " lost its shell:: prefix the same way.
        location = TrimEdgeWhitespaceAndQuotes(location);

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

        // Kept after the expansion as well: an environment variable's value can itself carry edge
        // whitespace, which would otherwise survive into the rules below.
        location = TrimEdgeWhitespaceAndQuotes(location);
        location = location.TrimEnd('/', '\\');

        // A bare "C:" means "current directory on drive C", not its root; restore the trailing
        // separator so the meaning does not depend on the process working directory.
        if (location.Length == 2 && char.IsLetter(location[0]) && location[1] == ':')
            location += '\\';

        return location.Replace('/', '\\');
    }

    /// <summary>
    /// Removes leading and trailing whitespace and quote characters — neither carries path meaning.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> a separator trim: a leading <c>\\</c> is the UNC marker and <c>\\?\</c>
    /// the extended-length prefix, so separators may only be stripped from the <em>end</em> — which the
    /// caller does in its own step (AUD-03).
    /// <para>
    /// A hand-rolled loop rather than <see cref="string.Trim()"/> plus a second <c>Trim(char[])</c>: the
    /// two would have to be applied in both orders to cover a value like <c>" ' C:\x ' "</c>, and
    /// <see cref="char.IsWhiteSpace(char)"/> already covers the exotic spaces (a non-breaking space is a
    /// common artefact of pasting from a web page) that a fixed character list would miss.
    /// </para>
    /// </remarks>
    private static string TrimEdgeWhitespaceAndQuotes(string value)
    {
        var start = 0;
        var end = value.Length - 1;

        while (start <= end && IsEdgeCharacter(value[start])) start++;
        while (end >= start && IsEdgeCharacter(value[end])) end--;

        return start == 0 && end == value.Length - 1
            ? value
            : value[start..(end + 1)];

        static bool IsEdgeCharacter(char c) => char.IsWhiteSpace(c) || c is '"' or '\'';
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