using System.Reflection;

namespace DWIS.Semantic.QueryRegistration.Tests;

public class LayoutTests
{
    [Fact]
    public void Library_is_built_and_referenced()
    {
        Assembly.Load("DWIS.Semantic.QueryRegistration").GetName().Name.Should().Be("DWIS.Semantic.QueryRegistration");
    }
}
