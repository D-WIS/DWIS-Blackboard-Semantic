using System.Reflection;

namespace DWIS.Semantic.Validation.Tests;

public class LayoutTests
{
    [Fact]
    public void Library_is_built_and_referenced()
    {
        Assembly.Load("DWIS.Semantic.Validation").GetName().Name.Should().Be("DWIS.Semantic.Validation");
    }
}
