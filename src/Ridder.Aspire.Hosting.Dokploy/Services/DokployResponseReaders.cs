using Microsoft.Extensions.Logging;
using Ridder.Aspire.Hosting.Dokploy.Models;
using Ridder.Aspire.Hosting.Dokploy.Utilities;
using System.Text.Json;

namespace Ridder.Aspire.Hosting.Dokploy.Services;

internal static class DokployResponseReaders
{
    internal static async Task<DokployProject?> ReadProjectFromResponseAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        return TryExtractProject(json.RootElement);
    }

    internal static async Task<DokployProject?> FindProjectByNameFromResponseAsync(HttpResponseMessage response, string expectedName)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        return FindProjectByName(json.RootElement, expectedName);
    }

    internal static async Task<DokployCompose?> ReadComposeFromResponseAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        return TryExtractCompose(json.RootElement);
    }

    internal static async Task<List<DokployRemoteRegistry>> ReadRegistriesFromResponseAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        var output = new List<DokployRemoteRegistry>();
        CollectRegistries(json.RootElement, output);
        return output;
    }

    internal static async Task<DokployRemoteRegistry?> ReadRegistryFromResponseAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        return TryExtractRegistry(json.RootElement);
    }

    internal static async Task<DokployApplication?> ReadApplicationFromResponseAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        return TryExtractApplication(json.RootElement);
    }

    internal static async Task<List<DokployServiceTask>> ReadServiceTasksFromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var tasks = new List<DokployServiceTask>();
        CollectServiceTasks(json.RootElement, tasks);

        return tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.Id))
            .DistinctBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static async Task<string?> ReadDockerImageFromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            // A completed or replaced Swarm task can disappear between the service
            // task listing and docker.getConfig. Dokploy returns an empty 200 in
            // that race, so let the rollout verifier continue polling.
            return null;
        }

        using var json = JsonDocument.Parse(content);
        return FindDockerImage(json.RootElement);
    }

    internal static async Task<List<DokployMount>> ReadMountsFromResponseAsync(HttpResponseMessage response, ILogger? logger = null, string source = "mounts.listByServiceId")
    {
        var content = DokployJsonPayload.Normalize(await response.Content.ReadAsStringAsync());
        logger?.LogInformation("{MountSource} payload: {Payload}", source, DokployJsonPayload.GetPayloadSnippet(content));

        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        using var root = JsonDocument.Parse(content);
        logger?.LogInformation("{MountSource} root kind: {RootKind}", source, root.RootElement.ValueKind);
        var mounts = new List<DokployMount>();
        CollectMounts(root.RootElement, mounts);

        var distinctMounts = mounts
            .Where(m => !string.IsNullOrWhiteSpace(m.MountPath))
            .DistinctBy(m => m.Id ?? $"{m.Type}|{m.MountPath}|{m.HostPath}|{m.VolumeName}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger?.LogInformation("{MountSource} parsed {MountCount} mount(s) for reconciliation.", source, distinctMounts.Count);
        return distinctMounts;
    }

    internal static async Task<List<DokployDomain>> ReadDomainsFromResponseAsync(HttpResponseMessage response, ILogger? logger = null, string source = "domain.byApplicationId")
    {
        var content = DokployJsonPayload.Normalize(await response.Content.ReadAsStringAsync());
        logger?.LogInformation("{DomainSource} payload: {Payload}", source, DokployJsonPayload.GetPayloadSnippet(content));

        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        using var root = JsonDocument.Parse(content);
        logger?.LogInformation("{DomainSource} root kind: {RootKind}", source, root.RootElement.ValueKind);
        if (root.RootElement.ValueKind == JsonValueKind.Object)
        {
            var direct = JsonSerializer.Deserialize<DokployDomain>(content, DokployApiClient.JsonOptions);
            if (direct is not null && !string.IsNullOrWhiteSpace(direct.Host))
            {
                return [direct];
            }

            var wrapped = JsonSerializer.Deserialize<TrpcEnvelope<List<DokployDomain>>>(content, DokployApiClient.JsonOptions);
            var wrappedDomains = wrapped?.Result?.Data?.Json?.Where(d => !string.IsNullOrWhiteSpace(d.Host)).ToList();
            if (wrappedDomains is { Count: > 0 })
            {
                return wrappedDomains;
            }

            var wrappedSingle = JsonSerializer.Deserialize<TrpcEnvelope<DokployDomain>>(content, DokployApiClient.JsonOptions);
            var single = wrappedSingle?.Result?.Data?.Json;
            if (single is not null && !string.IsNullOrWhiteSpace(single.Host))
            {
                return [single];
            }

            return [];
        }

        var directList = JsonSerializer.Deserialize<List<DokployDomain>>(content, DokployApiClient.JsonOptions);
        if (directList is { Count: > 0 })
        {
            return directList.Where(d => !string.IsNullOrWhiteSpace(d.Host)).ToList();
        }

        var wrappedList = JsonSerializer.Deserialize<List<TrpcEnvelope<List<DokployDomain>>>>(content, DokployApiClient.JsonOptions);
        if (wrappedList is { Count: > 0 })
        {
            var domains = wrappedList.SelectMany(x => x.Result?.Data?.Json ?? []).Where(d => !string.IsNullOrWhiteSpace(d.Host)).ToList();
            if (domains.Count > 0)
            {
                return domains;
            }
        }

        var wrappedSingleList = JsonSerializer.Deserialize<List<TrpcEnvelope<DokployDomain>>>(content, DokployApiClient.JsonOptions);
        if (wrappedSingleList is { Count: > 0 })
        {
            var domains = wrappedSingleList.Select(x => x.Result?.Data?.Json).Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Host)).Select(d => d!).ToList();
            if (domains.Count > 0)
            {
                return domains;
            }
        }

        return [];
    }

    internal static async Task<string?> ReadGeneratedHostFromResponseAsync(HttpResponseMessage response, ILogger? logger = null)
    {
        var content = DokployJsonPayload.Normalize(await response.Content.ReadAsStringAsync());
        logger?.LogInformation("domain.generateDomain payload: {Payload}", DokployJsonPayload.GetPayloadSnippet(content));

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        using var root = JsonDocument.Parse(content);
        logger?.LogInformation("domain.generateDomain root kind: {RootKind}", root.RootElement.ValueKind);
        if (root.RootElement.ValueKind == JsonValueKind.String)
        {
            return root.RootElement.GetString();
        }

        if (root.RootElement.ValueKind == JsonValueKind.Object)
        {
            string? fromWrappedString = null;
            try
            {
                var wrappedString = JsonSerializer.Deserialize<TrpcEnvelope<string>>(content, DokployApiClient.JsonOptions);
                fromWrappedString = wrappedString?.Result?.Data?.Json;
            }
            catch (JsonException)
            {
            }

            if (!string.IsNullOrWhiteSpace(fromWrappedString))
            {
                return fromWrappedString;
            }

            var wrappedData = JsonSerializer.Deserialize<TrpcEnvelope<GeneratedDomainData>>(content, DokployApiClient.JsonOptions);
            var fromWrappedData = wrappedData?.Result?.Data?.Json;
            if (fromWrappedData is not null)
            {
                var host = fromWrappedData.Json ?? fromWrappedData.Host ?? fromWrappedData.Domain;
                if (!string.IsNullOrWhiteSpace(host))
                {
                    return host;
                }
            }

            var directData = JsonSerializer.Deserialize<GeneratedDomainData>(content, DokployApiClient.JsonOptions);
            return directData?.Json ?? directData?.Host ?? directData?.Domain;
        }

        return null;
    }

    private static DokployProject? TryExtractProject(JsonElement root)
    {
        if (TryDeserializeProject(root, out var directProject))
        {
            return directProject;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "project", "data", "result" })
            {
                if (root.TryGetProperty(key, out var nested) && TryDeserializeProject(nested, out var nestedProject))
                {
                    return nestedProject;
                }
            }
        }

        return null;
    }

    private static bool TryDeserializeProject(JsonElement value, out DokployProject? project)
    {
        project = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!value.TryGetProperty("name", out _))
        {
            return false;
        }

        project = value.Deserialize<DokployProject>(DokployApiClient.JsonOptions);
        return project is not null;
    }

    private static DokployProject? FindProjectByName(JsonElement value, string expectedName)
    {
        if (TryDeserializeProject(value, out var candidate) && string.Equals(candidate?.Name, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var match = FindProjectByName(item, expectedName);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in value.EnumerateObject())
            {
                var match = FindProjectByName(prop.Value, expectedName);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private static DokployCompose? TryExtractCompose(JsonElement root)
    {
        if (TryDeserializeCompose(root, out var directCompose))
        {
            return directCompose;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "compose", "data", "result" })
            {
                if (root.TryGetProperty(key, out var nested) && TryDeserializeCompose(nested, out var nestedCompose))
                {
                    return nestedCompose;
                }
            }
        }

        return null;
    }

    private static bool TryDeserializeCompose(JsonElement value, out DokployCompose? compose)
    {
        compose = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!value.TryGetProperty("name", out _))
        {
            return false;
        }

        compose = value.Deserialize<DokployCompose>(DokployApiClient.JsonOptions);
        return compose is not null;
    }

    private static DokployRemoteRegistry? TryExtractRegistry(JsonElement element)
    {
        if (TryDeserializeRegistry(element, out var registry))
        {
            return registry;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "registry", "data", "result" })
            {
                if (element.TryGetProperty(key, out var nested))
                {
                    var nestedRegistry = TryExtractRegistry(nested);
                    if (nestedRegistry is not null)
                    {
                        return nestedRegistry;
                    }
                }
            }
        }

        return null;
    }

    private static void CollectRegistries(JsonElement element, List<DokployRemoteRegistry> output)
    {
        if (TryDeserializeRegistry(element, out var registry))
        {
            output.Add(registry);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                CollectRegistries(prop.Value, output);
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectRegistries(item, output);
            }
        }
    }

    private static bool TryDeserializeRegistry(JsonElement value, out DokployRemoteRegistry registry)
    {
        registry = new DokployRemoteRegistry();
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!value.TryGetProperty("registryUrl", out _))
        {
            return false;
        }

        var candidate = value.Deserialize<DokployRemoteRegistry>(DokployApiClient.JsonOptions);
        if (candidate is null)
        {
            return false;
        }

        registry = candidate;
        return true;
    }

    private static DokployApplication? TryExtractApplication(JsonElement root)
    {
        if (TryDeserializeApplication(root, out var directApp))
        {
            return directApp;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "application", "data", "result" })
            {
                if (root.TryGetProperty(key, out var nested) && TryDeserializeApplication(nested, out var nestedApp))
                {
                    return nestedApp;
                }
            }
        }

        return null;
    }

    private static bool TryDeserializeApplication(JsonElement value, out DokployApplication? application)
    {
        application = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!value.TryGetProperty("name", out _) && !value.TryGetProperty("appName", out _))
        {
            return false;
        }

        application = value.Deserialize<DokployApplication>(DokployApiClient.JsonOptions);
        return application is not null;
    }

    private static bool TryDeserializeMount(JsonElement value, out DokployMount? mount)
    {
        mount = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!value.TryGetProperty("mountPath", out _)
            && !value.TryGetProperty("mountId", out _)
            && !value.TryGetProperty("Destination", out _)
            && !value.TryGetProperty("destination", out _))
        {
            return false;
        }

        mount = value.Deserialize<DokployMount>(DokployApiClient.JsonOptions);
        return mount is not null && !string.IsNullOrWhiteSpace(mount.MountPath);
    }

    private static void CollectMounts(JsonElement value, List<DokployMount> output)
    {
        if (TryDeserializeMount(value, out var mount))
        {
            output.Add(mount!);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                CollectMounts(property.Value, output);
            }
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                CollectMounts(item, output);
            }
        }
    }

    private static void CollectServiceTasks(JsonElement value, List<DokployServiceTask> output)
    {
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("containerId", out var containerId)
            && containerId.ValueKind == JsonValueKind.String)
        {
            var task = value.Deserialize<DokployServiceTask>(DokployApiClient.JsonOptions);
            if (task is not null)
            {
                output.Add(task);
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                CollectServiceTasks(property.Value, output);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                CollectServiceTasks(item, output);
            }
        }
    }

    private static string? FindDockerImage(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("ContainerSpec", out var containerSpec)
                && containerSpec.ValueKind == JsonValueKind.Object
                && containerSpec.TryGetProperty("Image", out var taskImage)
                && taskImage.ValueKind == JsonValueKind.String)
            {
                return taskImage.GetString();
            }

            if (value.TryGetProperty("Config", out var config)
                && config.ValueKind == JsonValueKind.Object
                && config.TryGetProperty("Image", out var containerImage)
                && containerImage.ValueKind == JsonValueKind.String)
            {
                return containerImage.GetString();
            }

            foreach (var property in value.EnumerateObject())
            {
                var image = FindDockerImage(property.Value);
                if (!string.IsNullOrWhiteSpace(image))
                {
                    return image;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var image = FindDockerImage(item);
                if (!string.IsNullOrWhiteSpace(image))
                {
                    return image;
                }
            }
        }

        return null;
    }
}
