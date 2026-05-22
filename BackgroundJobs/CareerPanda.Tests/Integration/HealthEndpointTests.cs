using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CareerPanda.Tests.Integration;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact(Skip = "Requires PostgreSQL running at appsettings Connection string")]
    public async Task Health_Returns_Success_Or_ServiceUnavailable()
    {
        var response = await _client.GetAsync("/health");
        Assert.True(
            response.StatusCode == System.Net.HttpStatusCode.OK ||
            response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable);
    }
}
