using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Ridder.Hosting.Dokploy.Abstractions;
using Ridder.Hosting.Dokploy.Models;

namespace Ridder.Hosting.Dokploy;

/// <summary>
/// Represents a Dokploy environment within an Aspire application model.
/// </summary>
/// <remarks>
/// This resource also acts as an <see cref="IContainerRegistry"/> so compute resources can target the registry prepared for the Dokploy environment.
/// </remarks>
[AspireExport]
public class DokployProjectEnvironmentResource : Resource, IContainerRegistry
{
    private readonly string _name;

    internal DokployProjectEnvironmentResource(string name) : base(name)
    {
        _name = name;

        Annotations.Add(new ManifestPublishingCallbackAnnotation(context => DokployManifestPublishing.WriteEnvironmentManifest(context, this)));

#pragma warning disable ASPIREPIPELINES001
        Annotations.Add(new PipelineStepAnnotation(ctx =>
        {
            return
            [
                new PipelineStep()
                {
                    Name = $"prepare-registry-{name}",
                    Action = async context =>
                    {
                        var provisioner = context.Services.GetRequiredService<IDokployEnvironmentProvisioner>();
                        await provisioner.PrepareRegistryAsync(this, context);
                    },
                    RequiredBySteps = [WellKnownPipelineSteps.Push, WellKnownPipelineSteps.Deploy],
                    DependsOnSteps = [WellKnownPipelineSteps.DeployPrereq]
                },
                new PipelineStep()
                {
                    Name = $"provision-apps-{name}",
                    Action = async context =>
                    {
                        var provisioner = context.Services.GetRequiredService<IDokployEnvironmentProvisioner>();
                        await provisioner.ProvisionApplicationsAsync(this, context);
                    },
                    DependsOnSteps = [WellKnownPipelineSteps.Push, $"prepare-registry-{name}"],
                    RequiredBySteps = [WellKnownPipelineSteps.Deploy]
                }
            ];
        }));
#pragma warning restore ASPIREPIPELINES001
    }

    internal DokployRegistryMode? RegistryMode => this.TryGetDokployRegistryAnnotation(out var annotation) ? annotation!.Mode : null;
    internal string? RegistryType => this.TryGetDokployRegistryAnnotation(out var annotation) ? annotation!.RegistryType : null;
    internal string? ApiUrlParameterName => this.GetDokployEnvironmentConnection()?.ApiUrlParameter?.Name;
    internal string? ApiKeyParameterName => this.GetDokployEnvironmentConnection()?.ApiKeyParameter?.Name;
    internal bool HasRegistryConfiguration => this.TryGetDokployRegistryAnnotation(out _);

    /// <summary>
    /// Gets the registry endpoint that Aspire uses when publishing images for this Dokploy environment.
    /// </summary>
    public ReferenceExpression Endpoint => ReferenceExpression.Create($"{this.GetDokployRegistryAccess()?.ContainerRegistryUrl ?? string.Empty}");

    ReferenceExpression IContainerRegistry.Name => ReferenceExpression.Create($"{_name}-registry");

    internal string GetProjectName() => $"{_name}-project";

    internal async Task<DokployResolvedRegistrySettings> ResolveRegistrySettingsAsync(CancellationToken cancellationToken)
    {
        if (!this.TryGetDokployRegistryAnnotation(out var annotation))
        {
            throw new InvalidOperationException($"Dokploy environment '{_name}' does not have a registry configuration. Call WithHostedRegistry(...) or WithSelfHostedRegistry(...).");
        }

        return await annotation!.ResolveAsync(cancellationToken);
    }

    internal async Task<string> ResolveApiUrlAsync(CancellationToken cancellationToken)
    {
        var connection = this.GetDokployEnvironmentConnection()
            ?? throw new InvalidOperationException($"Dokploy environment '{_name}' does not have an API connection annotation.");

        var apiUrl = await connection.ResolveApiUrlAsync(_name, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            throw new InvalidOperationException($"Dokploy API URL for project {_name} is not set.");
        }

        return apiUrl;
    }

    internal async Task<string> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        var connection = this.GetDokployEnvironmentConnection()
            ?? throw new InvalidOperationException($"Dokploy environment '{_name}' does not have an API connection annotation.");

        var apiKey = await connection.ResolveApiKeyAsync(_name, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"API key for project {_name} is not set.");
        }

        return apiKey;
    }
}
