using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Smser.Tests;

/// <summary>
/// The real app, booted in-process, for tests that need rendered HTML or a response
/// header rather than a return value.
///
/// The storage connection string is a placeholder: no test here reaches storage, and the
/// Azure Table client is constructed lazily rather than connected at startup, so this
/// boots with no Azurite and no Docker.
/// </summary>
internal sealed class SmserApp : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SmserApp()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:tables"] = "UseDevelopmentStorage=true" }));
        });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public async Task<string> GetPageAsync(string url)
    {
        var response = await _client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET {url}");

        return await response.Content.ReadAsStringAsync();
    }

    public async Task<string> PostFormAsync(string url, Dictionary<string, string> fields)
    {
        var response = await _client.PostAsync(url, new FormUrlEncodedContent(fields));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"POST {url}");

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// A POST that hands back the response rather than asserting on it, for the cases
    /// where the status code is the thing under test — a rate limiter answering 429, say.
    /// </summary>
    public Task<HttpResponseMessage> PostFormRawAsync(string url, Dictionary<string, string> fields) =>
        _client.PostAsync(url, new FormUrlEncodedContent(fields));

    /// <summary>A GET that hands back the response, for the same reason.</summary>
    public Task<HttpResponseMessage> GetRawAsync(string url) => _client.GetAsync(url);

    /// <summary>
    /// A HEAD, for checking that something is served and what headers come back without
    /// pulling a three-megabyte WebAssembly module through the test host to find out.
    /// </summary>
    public Task<HttpResponseMessage> HeadAsync(string url) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
