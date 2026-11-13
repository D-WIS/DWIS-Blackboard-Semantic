using System.Reflection;

namespace DWIS.Semantic.Manifest.Tests;

public class LayoutTests
{
    [Fact]
    public void Library_is_built_and_referenced()
    {
        Assembly.Load("DWIS.Semantic.Manifest").GetName().Name.Should().Be("DWIS.Semantic.Manifest");
    }
}
