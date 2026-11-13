using System.Reflection;

namespace DWIS.Semantic.Ingest.Tests;

public class LayoutTests
{
    [Fact]
    public void Library_is_built_and_referenced()
    {
        Assembly.Load("DWIS.Semantic.Ingest").GetName().Name.Should().Be("DWIS.Semantic.Ingest");
    }
}
