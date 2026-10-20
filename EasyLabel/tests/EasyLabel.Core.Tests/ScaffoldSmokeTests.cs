using EasyLabel.Core;

namespace EasyLabel.Core.Tests;

public sealed class ScaffoldSmokeTests
{
    [Fact]
    public void CoreProjectReferenceResolves()
    {
        Assert.Equal("EasyLabel.Core", typeof(CoreAssembly).Assembly.GetName().Name);
    }
}
