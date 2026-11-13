using System.Reflection;

namespace DWIS.Semantic.Store.Tests;

public class LayoutTests
{
    [Fact]
    public void Library_is_built_and_referenced()
    {
        Assembly.Load("DWIS.Semantic.Store").GetName().Name.Should().Be("DWIS.Semantic.Store");
    }
}
