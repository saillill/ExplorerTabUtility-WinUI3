using Xunit;

namespace ExplorerTabUtility.Tests;

/// <summary>
/// Smoke test for the test toolchain itself. If this one fails nothing else is meaningful.
/// </summary>
public class ToolchainTests
{
    [Fact]
    public void Core_assembly_is_referenced()
    {
        Assert.Equal("ExplorerTabUtility.Core", typeof(Helpers.Constants).Assembly.GetName().Name);
    }
}
