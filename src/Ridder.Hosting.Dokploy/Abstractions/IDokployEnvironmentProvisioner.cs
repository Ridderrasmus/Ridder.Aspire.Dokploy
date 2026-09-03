using Aspire.Hosting.Pipelines;

namespace Ridder.Hosting.Dokploy.Abstractions;

#pragma warning disable ASPIREPIPELINES001
internal interface IDokployEnvironmentProvisioner
{
    Task PrepareRegistryAsync(DokployProjectEnvironmentResource resource, PipelineStepContext context);
    Task ProvisionApplicationsAsync(DokployProjectEnvironmentResource resource, PipelineStepContext context);
}
#pragma warning restore ASPIREPIPELINES001
