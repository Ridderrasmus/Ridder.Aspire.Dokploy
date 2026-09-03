using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ridder.Aspire.Hosting.Dokploy.Abstractions;
using Ridder.Aspire.Hosting.Dokploy.Annotations;
using Ridder.Aspire.Hosting.Dokploy.Models;

namespace Ridder.Aspire.Hosting.Dokploy.Services;

#pragma warning disable ASPIREPIPELINES001
internal sealed class DokployEnvironmentProvisioner : IDokployEnvironmentProvisioner
{
    public async Task PrepareRegistryAsync(DokployProjectEnvironmentResource resource, PipelineStepContext context)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);

        var apiKeyValue = await resource.ResolveApiKeyAsync(context.CancellationToken);
        var apiUrlValue = await resource.ResolveApiUrlAsync(context.CancellationToken);
        var resolvedRegistrySettings = await resource.ResolveRegistrySettingsAsync(context.CancellationToken);

        var maskedApiKey = apiKeyValue is { Length: >= 8 }
            ? apiKeyValue[..4] + "****" + apiKeyValue[^4..]
            : "****";
        context.Logger.LogInformation(
            "Deploying project {ProjectName} with API key {ApiKey}",
            resource.Name,
            maskedApiKey);

        using var client = new DokployApiClient(
            apiKeyValue,
            apiUrlValue,
            context.Services.GetRequiredService<IHostEnvironment>(),
            context.Logger,
            resolvedRegistrySettings);

        var projectService = new DokployProjectService(client);
        var registryService = new DokployRegistryService(client);

        var projectName = resource.GetProjectName();
        var project = await projectService.GetProjectOrCreateAsync(projectName);
        context.Logger.LogInformation("Project {ProjectName} exists.", project.Name);

        var registry = await registryService.GetOrCreateRegistryAsync(project);
        resource.ReplaceDokployRegistryAccessAnnotation(new DokployRegistryAccessAnnotation(
            string.IsNullOrWhiteSpace(registry.PushPrefix) ? registry.RegistryUrl : registry.PushPrefix));
        context.Logger.LogInformation("Registry for project {ProjectName} is ready.", project.Name);

        var resources = context.Model.GetComputeResources().OfType<IComputeResource>().GetDokployComputeResources(resource);
        if (resources.Count == 0)
        {
            context.Logger.LogInformation("No compute resources were selected for Dokploy environment {EnvironmentName}.", resource.Name);
            return;
        }

#pragma warning disable ASPIRECONTAINERRUNTIME001
        var containerRuntimeResolver = context.Services.GetRequiredService<IContainerRuntimeResolver>();
        var containerRuntime = await containerRuntimeResolver.ResolveAsync(context.CancellationToken);
#pragma warning restore ASPIRECONTAINERRUNTIME001
        await containerRuntime.LoginToRegistryAsync(
            registry.RegistryUrl,
            registry.Username ?? resolvedRegistrySettings.Username,
            registry.Password ?? resolvedRegistrySettings.Password,
            context.CancellationToken);
    }

    public async Task ProvisionApplicationsAsync(DokployProjectEnvironmentResource resource, PipelineStepContext context)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);

        var apiKeyValue = await resource.ResolveApiKeyAsync(context.CancellationToken);
        var apiUrlValue = await resource.ResolveApiUrlAsync(context.CancellationToken);
        var resolvedRegistrySettings = await resource.ResolveRegistrySettingsAsync(context.CancellationToken);
        using var client = new DokployApiClient(
            apiKeyValue,
            apiUrlValue,
            context.Services.GetRequiredService<IHostEnvironment>(),
            context.Logger,
            resolvedRegistrySettings);

        var projectService = new DokployProjectService(client);
        var applicationService = new DokployApplicationService(client, projectService);

        var resources = context.Model.GetComputeResources().OfType<IComputeResource>().GetDokployComputeResources(resource);
        if (resources.Count == 0)
        {
            context.Logger.LogInformation("No compute resources were selected for Dokploy environment {EnvironmentName}.", resource.Name);
            return;
        }

        var applications = new List<(IComputeResource Resource, DokployApplication Application)>();
        var projectName = resource.GetProjectName();

        foreach (var computeResource in resources)
        {
            var applicationName = computeResource.TryGetDokployPublishAnnotation(resource, out var annotation)
                ? annotation!.ResolveApplicationName(computeResource.Name)
                : $"{resource.Name}-app-{computeResource.Name}";

            var application = await applicationService.GetOrCreateApplication(applicationName, projectName);
            applications.Add((computeResource, application));
        }

        var applicationHostsByResource = applications.ToDictionary(
            app => app.Resource.Name,
            app => string.IsNullOrWhiteSpace(app.Application.AppName) ? app.Application.Name : app.Application.AppName,
            StringComparer.OrdinalIgnoreCase);

        foreach (var app in applications)
        {
            await applicationService.ConfigureApplicationAsync(app.Application, projectName, app.Resource, context.ExecutionContext, applicationHostsByResource, context.CancellationToken);
        }

        var applicationsByResourceName = applications.ToDictionary(
            app => app.Resource.Name,
            StringComparer.OrdinalIgnoreCase);
        var deploymentBatches = DokployDeploymentPlanner.CreateBatches(
            applications.Select(app => app.Resource).ToList());

        foreach (var deploymentBatch in deploymentBatches)
        {
            context.Logger.LogInformation(
                "Deploying and verifying Dokploy resource batch: {ResourceNames}.",
                string.Join(", ", deploymentBatch.Select(resource => resource.Name)));

            var deploymentBaselines = new List<(IComputeResource Resource, DokployApplication Application, HashSet<string> ExistingTaskIds)>();
            foreach (var resourceToDeploy in deploymentBatch)
            {
                var app = applicationsByResourceName[resourceToDeploy.Name];
                var existingTaskIds = await applicationService.DeployApplicationAsync(
                    app.Application,
                    app.Resource,
                    context.CancellationToken);
                deploymentBaselines.Add((app.Resource, app.Application, existingTaskIds));
            }

            await Task.WhenAll(deploymentBaselines.Select(app =>
                applicationService.VerifyApplicationDeploymentAsync(
                    app.Application,
                    app.Resource,
                    app.ExistingTaskIds,
                    context.CancellationToken)));
        }
    }
}
#pragma warning restore ASPIREPIPELINES001
