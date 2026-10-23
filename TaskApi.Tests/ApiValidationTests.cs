using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TaskApi.Tests;

// These requests fail before database I/O. The real HTTP pipeline can therefore
// be tested without substituting an EF provider or needing Docker.
[Trait("Category", "Validation")]
public sealed class ApiValidationTests : IClassFixture<ApiOnlyFactory>
{
    private readonly HttpClient _client;
    public ApiValidationTests(ApiOnlyFactory factory) => _client = factory.CreateClient();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task Empty_titles_are_rejected(string? title)
    {
        using var response = await _client.PostAsJsonAsync("/tasks", new { title });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("title", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{\"title\":\"Test\",\"priority\":\"Urgent\"}")]
    [InlineData("{\"title\":\"Test\",\"priority\":99}")]
    [InlineData("{\"title\":\"Test\",\"priority\":null}")]
    [InlineData("{\"title\":")]
    public async Task Invalid_priorities_and_malformed_json_are_rejected(string json)
    {
        using var response = await _client.PostAsync("/tasks", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Titles_over_200_characters_are_rejected()
    {
        using var response = await _client.PostAsJsonAsync("/tasks", new { title = new string('a', 201) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("PUT", "/tasks/not-a-guid/complete")]
    [InlineData("DELETE", "/tasks/not-a-guid")]
    public async Task Malformed_guids_are_rejected(string method, string url)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public sealed class ApiOnlyFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ApplyMigrations"] = "false"
            }));
    }
}
