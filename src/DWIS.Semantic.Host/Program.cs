// The semantic service host. Milestone 1 gives it only a health endpoint; the manifest and SPARQL
// endpoints of specification 3.4 arrive with issue M1.7, the configuration library with M1.2.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/api/v1/health");

app.Run();

/// <summary>Lets the test project host the application with <c>WebApplicationFactory</c>.</summary>
public partial class Program
{
}
