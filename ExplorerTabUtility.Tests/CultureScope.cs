using System;
using System.Globalization;
using System.Threading;

namespace ExplorerTabUtility.Tests;

/// <summary>
/// Pins <see cref="CultureInfo.CurrentUICulture"/> for the duration of a test.
/// <para>
/// Needed because a few assertions are about <em>translated</em> strings, and the neutral (English)
/// resource file legitimately contains labels that read the same as their enum member name — for
/// example <c>Action_Duplicate = "Duplicate"</c>. A test that asserted "the label is not the enum
/// name" would therefore pass on a Chinese machine and fail on an English CI runner, so the culture is
/// pinned rather than inherited.
/// </para>
/// </summary>
public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _previousUiCulture;
    private readonly CultureInfo _previousCulture;

    private CultureScope(string code)
    {
        _previousUiCulture = CultureInfo.CurrentUICulture;
        _previousCulture = CultureInfo.CurrentCulture;

        var culture = new CultureInfo(code);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
    }

    public static CultureScope Use(string code) => new(code);

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _previousUiCulture;
        CultureInfo.CurrentCulture = _previousCulture;
        Thread.CurrentThread.CurrentUICulture = _previousUiCulture;
        Thread.CurrentThread.CurrentCulture = _previousCulture;
    }
}
