using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ridder.Aspire.Hosting.Dokploy.Models;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;

namespace Ridder.Aspire.Hosting.Dokploy.Services;

internal sealed class DokployApiClient : IDisposable
{
    internal readonly IHostEnvironment Env;
    internal readonly ILogger Logger;
    internal readonly DokployResolvedRegistrySettings RegistrySettings;
    internal readonly HttpClient Http;

    internal DokployApiClient(
        string apiKey,
        string url,
        IHostEnvironment env,
        ILogger logger,
        DokployResolvedRegistrySettings registrySettings,
        HttpMessageHandler? handler = null)
    {
        Env = env;
        Logger = logger;
        RegistrySettings = registrySettings;

        var baseUrl = url.EndsWith("/", StringComparison.Ordinal) ? url : $"{url}/";
        Http = handler is null ? new HttpClient() : new HttpClient(handler);
        Http.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        Http.Timeout = TimeSpan.FromMinutes(5);
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        Http.DefaultRequestHeaders.Add("x-api-key", apiKey);
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public void Dispose()
    {
        Http.Dispose();
        GC.SuppressFinalize(this);
    }

    internal static StringContent CreateJsonContent(string body)
    {
        return new StringContent(body, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"));
    }
}
