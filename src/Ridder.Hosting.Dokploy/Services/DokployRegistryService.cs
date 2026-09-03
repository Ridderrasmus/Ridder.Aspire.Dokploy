using Microsoft.Extensions.Logging;
using Ridder.Hosting.Dokploy.Models;
using Ridder.Hosting.Dokploy.Utilities;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ridder.Hosting.Dokploy.Services;

internal sealed class DokployRegistryService
{
    private readonly DokployApiClient _client;

    internal DokployRegistryService(DokployApiClient client)
    {
        _client = client;
    }

    internal async Task<DokployRegistry> GetOrCreateRegistryAsync(DokployProject proj)
    {
        ArgumentNullException.ThrowIfNull(proj);
        if (string.IsNullOrWhiteSpace(proj.Id))
        {
            throw new InvalidOperationException($"Project '{proj.Name}' has no id. Cannot create registry compose resource without project id.");
        }

        using var projectResponse = await _client.Http.GetAsync($"api/project.one?projectId={Uri.EscapeDataString(proj.Id)}");
        projectResponse.EnsureSuccessStatusCode();

        var refreshedProject = await DokployResponseReaders.ReadProjectFromResponseAsync(projectResponse)
            ?? throw new InvalidOperationException($"Could not parse project details for project id '{proj.Id}'.");

        var targetEnvironment = refreshedProject.Environments
            .FirstOrDefault(e => string.Equals(e.Name, "production", StringComparison.OrdinalIgnoreCase))
            ?? refreshedProject.Environments.FirstOrDefault();

        if (targetEnvironment is null || string.IsNullOrWhiteSpace(targetEnvironment.Id))
        {
            throw new InvalidOperationException($"Project '{refreshedProject.Name}' has no usable environment (expected one named 'production').");
        }

        if (_client.RegistrySettings.Mode == DokployRegistryMode.Hosted)
        {
            var hostedRegistryName = $"{refreshedProject.Name}-registry";
            var linkedHostedRegistry = await EnsureRegistryLinkedAsync(hostedRegistryName);
            var hostedRegistryUrl = NormalizeRegistryHost(linkedHostedRegistry?.RegistryUrl ?? GetRegistryUrl());
            var hostedPushPrefix = ResolveHostedPushPrefix(hostedRegistryUrl, linkedHostedRegistry?.Username ?? _client.RegistrySettings.Username, linkedHostedRegistry?.ImagePrefix);
            return new DokployRegistry
            {
                RegistryId = linkedHostedRegistry?.RegistryId,
                RegistryUrl = hostedRegistryUrl,
                ProjectId = refreshedProject.Id,
                EnvironmentId = targetEnvironment.Id,
                Name = linkedHostedRegistry?.RegistryName ?? hostedRegistryName,
                Username = linkedHostedRegistry?.Username ?? _client.RegistrySettings.Username,
                Password = linkedHostedRegistry?.Password ?? _client.RegistrySettings.Password,
                PushPrefix = hostedPushPrefix
            };
        }

        var existingRegistry = targetEnvironment.Compose
            .FirstOrDefault(c => string.Equals(c.Name, "registry", StringComparison.OrdinalIgnoreCase));

        if (existingRegistry is not null)
        {
            var existingRegistryDetails = await GetComposeDetailsAsync(existingRegistry);
            await DeployComposeAsync(existingRegistryDetails);
            await EnsureRegistryComposeDomainAsync(existingRegistryDetails);
            var linkedRegistry = await EnsureRegistryLinkedAsync(existingRegistryDetails.Name);
            var access = ResolveSelfHostedRegistryAccess(existingRegistryDetails, linkedRegistry);

            _client.Logger.LogInformation("Registry compose already exists for project {ProjectName} in environment {EnvironmentName}.", refreshedProject.Name, targetEnvironment.Name);
            return new DokployRegistry
            {
                RegistryId = linkedRegistry?.RegistryId,
                RegistryUrl = access.RegistryUrl,
                ProjectId = refreshedProject.Id,
                EnvironmentId = targetEnvironment.Id,
                ComposeId = existingRegistryDetails.Id ?? existingRegistry.Id,
                Name = existingRegistryDetails.Name,
                Username = access.Username,
                Password = access.Password,
                PushPrefix = access.RegistryUrl
            };
        }

        var deployBody = JsonSerializer.Serialize(new
        {
            environmentId = targetEnvironment.Id,
            id = "registry"
        }, DokployApiClient.JsonOptions);

        using var deployResponse = await _client.Http.PostAsync("api/compose.deployTemplate", DokployApiClient.CreateJsonContent(deployBody));
        deployResponse.EnsureSuccessStatusCode();

        var deployedCompose = await DokployResponseReaders.ReadComposeFromResponseAsync(deployResponse);
        if (deployedCompose is not null)
        {
            _client.Logger.LogInformation("Registry compose deployment accepted for project {ProjectName}. Verifying via project.one.", refreshedProject.Name);
        }

        using var verifyResponse = await _client.Http.GetAsync($"api/project.one?projectId={Uri.EscapeDataString(proj.Id)}");
        verifyResponse.EnsureSuccessStatusCode();

        var verifiedProject = await DokployResponseReaders.ReadProjectFromResponseAsync(verifyResponse)
            ?? throw new InvalidOperationException($"Registry deploy completed but project '{proj.Name}' could not be reloaded for verification.");

        var verifiedEnvironment = verifiedProject.Environments
            .FirstOrDefault(e => string.Equals(e.Name, targetEnvironment.Name, StringComparison.OrdinalIgnoreCase));

        var verifiedRegistry = verifiedEnvironment?.Compose
            .FirstOrDefault(c => string.Equals(c.Name, "registry", StringComparison.OrdinalIgnoreCase));

        if (verifiedRegistry is null)
        {
            throw new InvalidOperationException($"Registry compose deploy returned success but no 'registry' compose was found for project '{verifiedProject.Name}'.");
        }

        var verifiedRegistryDetails = await GetComposeDetailsAsync(verifiedRegistry);
        await DeployComposeAsync(verifiedRegistryDetails);
        await EnsureRegistryComposeDomainAsync(verifiedRegistryDetails);
        var linkedVerifiedRegistry = await EnsureRegistryLinkedAsync(verifiedRegistryDetails.Name);
        var verifiedAccess = ResolveSelfHostedRegistryAccess(verifiedRegistryDetails, linkedVerifiedRegistry);

        return new DokployRegistry
        {
            RegistryId = linkedVerifiedRegistry?.RegistryId,
            RegistryUrl = verifiedAccess.RegistryUrl,
            ProjectId = verifiedProject.Id,
            EnvironmentId = verifiedEnvironment?.Id,
            ComposeId = verifiedRegistryDetails.Id ?? verifiedRegistry.Id,
            Name = verifiedRegistryDetails.Name,
            Username = verifiedAccess.Username,
            Password = verifiedAccess.Password,
            PushPrefix = verifiedAccess.RegistryUrl
        };
    }

    private RegistryAccess ResolveSelfHostedRegistryAccess(DokployCompose compose, DokployRemoteRegistry? linkedRegistry)
    {
        var registryUrl = linkedRegistry?.RegistryUrl ?? GetRegistryUrl();
        var username = ResolveRegistryUsername(compose, linkedRegistry);
        var password = ResolveRegistryPassword(compose, linkedRegistry);

        return new RegistryAccess(registryUrl, username, password);
    }

    private async Task EnsureRegistryComposeDomainAsync(DokployCompose compose)
    {
        if (string.IsNullOrWhiteSpace(compose.Id))
        {
            throw new InvalidOperationException($"Compose '{compose.Name}' has no composeId, so compose domain cannot be verified.");
        }

        var registryHost = GetRegistryUrl();
        using var byComposeResponse = await _client.Http.GetAsync($"api/domain.byComposeId?composeId={Uri.EscapeDataString(compose.Id)}");
        byComposeResponse.EnsureSuccessStatusCode();

        var existingDomains = await DokployResponseReaders.ReadDomainsFromResponseAsync(byComposeResponse, _client.Logger, "domain.byComposeId");
        var existingDomain = existingDomains.FirstOrDefault(d => string.Equals(d.Host, registryHost, StringComparison.OrdinalIgnoreCase));
        if (existingDomain is not null)
        {
            if (string.IsNullOrWhiteSpace(existingDomain.Id))
            {
                throw new InvalidOperationException($"Compose domain '{registryHost}' exists for compose '{compose.Name}' but no domainId was returned.");
            }

            var updateBody = JsonSerializer.Serialize(new
            {
                domainId = existingDomain.Id,
                host = registryHost,
                port = 5000,
                https = true,
                certificateType = "letsencrypt",
                serviceName = "registry",
                domainType = "compose"
            }, DokployApiClient.JsonOptions);

            using var updateResponse = await _client.Http.PostAsync("api/domain.update", DokployApiClient.CreateJsonContent(updateBody));
            updateResponse.EnsureSuccessStatusCode();
            _client.Logger.LogInformation("Updated compose domain {DomainHost} for registry compose {ComposeName}.", registryHost, compose.Name);
            return;
        }

        var createBody = JsonSerializer.Serialize(new
        {
            composeId = compose.Id,
            host = registryHost,
            port = 5000,
            https = true,
            certificateType = "letsencrypt",
            serviceName = "registry",
            domainType = "compose"
        }, DokployApiClient.JsonOptions);

        using var createResponse = await _client.Http.PostAsync("api/domain.create", DokployApiClient.CreateJsonContent(createBody));
        createResponse.EnsureSuccessStatusCode();
        _client.Logger.LogInformation("Created compose domain {DomainHost} for registry compose {ComposeName}.", registryHost, compose.Name);
    }

    private async Task<DokployCompose> GetComposeDetailsAsync(DokployCompose compose)
    {
        if (string.IsNullOrWhiteSpace(compose.Id))
        {
            throw new InvalidOperationException($"Compose '{compose.Name}' does not have composeId, so compose.one cannot be called.");
        }

        using var composeResponse = await _client.Http.GetAsync($"api/compose.one?composeId={Uri.EscapeDataString(compose.Id)}");
        composeResponse.EnsureSuccessStatusCode();

        var fullCompose = await DokployResponseReaders.ReadComposeFromResponseAsync(composeResponse)
            ?? throw new InvalidOperationException($"compose.one returned success but no compose payload for composeId '{compose.Id}'.");

        return fullCompose;
    }

    private async Task DeployComposeAsync(DokployCompose compose)
    {
        if (string.IsNullOrWhiteSpace(compose.Id))
        {
            throw new InvalidOperationException($"Compose '{compose.Name}' does not have composeId, so compose.deploy cannot be called.");
        }

        var deployBody = JsonSerializer.Serialize(new
        {
            composeId = compose.Id,
            title = $"Aspire deploy for {compose.Name}",
            description = "Started automatically before registry link verification."
        }, DokployApiClient.JsonOptions);

        using var deployResponse = await _client.Http.PostAsync("api/compose.deploy", DokployApiClient.CreateJsonContent(deployBody));
        _client.Logger.LogInformation("Compose deploy response for {ComposeName}: {StatusCode} - {ReasonPhrase}", compose.Name, deployResponse.StatusCode, deployResponse.ReasonPhrase);
        deployResponse.EnsureSuccessStatusCode();
    }

    private async Task<DokployRemoteRegistry?> EnsureRegistryLinkedAsync(string registryName)
    {
        var registryUrl = NormalizeRegistryHost(GetRegistryUrl());
        var username = _client.RegistrySettings.Username;
        var password = _client.RegistrySettings.Password;
        var imagePrefix = ResolveHostedPushPrefix(registryUrl, username, imagePrefix: null);

        var testInput = new RegistryRequestPayload
        {
            RegistryName = registryName,
            Username = username,
            Password = password,
            RegistryUrl = registryUrl,
            RegistryType = _client.RegistrySettings.RegistryType
        };

        var existingRegistry = await FindExistingRegistryAsync(testInput);
        if (existingRegistry is not null)
        {
            _client.Logger.LogInformation("Registry URL {RegistryUrl} already exists as registryId {RegistryId}.", registryUrl, existingRegistry.RegistryId);
            return existingRegistry;
        }

        var works = await TestRegistryLinkedAsync(testInput);
        if (!works)
        {
            _client.Logger.LogInformation("Registry URL {RegistryUrl} is not yet valid in Dokploy. Will attempt creation.", registryUrl);
        }

        var createBody = JsonSerializer.Serialize(new
        {
            registryName,
            username,
            password,
            registryUrl,
            registryType = _client.RegistrySettings.RegistryType,
            imagePrefix
        }, DokployApiClient.JsonOptions);

        using var createResponse = await _client.Http.PostAsync("api/registry.create", DokployApiClient.CreateJsonContent(createBody));
        _client.Logger.LogInformation("Create registry response: {StatusCode} - {ReasonPhrase}", createResponse.StatusCode, createResponse.ReasonPhrase);
        createResponse.EnsureSuccessStatusCode();
        _client.Logger.LogInformation("Created Dokploy registry entry for {RegistryUrl}.", registryUrl);

        var createdRegistry = await DokployResponseReaders.ReadRegistryFromResponseAsync(createResponse);
        if (createdRegistry is not null)
        {
            return createdRegistry;
        }

        return await FindExistingRegistryAsync(testInput);
    }

    private async Task<DokployRemoteRegistry?> FindExistingRegistryAsync(RegistryRequestPayload payload)
    {
        using var allResponse = await _client.Http.GetAsync("api/registry.all");
        allResponse.EnsureSuccessStatusCode();

        var registries = await DokployResponseReaders.ReadRegistriesFromResponseAsync(allResponse);
        return registries.FirstOrDefault(r =>
            string.Equals(r.RegistryUrl, payload.RegistryUrl, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Username, payload.Username, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> TestRegistryLinkedAsync(RegistryRequestPayload payload)
    {
        var testBody = JsonSerializer.Serialize(new
        {
            registryName = payload.RegistryName,
            username = payload.Username,
            password = payload.Password,
            registryUrl = payload.RegistryUrl,
            registryType = payload.RegistryType
        }, DokployApiClient.JsonOptions);

        _client.Logger.LogInformation(
            "Testing Dokploy registry link for {RegistryName} at {RegistryUrl} with username {Username}.",
            payload.RegistryName,
            payload.RegistryUrl,
            payload.Username);
        using var testResponse = await _client.Http.PostAsync("api/registry.testRegistry", DokployApiClient.CreateJsonContent(testBody));
        var responseContent = await testResponse.Content.ReadAsStringAsync();
        _client.Logger.LogInformation("Test registry response: {StatusCode} - {ReasonPhrase}", testResponse.StatusCode, responseContent);

        if (!testResponse.IsSuccessStatusCode)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return false;
        }

        using var json = JsonDocument.Parse(responseContent);
        return DokployJsonPayload.ExtractLinkState(json.RootElement);
    }

    private string GetRegistryUrl()
    {
        return _client.RegistrySettings.RegistryUrl;
    }

    internal static string ResolveHostedPushPrefix(string registryUrl, string username, string? imagePrefix)
    {
        var normalizedRegistry = NormalizeRegistryHost(registryUrl);
        var normalizedPrefix = NormalizeImagePrefix(imagePrefix);

        if (string.IsNullOrWhiteSpace(normalizedPrefix) || string.Equals(normalizedPrefix, normalizedRegistry, StringComparison.OrdinalIgnoreCase))
        {
            return RequiresRepositoryNamespace(normalizedRegistry)
                ? $"{normalizedRegistry}/{username}"
                : normalizedRegistry;
        }

        if (RequiresRepositoryNamespace(normalizedRegistry) && !normalizedPrefix.Contains('/', StringComparison.Ordinal))
        {
            return $"{normalizedRegistry}/{normalizedPrefix}";
        }

        if (!normalizedPrefix.StartsWith($"{normalizedRegistry}/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalizedPrefix, normalizedRegistry, StringComparison.OrdinalIgnoreCase)
            && !RequiresRepositoryNamespace(normalizedRegistry))
        {
            return $"{normalizedRegistry}/{normalizedPrefix}";
        }

        return normalizedPrefix;
    }

    internal static string NormalizeRegistryHost(string registryUrl)
    {
        if (string.IsNullOrWhiteSpace(registryUrl))
        {
            return registryUrl;
        }

        var trimmed = registryUrl.Trim().TrimEnd('/');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.Authority;
        }

        return trimmed.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
    }

    private static string? NormalizeImagePrefix(string? imagePrefix)
    {
        if (string.IsNullOrWhiteSpace(imagePrefix))
        {
            return null;
        }

        return imagePrefix.Trim()
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
    }

    private static bool RequiresRepositoryNamespace(string registryUrl)
    {
        return string.Equals(registryUrl, "docker.io", StringComparison.OrdinalIgnoreCase)
            || string.Equals(registryUrl, "index.docker.io", StringComparison.OrdinalIgnoreCase)
            || string.Equals(registryUrl, "registry-1.docker.io", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRegistryUsername(DokployCompose compose, DokployRemoteRegistry? linkedRegistry)
    {
        if (!string.IsNullOrWhiteSpace(linkedRegistry?.Username))
        {
            return linkedRegistry.Username;
        }

        var envUsername = TryGetEnvValue(compose.Env, "REGISTRY_USERNAME", "REGISTRY_USER", "USERNAME", "REGISTRY_HTPASSWD_USERNAME");
        if (!string.IsNullOrWhiteSpace(envUsername))
        {
            return envUsername;
        }

        return GetRegistryUsernameFromCompose(compose);
    }

    private static string ResolveRegistryPassword(DokployCompose compose, DokployRemoteRegistry? linkedRegistry)
    {
        if (!string.IsNullOrWhiteSpace(linkedRegistry?.Password))
        {
            return linkedRegistry.Password;
        }

        return GetRegistryPasswordFromCompose(compose);
    }

    internal static string GetRegistryUsernameFromCompose(DokployCompose compose)
    {
        var envUsername = TryGetEnvValue(compose.Env, "REGISTRY_USERNAME", "REGISTRY_USER", "USERNAME", "REGISTRY_HTPASSWD_USERNAME");
        if (!string.IsNullOrWhiteSpace(envUsername))
        {
            return envUsername;
        }

        var composeFile = NormalizeTemplateText(compose.ComposeFile);
        if (string.IsNullOrWhiteSpace(composeFile))
        {
            throw new InvalidOperationException($"Compose '{compose.Name}' has no compose file content to extract registry username from.");
        }

        var keyMatch = Regex.Match(composeFile, @"(?im)^\s*(REGISTRY_USERNAME|REGISTRY_USER|USERNAME)\s*[:=]\s*""?([^""\r\n#]+)");
        if (keyMatch.Success)
        {
            return keyMatch.Groups[2].Value.Trim();
        }

        var authModeMatch = Regex.Match(composeFile, @"(?im)^\s*REGISTRY_AUTH\s*[:=]\s*""?([^""\r\n#]+)");
        if (authModeMatch.Success)
        {
            return authModeMatch.Groups[1].Value.Trim();
        }

        var userAtHost = Regex.Match(composeFile, @"(?i)\b([a-z0-9._-]+)@[a-z0-9.-]+\b");
        if (userAtHost.Success)
        {
            return userAtHost.Groups[1].Value.Trim();
        }

        throw new InvalidOperationException($"Could not extract registry username from compose file for compose '{compose.Name}'.");
    }

    internal static string GetRegistryPasswordFromCompose(DokployCompose compose)
    {
        var envPassword = TryGetEnvValue(compose.Env, "REGISTRY_PASSWORD", "PASSWORD", "REGISTRY_HTPASSWD_PASSWORD");
        if (!string.IsNullOrWhiteSpace(envPassword))
        {
            return envPassword;
        }

        throw new InvalidOperationException($"Could not extract registry password from compose env for compose '{compose.Name}'.");
    }

    private static string NormalizeTemplateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return text.Replace("\\r\\n", "\n", StringComparison.Ordinal)
                   .Replace("\\n", "\n", StringComparison.Ordinal)
                   .Replace("\\r", "\n", StringComparison.Ordinal);
    }

    private static string? TryGetEnvValue(string envText, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(envText))
        {
            return null;
        }

        var lines = envText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal) || !trimmed.Contains('='))
            {
                continue;
            }

            var idx = trimmed.IndexOf('=');
            var lineKey = trimmed[..idx].Trim();
            if (!keys.Any(key => string.Equals(lineKey, key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var value = trimmed[(idx + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim('"');
            }
        }

        return null;
    }

    private sealed class RegistryRequestPayload
    {
        public string RegistryName { get; init; } = string.Empty;
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public string RegistryUrl { get; init; } = string.Empty;
        public string RegistryType { get; init; } = string.Empty;
    }

    private sealed record RegistryAccess(string RegistryUrl, string Username, string Password);
}
