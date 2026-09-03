using Microsoft.Extensions.Logging;
using Ridder.Hosting.Dokploy.Models;
using System.Text.Json;

namespace Ridder.Hosting.Dokploy.Services;

internal sealed class DokployProjectService
{
    private readonly DokployApiClient _client;

    internal DokployProjectService(DokployApiClient client)
    {
        _client = client;
    }

    internal async Task<DokployProject> GetProjectOrCreateAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Project id/name must be provided.", nameof(name));
        }

        using var allResponse = await _client.Http.GetAsync("api/project.all");

        if (allResponse.IsSuccessStatusCode)
        {
            var existing = await DokployResponseReaders.FindProjectByNameFromResponseAsync(allResponse, name);
            if (existing is not null)
            {
                _client.Logger.LogInformation("Project {ProjectName} already exists with id {ProjectId}.", existing.Name, existing.Id);
                return existing;
            }
        }

        _client.Logger.LogInformation("Project {ProjectName} not found. Creating new project.", name);

        var createBody = JsonSerializer.Serialize(new
        {
            name,
            description = "Project created from Aspire hosting environment.",
            env = _client.Env.EnvironmentName
        }, DokployApiClient.JsonOptions);

        using var createResponse = await _client.Http.PostAsync("api/project.create", DokployApiClient.CreateJsonContent(createBody));
        _client.Logger.LogInformation("Create project response: {StatusCode} - {ReasonPhrase}", createResponse.StatusCode, createResponse.ReasonPhrase);
        createResponse.EnsureSuccessStatusCode();

        return await DokployResponseReaders.ReadProjectFromResponseAsync(createResponse)
            ?? throw new InvalidOperationException($"Dokploy returned success for project '{name}', but no project payload was found.");
    }
}
