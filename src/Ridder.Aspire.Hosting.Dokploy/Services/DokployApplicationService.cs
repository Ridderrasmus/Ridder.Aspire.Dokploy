using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Ridder.Aspire.Hosting.Dokploy.Models;
using Ridder.Aspire.Hosting.Dokploy.Utilities;
using System.Globalization;
using System.Text.Json;

namespace Ridder.Aspire.Hosting.Dokploy.Services;

internal sealed class DokployApplicationService
{
    private static readonly TimeSpan DeploymentVerificationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DeploymentVerificationInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeploymentStabilityPeriod = TimeSpan.FromSeconds(10);

    private readonly DokployApiClient _client;
    private readonly DokployProjectService _projectService;

    internal DokployApplicationService(DokployApiClient client, DokployProjectService projectService)
    {
        _client = client;
        _projectService = projectService;
    }

    internal async Task<DokployApplication> GetOrCreateApplication(string appName, string projectName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            throw new ArgumentException("Application name must be provided.", nameof(appName));
        }

        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new ArgumentException("Project name must be provided.", nameof(projectName));
        }

        var project = await _projectService.GetProjectOrCreateAsync(projectName);
        if (string.IsNullOrWhiteSpace(project.Id))
        {
            throw new InvalidOperationException($"Project '{projectName}' does not have a projectId.");
        }

        using var projectResponse = await _client.Http.GetAsync($"api/project.one?projectId={Uri.EscapeDataString(project.Id)}");
        projectResponse.EnsureSuccessStatusCode();

        var refreshedProject = await DokployResponseReaders.ReadProjectFromResponseAsync(projectResponse)
            ?? throw new InvalidOperationException($"Could not parse project.one response for project '{projectName}'.");

        var targetEnvironment = refreshedProject.Environments
            .FirstOrDefault(e => string.Equals(e.Name, "production", StringComparison.OrdinalIgnoreCase))
            ?? refreshedProject.Environments.FirstOrDefault();

        if (targetEnvironment is null || string.IsNullOrWhiteSpace(targetEnvironment.Id))
        {
            throw new InvalidOperationException($"Project '{refreshedProject.Name}' has no usable environment for application deployment.");
        }

        var existing = targetEnvironment.Applications.FirstOrDefault(a =>
            string.Equals(a.Name, appName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.AppName, appName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            _client.Logger.LogInformation("Application {AppName} already exists in project {ProjectName}.", appName, refreshedProject.Name);
            return existing;
        }

        var createBody = JsonSerializer.Serialize(new
        {
            name = appName,
            appName = appName,
            description = "Created by Aspire deploy pipeline.",
            environmentId = targetEnvironment.Id
        }, DokployApiClient.JsonOptions);

        using var createResponse = await _client.Http.PostAsync("api/application.create", DokployApiClient.CreateJsonContent(createBody));
        createResponse.EnsureSuccessStatusCode();

        var created = await DokployResponseReaders.ReadApplicationFromResponseAsync(createResponse);
        _client.Logger.LogInformation("Created application {AppName} in project {ProjectName}.", appName, refreshedProject.Name);

        using var verifyResponse = await _client.Http.GetAsync($"api/project.one?projectId={Uri.EscapeDataString(project.Id)}");
        verifyResponse.EnsureSuccessStatusCode();

        var verifiedProject = await DokployResponseReaders.ReadProjectFromResponseAsync(verifyResponse)
            ?? throw new InvalidOperationException($"Application create succeeded but project '{projectName}' could not be reloaded.");

        var verifiedEnvironment = verifiedProject.Environments
            .FirstOrDefault(e => string.Equals(e.Name, targetEnvironment.Name, StringComparison.OrdinalIgnoreCase));

        var verified = verifiedEnvironment?.Applications.FirstOrDefault(a =>
            string.Equals(a.Name, appName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.AppName, appName, StringComparison.OrdinalIgnoreCase));

        if (verified is null && created is not null)
        {
            verified = verifiedEnvironment?.Applications.FirstOrDefault(a =>
                string.Equals(a.Name, created.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.AppName, created.AppName, StringComparison.OrdinalIgnoreCase));
        }

        if (verified is null)
        {
            throw new InvalidOperationException($"Application '{appName}' was not found after create in project '{projectName}'.");
        }

        return verified;
    }

    internal async Task ConfigureApplicationAsync(
        DokployApplication application,
        string projectName,
        IComputeResource rsc,
        DistributedApplicationExecutionContext executionContext,
        IReadOnlyDictionary<string, string> applicationHostsByResource,
        CancellationToken cancellationToken)
    {
        rsc.TryGetDokployPublishAnnotation(out var publishAnnotation);

        if (string.IsNullOrWhiteSpace(application.Id))
        {
            throw new InvalidOperationException($"Application '{rsc.Name}' has no applicationId, so provider cannot be configured.");
        }

        var registryUrl = _client.RegistrySettings.RegistryUrl;

        var dockerImage = await ResolveDeploymentImageAsync(rsc, cancellationToken);

        var saveDockerProviderBody = JsonSerializer.Serialize(new
        {
            applicationId = application.Id,
            registryUrl,
            dockerImage,
            username = _client.RegistrySettings.Username,
            password = _client.RegistrySettings.Password
        }, DokployApiClient.JsonOptions);

        using var saveDockerProviderResponse = await _client.Http.PostAsync("api/application.saveDockerProvider", DokployApiClient.CreateJsonContent(saveDockerProviderBody));
        saveDockerProviderResponse.EnsureSuccessStatusCode();
        _client.Logger.LogInformation("Saved docker provider for application {AppName}.", rsc.Name);

        if (publishAnnotation?.Options.ConfigureEnvironmentVariables ?? true)
        {
            await SaveApplicationEnvironmentAsync(application, projectName, rsc, executionContext, applicationHostsByResource, cancellationToken);
        }

        if (publishAnnotation?.Options.ConfigureMounts ?? true)
        {
            await EnsureApplicationMountsAsync(application, rsc);
        }

        await ConfigureStatefulRolloutPolicyAsync(application, rsc, cancellationToken);

        if (publishAnnotation?.Options.CreateDomainsForExternalEndpoints ?? true)
        {
            await EnsureApplicationDomainsAsync(application, rsc, publishAnnotation?.Options.Domains ?? []);
        }

        if (publishAnnotation?.Options.RunOnce == true)
        {
            await ConfigureRunOncePolicyAsync(application, rsc, cancellationToken);
        }
    }

    internal async Task ConfigureStatefulRolloutPolicyAsync(
        DokployApplication application,
        IComputeResource resource,
        CancellationToken cancellationToken)
    {
        if (!resource.TryGetContainerMounts(out var containerMounts)
            || !containerMounts.Any(mount => mount.Type == ContainerMountType.Volume))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(application.Id))
        {
            throw new InvalidOperationException(
                $"Application '{resource.Name}' has no applicationId, so its stateful rollout policy cannot be configured.");
        }

        var body = JsonSerializer.Serialize(new
        {
            applicationId = application.Id,
            updateConfigSwarm = new
            {
                Parallelism = 1,
                FailureAction = "rollback",
                Monitor = 5_000_000_000L,
                MaxFailureRatio = 0,
                Order = "stop-first"
            },
            rollbackConfigSwarm = new
            {
                Parallelism = 1,
                FailureAction = "pause",
                Monitor = 5_000_000_000L,
                MaxFailureRatio = 0,
                Order = "stop-first"
            }
        }, DokployApiClient.JsonOptions);

        using var response = await _client.Http.PostAsync(
            "api/application.update",
            DokployApiClient.CreateJsonContent(body),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        _client.Logger.LogInformation(
            "Configured application {AppName} to stop its existing task before update or rollback because it uses a persistent volume.",
            resource.Name);
    }

    private async Task ConfigureRunOncePolicyAsync(
        DokployApplication application,
        IComputeResource resource,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            applicationId = application.Id,
            restartPolicySwarm = new
            {
                Condition = "none"
            },
            updateConfigSwarm = new
            {
                Parallelism = 1,
                FailureAction = "pause",
                Monitor = 5_000_000_000L,
                MaxFailureRatio = 0,
                Order = "stop-first"
            }
        }, DokployApiClient.JsonOptions);

        using var response = await _client.Http.PostAsync(
            "api/application.update",
            DokployApiClient.CreateJsonContent(body),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        _client.Logger.LogInformation(
            "Configured application {AppName} to run once without Swarm restarts or automatic rollback.",
            resource.Name);
    }

    internal async Task<HashSet<string>> DeployApplicationAsync(
        DokployApplication application,
        IComputeResource rsc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(application.Id))
        {
            throw new InvalidOperationException($"Application '{rsc.Name}' has no applicationId, so deployment cannot be triggered.");
        }

        var existingTaskIds = (await GetServiceTasksAsync(application, cancellationToken))
            .Select(task => task.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deployBody = JsonSerializer.Serialize(new
        {
            applicationId = application.Id,
            title = $"Aspire deployment for {rsc.Name}",
            description = $"Automated deploy for resource '{rsc.Name}' in project '{_client.Env.ApplicationName}'."
        }, DokployApiClient.JsonOptions);

        using var deployResponse = await _client.Http.PostAsync(
            "api/application.deploy",
            DokployApiClient.CreateJsonContent(deployBody),
            cancellationToken);
        deployResponse.EnsureSuccessStatusCode();
        _client.Logger.LogInformation("Triggered application deploy for {AppName}.", rsc.Name);
        return existingTaskIds;
    }

    internal async Task VerifyApplicationDeploymentAsync(
        DokployApplication application,
        IComputeResource resource,
        IReadOnlySet<string> existingTaskIds,
        CancellationToken cancellationToken)
    {
        var expectedImage = await ResolveDeploymentImageAsync(resource, cancellationToken);
        var appName = GetDokployApplicationName(application, resource);
        var deadline = DateTimeOffset.UtcNow + DeploymentVerificationTimeout;
        var observedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runningSinceByTask = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = await GetServiceTasksAsync(application, cancellationToken);

            foreach (var task in tasks.Where(task => !existingTaskIds.Contains(task.Id)))
            {
                var image = await GetServiceTaskImageAsync(task.Id, cancellationToken);
                if (string.IsNullOrWhiteSpace(image))
                {
                    continue;
                }

                observedImages.Add(image);
                if (!ImageReferencesMatch(image, expectedImage))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(task.Error))
                {
                    observedFailures.Add($"{task.Id}: {task.Error}");
                    runningSinceByTask.Remove(task.Id);
                    continue;
                }

                if (task.CurrentState.StartsWith("Complete ", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(task.CurrentState, "Complete", StringComparison.OrdinalIgnoreCase))
                {
                    _client.Logger.LogInformation(
                        "Verified Dokploy rollout for {AppName}: new one-shot service task {TaskId} completed using {Image}.",
                        resource.Name,
                        task.Id,
                        expectedImage);
                    return;
                }

                var isRunning = string.Equals(task.State, "running", StringComparison.OrdinalIgnoreCase)
                    && (task.CurrentState.StartsWith("Running ", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(task.CurrentState, "Running", StringComparison.OrdinalIgnoreCase));
                if (!isRunning)
                {
                    runningSinceByTask.Remove(task.Id);
                    continue;
                }

                if (!runningSinceByTask.TryGetValue(task.Id, out var runningSince))
                {
                    runningSinceByTask[task.Id] = DateTimeOffset.UtcNow;
                    continue;
                }

                if (DateTimeOffset.UtcNow - runningSince < DeploymentStabilityPeriod)
                {
                    continue;
                }

                _client.Logger.LogInformation(
                    "Verified Dokploy rollout for {AppName}: new service task {TaskId} remained running with {Image} for {StabilitySeconds} seconds.",
                    resource.Name,
                    task.Id,
                    expectedImage,
                    DeploymentStabilityPeriod.TotalSeconds);
                return;
            }

            await Task.Delay(DeploymentVerificationInterval, cancellationToken);
        }

        var observed = observedImages.Count == 0
            ? "no new service task images"
            : string.Join(", ", observedImages.Order(StringComparer.OrdinalIgnoreCase));
        var failures = observedFailures.Count == 0
            ? string.Empty
            : $" Task failures: {string.Join("; ", observedFailures.Order(StringComparer.OrdinalIgnoreCase))}.";
        throw new InvalidOperationException(
            $"Dokploy accepted the deployment for '{appName}', but no new service task using expected image '{expectedImage}' remained healthy within {DeploymentVerificationTimeout.TotalMinutes:0} minutes. Observed {observed}.{failures}");
    }

    private static bool HasDockerfileBuildAnnotation(IResource resource)
    {
        return resource.Annotations.Any(annotation =>
            string.Equals(annotation.GetType().Name, "DockerfileBuildAnnotation", StringComparison.Ordinal));
    }

    private static async Task<string> ResolveDockerImageAsync(IComputeResource resource, CancellationToken cancellationToken)
    {
        if (HasDockerfileBuildAnnotation(resource) || resource is ProjectResource)
        {
            var imageReference = new ContainerImageReference(resource);
            var value = await ((IValueProvider)imageReference).GetValueAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        else if (resource.TryGetContainerImageName(out var imageName) && !string.IsNullOrWhiteSpace(imageName))
        {
            return imageName;
        }

        throw new InvalidOperationException($"Compute resource '{resource.Name}' does not have Docker image information in annotations or properties.");
    }

    private async Task<string> ResolveDeploymentImageAsync(
        IComputeResource resource,
        CancellationToken cancellationToken)
    {
        var image = await ResolveDockerImageAsync(resource, cancellationToken);
        var digestReference = await ContainerRegistryDigestResolver.ResolveAsync(
            image,
            _client.RegistrySettings,
            cancellationToken);

        _client.Logger.LogInformation(
            "Resolved immutable deployment image for {ResourceName}: {ImageReference}.",
            resource.Name,
            digestReference);
        return digestReference;
    }

    private async Task<List<DokployServiceTask>> GetServiceTasksAsync(
        DokployApplication application,
        CancellationToken cancellationToken)
    {
        var appName = GetDokployApplicationName(application);
        using var response = await _client.Http.GetAsync(
            $"api/docker.getServiceContainersByAppName?appName={Uri.EscapeDataString(appName)}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await DokployResponseReaders.ReadServiceTasksFromResponseAsync(response, cancellationToken);
    }

    private async Task<string?> GetServiceTaskImageAsync(string taskId, CancellationToken cancellationToken)
    {
        using var response = await _client.Http.GetAsync(
            $"api/docker.getConfig?containerId={Uri.EscapeDataString(taskId)}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await DokployResponseReaders.ReadDockerImageFromResponseAsync(response, cancellationToken);
    }

    private static string GetDokployApplicationName(DokployApplication application, IComputeResource? resource = null)
    {
        var appName = string.IsNullOrWhiteSpace(application.AppName) ? application.Name : application.AppName;
        if (!string.IsNullOrWhiteSpace(appName))
        {
            return appName;
        }

        throw new InvalidOperationException($"Application '{resource?.Name ?? "unknown"}' has no Dokploy service name.");
    }

    internal static bool ImageReferencesMatch(string actual, string expected)
    {
        static string Normalize(string image)
        {
            var normalized = image.Trim();

            foreach (var prefix in new[] { "docker.io/", "index.docker.io/", "registry-1.docker.io/" })
            {
                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized[prefix.Length..];
                    break;
                }
            }

            return normalized;
        }

        return string.Equals(Normalize(actual), Normalize(expected), StringComparison.OrdinalIgnoreCase);
    }

    private async Task SaveApplicationEnvironmentAsync(
        DokployApplication application,
        string projectName,
        IComputeResource resource,
        DistributedApplicationExecutionContext executionContext,
        IReadOnlyDictionary<string, string> applicationHostsByResource,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(application.Id))
        {
            throw new InvalidOperationException("Application id is required to save environment variables.");
        }

        var environmentVariables = await ResolveResourceEnvironmentAsync(resource, projectName, executionContext, applicationHostsByResource, cancellationToken);
        if (environmentVariables.Count == 0)
        {
            _client.Logger.LogInformation("No Aspire environment variables found for resource {ResourceName}.", resource.Name);
            return;
        }

        var envPayload = string.Join(
            '\n',
            environmentVariables
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key}={EscapeEnvValue(kv.Value)}"));

        var envKeys = environmentVariables.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();

        _client.Logger.LogInformation("Preparing to save {Count} environment variable(s) for resource {ResourceName}. Keys: {EnvironmentKeys}", envKeys.Length, resource.Name, string.Join(", ", envKeys));
        _client.Logger.LogInformation("Environment payload size for resource {ResourceName}: {PayloadLength} characters.", resource.Name, envPayload.Length);

        var saveEnvironmentBody = JsonSerializer.Serialize(new
        {
            applicationId = application.Id,
            env = envPayload,
            createEnvFile = true,
            buildArgs = "",
            buildSecrets = ""
        }, DokployApiClient.JsonOptions);

        using var saveEnvironmentResponse = await _client.Http.PostAsync("api/application.saveEnvironment", DokployApiClient.CreateJsonContent(saveEnvironmentBody));
        saveEnvironmentResponse.EnsureSuccessStatusCode();
        _client.Logger.LogInformation("Saved {Count} environment variable(s) for application {AppName}.", environmentVariables.Count, resource.Name);
    }

    internal async Task EnsureApplicationMountsAsync(DokployApplication application, IComputeResource resource)
    {
        if (string.IsNullOrWhiteSpace(application.Id))
        {
            throw new InvalidOperationException("Application id is required to verify mounts.");
        }

        if (!resource.TryGetContainerMounts(out var containerMounts))
        {
            _client.Logger.LogInformation("Application {AppName} has no Aspire container mounts. Skipping mount creation.", resource.Name);
            return;
        }

        var desiredMounts = containerMounts
            .Select(mount => ToDesiredMount(resource, mount))
            .DistinctBy(GetMountIdentity, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (desiredMounts.Count == 0)
        {
            _client.Logger.LogInformation("Application {AppName} has no supported Aspire container mounts after normalization. Skipping mount creation.", resource.Name);
            return;
        }

        using var existingMountsResponse = await _client.Http.GetAsync(
            $"api/mounts.listByServiceId?serviceId={Uri.EscapeDataString(application.Id)}&serviceType=application");
        existingMountsResponse.EnsureSuccessStatusCode();

        var existingMounts = await DokployResponseReaders.ReadMountsFromResponseAsync(
            existingMountsResponse,
            _client.Logger,
            "mounts.listByServiceId");
        var unmatchedExistingMounts = new List<DokployMount>(existingMounts);

        foreach (var desiredMount in desiredMounts)
        {
            var targetMatches = unmatchedExistingMounts
                .Where(existing => MountLocationMatches(existing, desiredMount))
                .ToList();
            var exactMatch = targetMatches.FirstOrDefault(existing => MountIdentityMatches(existing, desiredMount));

            if (exactMatch is not null)
            {
                unmatchedExistingMounts.Remove(exactMatch);
                var redundantMatches = targetMatches
                    .Where(existing => !ReferenceEquals(existing, exactMatch))
                    .ToList();
                unmatchedExistingMounts.RemoveAll(redundantMatches.Contains);
                await RemoveRedundantMountsAsync(
                    application,
                    resource,
                    redundantMatches);
                _client.Logger.LogInformation("Mount {MountPath} for application {AppName} already exists as {MountType}.", desiredMount.MountPath, resource.Name, desiredMount.Type);
                continue;
            }

            var targetMatch = targetMatches.FirstOrDefault();
            if (targetMatch is not null)
            {
                unmatchedExistingMounts.Remove(targetMatch);

                if (string.IsNullOrWhiteSpace(targetMatch.Id))
                {
                    throw new InvalidOperationException($"Mount '{targetMatch.MountPath}' exists for application '{resource.Name}' but no mountId was returned.");
                }

                var updateBody = JsonSerializer.Serialize(new
                {
                    mountId = targetMatch.Id,
                    type = desiredMount.Type,
                    hostPath = desiredMount.HostPath,
                    volumeName = desiredMount.VolumeName,
                    mountPath = desiredMount.MountPath,
                    serviceType = "application",
                    applicationId = application.Id
                }, DokployApiClient.JsonOptions);

                using var updateResponse = await _client.Http.PostAsync("api/mounts.update", DokployApiClient.CreateJsonContent(updateBody));
                updateResponse.EnsureSuccessStatusCode();

                var redundantMatches = targetMatches
                    .Where(existing => !ReferenceEquals(existing, targetMatch))
                    .ToList();
                unmatchedExistingMounts.RemoveAll(redundantMatches.Contains);
                await RemoveRedundantMountsAsync(
                    application,
                    resource,
                    redundantMatches);
                _client.Logger.LogInformation("Updated mount {MountPath} for application {AppName} to {MountType}.", desiredMount.MountPath, resource.Name, desiredMount.Type);
                continue;
            }

            var createBody = JsonSerializer.Serialize(new
            {
                type = desiredMount.Type,
                hostPath = desiredMount.HostPath,
                volumeName = desiredMount.VolumeName,
                mountPath = desiredMount.MountPath,
                serviceType = "application",
                serviceId = application.Id
            }, DokployApiClient.JsonOptions);

            using var createResponse = await _client.Http.PostAsync("api/mounts.create", DokployApiClient.CreateJsonContent(createBody));
            createResponse.EnsureSuccessStatusCode();
            _client.Logger.LogInformation("Created mount {MountPath} for application {AppName} as {MountType}.", desiredMount.MountPath, resource.Name, desiredMount.Type);
        }

        if (unmatchedExistingMounts.Count > 0)
        {
            _client.Logger.LogInformation("Application {AppName} has {ExtraCount} extra existing Dokploy mount(s) beyond the {ExpectedCount} Aspire mount(s); leaving them unchanged.", resource.Name, unmatchedExistingMounts.Count, desiredMounts.Count);
        }
    }

    private static DokployMountSpec ToDesiredMount(IComputeResource resource, ContainerMountAnnotation mount)
    {
        if (mount.IsReadOnly)
        {
            throw new InvalidOperationException($"Resource '{resource.Name}' declares read-only mount '{mount.Target}', but Dokploy mount APIs do not expose read-only semantics.");
        }

        if (string.IsNullOrWhiteSpace(mount.Target))
        {
            throw new InvalidOperationException($"Resource '{resource.Name}' has a container mount with no target path.");
        }

        return mount.Type switch
        {
            ContainerMountType.Volume when !string.IsNullOrWhiteSpace(mount.Source) => new DokployMountSpec("volume", mount.Target, null, mount.Source),
            ContainerMountType.BindMount when !string.IsNullOrWhiteSpace(mount.Source) => new DokployMountSpec("bind", mount.Target, mount.Source, null),
            ContainerMountType.Volume => throw new InvalidOperationException($"Resource '{resource.Name}' has a volume mount for '{mount.Target}' without a volume name."),
            ContainerMountType.BindMount => throw new InvalidOperationException($"Resource '{resource.Name}' has a bind mount for '{mount.Target}' without a host path."),
            _ => throw new InvalidOperationException($"Resource '{resource.Name}' uses unsupported container mount type '{mount.Type}'.")
        };
    }

    private static string GetMountIdentity(DokployMountSpec mount)
    {
        return DokployMountReconciler.GetMountIdentity(mount.Type, mount.MountPath, mount.HostPath, mount.VolumeName);
    }

    private static bool MountIdentityMatches(DokployMount existingMount, DokployMountSpec desiredMount)
    {
        return DokployMountReconciler.MountIdentityMatches(existingMount, desiredMount.Type, desiredMount.MountPath, desiredMount.HostPath, desiredMount.VolumeName);
    }

    private static bool MountLocationMatches(DokployMount existingMount, DokployMountSpec desiredMount)
    {
        return DokployMountReconciler.MountLocationMatches(existingMount, desiredMount.MountPath);
    }

    private async Task<Dictionary<string, string>> ResolveResourceEnvironmentAsync(
        IComputeResource resource,
        string projectName,
        DistributedApplicationExecutionContext executionContext,
        IReadOnlyDictionary<string, string> applicationHostsByResource,
        CancellationToken cancellationToken)
    {
        var environmentVariables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var environmentCallbacks))
        {
            var callbackContext = new EnvironmentCallbackContext(executionContext, resource, environmentVariables, cancellationToken: cancellationToken);

            foreach (var callback in environmentCallbacks)
            {
                await callback.Callback(callbackContext).ConfigureAwait(false);
            }
        }

        var materialized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in environmentVariables)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
            {
                continue;
            }

            var value = await MaterializeEnvironmentValueAsync(kv.Value, projectName, applicationHostsByResource, cancellationToken).ConfigureAwait(false);
            materialized[kv.Key] = value;
        }

        var normalized = NormalizeDokployEnvironmentVariables(materialized, applicationHostsByResource);

        if (normalized.Count != materialized.Count)
        {
            _client.Logger.LogInformation(
                "Removed {RemovedCount} invalid internal HTTPS environment variable(s) for resource {ResourceName} because Dokploy service-to-service traffic is published as HTTP.",
                materialized.Count - normalized.Count,
                resource.Name);
        }

        return normalized;
    }

    internal static Dictionary<string, string> NormalizeDokployEnvironmentVariables(
        IReadOnlyDictionary<string, string> environmentVariables,
        IReadOnlyDictionary<string, string> applicationHostsByResource)
    {
        var internalHosts = applicationHostsByResource.Values
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (internalHosts.Count == 0)
        {
            return new Dictionary<string, string>(environmentVariables, StringComparer.OrdinalIgnoreCase);
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var environmentVariable in environmentVariables)
        {
            if (ShouldSuppressInternalHttpsEnvironmentVariable(environmentVariable.Key, environmentVariable.Value, internalHosts))
            {
                continue;
            }

            normalized[environmentVariable.Key] = environmentVariable.Value;
        }

        return normalized;
    }

    private static bool ShouldSuppressInternalHttpsEnvironmentVariable(string key, string value, HashSet<string> internalHosts)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !internalHosts.Contains(uri.Host))
        {
            return false;
        }

        return key.Contains("__https__", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_HTTPS", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> MaterializeEnvironmentValueAsync(
        object? value,
        string projectName,
        IReadOnlyDictionary<string, string> applicationHostsByResource,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            switch (value)
            {
                case null:
                    return string.Empty;
                case string s:
                    return s;
                case bool b:
                    return b ? "true" : "false";
                case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                case EndpointReference endpointReference:
                    return ResolveEndpointValue(endpointReference, EndpointProperty.Url, projectName, applicationHostsByResource);
                case EndpointReferenceExpression endpointReferenceExpression:
                    return ResolveEndpointValue(endpointReferenceExpression.Endpoint, endpointReferenceExpression.Property, projectName, applicationHostsByResource);
                case ParameterResource parameter:
                    return await parameter.GetValueAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
                case ConnectionStringReference connectionStringReference:
                    value = connectionStringReference.Resource.ConnectionStringExpression;
                    continue;
                case IResourceWithConnectionString resourceWithConnectionString:
                    value = resourceWithConnectionString.ConnectionStringExpression;
                    continue;
                case ReferenceExpression referenceExpression:
                    return await FormatReferenceExpressionAsync(referenceExpression, projectName, applicationHostsByResource, cancellationToken).ConfigureAwait(false);
                case IValueProvider valueProvider:
                    return await valueProvider.GetValueAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
                case IManifestExpressionProvider manifestExpressionProvider:
                    return manifestExpressionProvider.ValueExpression;
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
    }

    private async Task<string> FormatReferenceExpressionAsync(
        ReferenceExpression expression,
        string projectName,
        IReadOnlyDictionary<string, string> applicationHostsByResource,
        CancellationToken cancellationToken)
    {
        if (expression is { Format: "{0}", ValueProviders.Count: 1 })
        {
            return await MaterializeEnvironmentValueAsync(expression.ValueProviders[0], projectName, applicationHostsByResource, cancellationToken).ConfigureAwait(false);
        }

        var args = new object[expression.ValueProviders.Count];
        for (var i = 0; i < expression.ValueProviders.Count; i++)
        {
            args[i] = await MaterializeEnvironmentValueAsync(expression.ValueProviders[i], projectName, applicationHostsByResource, cancellationToken).ConfigureAwait(false);
        }

        return string.Format(CultureInfo.InvariantCulture, expression.Format, args);
    }

    private static string ResolveEndpointValue(
        EndpointReference endpointReference,
        EndpointProperty property,
        string projectName,
        IReadOnlyDictionary<string, string> applicationHostsByResource)
    {
        var referencedResource = endpointReference.Resource;
        var host = GetApplicationServiceName(projectName, referencedResource?.Name ?? "unknown-service", applicationHostsByResource).ToLowerInvariant();

        if (referencedResource is null)
        {
            return property switch
            {
                EndpointProperty.Host => host,
                EndpointProperty.IPV4Host => host,
                EndpointProperty.Port => "8080",
                EndpointProperty.TargetPort => "8080",
                EndpointProperty.HostAndPort => $"{host}:8080",
                EndpointProperty.Scheme => "http",
                _ => $"http://{host}:8080"
            };
        }

        var resolved = referencedResource.ResolveEndpoints().FirstOrDefault(e => string.Equals(e.Endpoint?.Name, endpointReference.EndpointName, StringComparison.OrdinalIgnoreCase));

        var scheme = "http";
        var port = 8080;

        if (resolved?.Endpoint is { } endpoint)
        {
            scheme = endpoint.UriScheme ?? "http";
            port = resolved.TargetPort.Value ?? resolved.ExposedPort.Value ?? 8080;
        }

        return property switch
        {
            EndpointProperty.Url => $"{scheme}://{host}:{port}",
            EndpointProperty.Host => host,
            EndpointProperty.IPV4Host => host,
            EndpointProperty.Port => port.ToString(CultureInfo.InvariantCulture),
            EndpointProperty.TargetPort => port.ToString(CultureInfo.InvariantCulture),
            EndpointProperty.HostAndPort => $"{host}:{port}",
            EndpointProperty.Scheme => scheme,
            _ => $"{scheme}://{host}:{port}"
        };
    }

    private static string GetApplicationServiceName(
        string projectName,
        string resourceName,
        IReadOnlyDictionary<string, string>? applicationHostsByResource = null)
    {
        if (applicationHostsByResource is not null
            && applicationHostsByResource.TryGetValue(resourceName, out var applicationHost)
            && !string.IsNullOrWhiteSpace(applicationHost))
        {
            return applicationHost;
        }

        const string projectSuffix = "-project";
        var prefix = projectName.EndsWith(projectSuffix, StringComparison.OrdinalIgnoreCase)
            ? projectName[..^projectSuffix.Length]
            : projectName;

        return $"{prefix}-app-{resourceName}";
    }

    private static string EscapeEnvValue(string value)
    {
        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        if (normalized.Length == 0)
        {
            return normalized;
        }

        var needsQuotes = normalized.Any(char.IsWhiteSpace)
            || normalized.Contains('#', StringComparison.Ordinal)
            || normalized.Contains('"', StringComparison.Ordinal)
            || normalized.Contains('=', StringComparison.Ordinal);

        if (!needsQuotes)
        {
            return normalized;
        }

        // Dokploy parses this payload with dotenv. Single quotes and backticks
        // preserve both genuine newlines and existing escape sequences verbatim.
        // Double quotes would interpret \n sequences and corrupt PEM secrets that
        // are already stored in their common JSON-escaped form.
        if (!normalized.Contains('\'', StringComparison.Ordinal))
        {
            return $"'{normalized}'";
        }

        if (!normalized.Contains('`', StringComparison.Ordinal))
        {
            return $"`{normalized}`";
        }

        // Values containing both literal quote delimiters are uncommon. Use
        // dotenv's double-quoted form as a last resort.
        var encoded = normalized
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return $"\"{encoded}\"";
    }

    private async Task EnsureApplicationDomainsAsync(
        DokployApplication application,
        IComputeResource resource,
        IReadOnlyList<DokployDomainConfiguration> configuredDomains)
    {
        if (string.IsNullOrWhiteSpace(application.Id))
        {
            throw new InvalidOperationException("Application id is required to verify domains.");
        }

        var externalEndpoints = GetExternalEndpoints(resource);
        if (externalEndpoints.Count == 0)
        {
            if (configuredDomains.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Application '{application.AppName}' configures Dokploy domains, but the Aspire resource has no external endpoints.");
            }

            _client.Logger.LogInformation("Application {AppName} has no external Aspire endpoints. Skipping domain creation.", application.AppName);
            return;
        }

        using var byAppResponse = await _client.Http.GetAsync($"api/domain.byApplicationId?applicationId={Uri.EscapeDataString(application.Id)}");
        byAppResponse.EnsureSuccessStatusCode();

        var existingDomains = await DokployResponseReaders.ReadDomainsFromResponseAsync(byAppResponse, _client.Logger, "domain.byApplicationId");
        var appNameForDomain = string.IsNullOrWhiteSpace(application.AppName) ? application.Name : application.AppName;
        if (string.IsNullOrWhiteSpace(appNameForDomain))
        {
            throw new InvalidOperationException("Application name is required to configure domains.");
        }

        List<ApplicationDomainConfig> domainsToReconcile;
        if (configuredDomains.Count == 0)
        {
            var generatedHost = await GenerateApplicationDomainAsync(appNameForDomain);
            domainsToReconcile = externalEndpoints
                .Select(endpoint => new ApplicationDomainConfig(generatedHost, endpoint))
                .ToList();
        }
        else
        {
            domainsToReconcile = new List<ApplicationDomainConfig>(configuredDomains.Count);
            foreach (var configuredDomain in configuredDomains)
            {
                var endpoint = externalEndpoints.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, configuredDomain.EndpointName, StringComparison.OrdinalIgnoreCase));

                if (endpoint is null)
                {
                    var availableEndpoints = string.Join(", ", externalEndpoints.Select(candidate => candidate.Name));
                    throw new InvalidOperationException(
                        $"Dokploy domain '{configuredDomain.Host}' references endpoint '{configuredDomain.EndpointName}', "
                        + $"but that endpoint is not external on application '{appNameForDomain}'. Available external endpoints: {availableEndpoints}.");
                }

                domainsToReconcile.Add(new ApplicationDomainConfig(configuredDomain.Host, endpoint));
            }
        }

        var unmatchedDomains = new List<DokployDomain>(existingDomains);
        var reconciliations = new List<(ApplicationDomainConfig Config, DokployDomain? Existing)>(domainsToReconcile.Count);

        foreach (var config in domainsToReconcile)
        {
            var existingDomain = unmatchedDomains
                .FirstOrDefault(domain =>
                    string.Equals(domain.Host, config.Host, StringComparison.OrdinalIgnoreCase)
                    && domain.Port == config.Endpoint.Port)
                ?? unmatchedDomains.FirstOrDefault(domain =>
                    string.Equals(domain.Host, config.Host, StringComparison.OrdinalIgnoreCase));

            if (existingDomain is not null)
            {
                unmatchedDomains.Remove(existingDomain);
            }

            reconciliations.Add((config, existingDomain));
        }

        for (var index = 0; index < reconciliations.Count; index++)
        {
            var reconciliation = reconciliations[index];
            if (reconciliation.Existing is not null)
            {
                continue;
            }

            var fallbackDomain = unmatchedDomains.FirstOrDefault(domain => domain.Port == reconciliation.Config.Endpoint.Port)
                ?? unmatchedDomains.FirstOrDefault();

            if (fallbackDomain is not null)
            {
                unmatchedDomains.Remove(fallbackDomain);
            }

            reconciliations[index] = (reconciliation.Config, fallbackDomain);
        }

        foreach (var (config, existingDomain) in reconciliations)
        {
            var endpointUrl = BuildApplicationEndpointUrl(config.Host, config.Endpoint);

            if (existingDomain is not null)
            {
                if (string.IsNullOrWhiteSpace(existingDomain.Id))
                {
                    throw new InvalidOperationException($"Application domain '{existingDomain.Host}' exists for application '{appNameForDomain}' but no domainId was returned.");
                }

                var updateBody = JsonSerializer.Serialize(new
                {
                    domainId = existingDomain.Id,
                    host = config.Host,
                    port = config.Endpoint.Port,
                    https = config.Endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase),
                    domainType = "application"
                }, DokployApiClient.JsonOptions);

                using var updateResponse = await _client.Http.PostAsync("api/domain.update", DokployApiClient.CreateJsonContent(updateBody));
                updateResponse.EnsureSuccessStatusCode();

                _client.Logger.LogInformation("Updated deployed application URL {EndpointUrl} for application {AppName} using endpoint {EndpointName}.", endpointUrl, appNameForDomain, config.Endpoint.Name);
                continue;
            }

            var createBody = JsonSerializer.Serialize(new
            {
                applicationId = application.Id,
                host = config.Host,
                port = config.Endpoint.Port,
                https = config.Endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase),
                domainType = "application"
            }, DokployApiClient.JsonOptions);

            using var createResponse = await _client.Http.PostAsync("api/domain.create", DokployApiClient.CreateJsonContent(createBody));
            createResponse.EnsureSuccessStatusCode();

            _client.Logger.LogInformation("Created deployed application URL {EndpointUrl} for application {AppName} using endpoint {EndpointName}.", endpointUrl, appNameForDomain, config.Endpoint.Name);
        }

        if (unmatchedDomains.Count > 0)
        {
            _client.Logger.LogInformation("Application {AppName} has {ExtraCount} extra existing domain(s) beyond the {ExpectedCount} external Aspire endpoint(s); leaving them unchanged.", appNameForDomain, unmatchedDomains.Count, externalEndpoints.Count);
        }
    }

    private async Task RemoveRedundantMountsAsync(
        DokployApplication application,
        IComputeResource resource,
        IEnumerable<DokployMount> redundantMounts)
    {
        foreach (var redundantMount in redundantMounts)
        {
            if (string.IsNullOrWhiteSpace(redundantMount.Id))
            {
                throw new InvalidOperationException(
                    $"Duplicate mount '{redundantMount.MountPath}' exists for application '{resource.Name}' but no mountId was returned.");
            }

            var removeBody = JsonSerializer.Serialize(new
            {
                mountId = redundantMount.Id
            }, DokployApiClient.JsonOptions);

            using var removeResponse = await _client.Http.PostAsync(
                "api/mounts.remove",
                DokployApiClient.CreateJsonContent(removeBody));
            removeResponse.EnsureSuccessStatusCode();
            _client.Logger.LogInformation(
                "Removed redundant mount record {MountId} for {MountPath} from application {AppName} ({ApplicationId}).",
                redundantMount.Id,
                redundantMount.MountPath,
                resource.Name,
                application.Id);
        }
    }

    private async Task<string> GenerateApplicationDomainAsync(string applicationName)
    {
        var generateBody = JsonSerializer.Serialize(new { appName = applicationName }, DokployApiClient.JsonOptions);

        using var generateResponse = await _client.Http.PostAsync("api/domain.generateDomain", DokployApiClient.CreateJsonContent(generateBody));
        generateResponse.EnsureSuccessStatusCode();

        return await DokployResponseReaders.ReadGeneratedHostFromResponseAsync(generateResponse, _client.Logger)
            ?? throw new InvalidOperationException($"Could not parse generated domain host for application '{applicationName}'.");
    }

    private static string BuildApplicationEndpointUrl(string host, ExternalEndpointConfig endpoint)
    {
        return $"{endpoint.Scheme}://{host}:{endpoint.Port}";
    }

    private static List<ExternalEndpointConfig> GetExternalEndpoints(IComputeResource resource)
    {
        return resource.ResolveEndpoints()
            .Where(e => e.Endpoint.IsExternal)
            .Select(e => new ExternalEndpointConfig(
                e.Endpoint.Name,
                e.Endpoint.UriScheme ?? "http",
                e.TargetPort.Value ?? e.ExposedPort.Value ?? 8080))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Scheme, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Port)
            .DistinctBy(e => $"{e.Name}|{e.Scheme}|{e.Port}")
            .ToList();
    }

    private sealed record ApplicationDomainConfig(string Host, ExternalEndpointConfig Endpoint);
    private sealed record ExternalEndpointConfig(string Name, string Scheme, int Port);
    private sealed record DokployMountSpec(string Type, string MountPath, string? HostPath, string? VolumeName);
}
