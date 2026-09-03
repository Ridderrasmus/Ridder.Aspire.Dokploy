using Aspire.Hosting.ApplicationModel;

namespace Ridder.Hosting.Dokploy.Annotations;

internal sealed class DokployRegistryAccessAnnotation : IResourceAnnotation
{
    public DokployRegistryAccessAnnotation(string containerRegistryUrl)
    {
        ContainerRegistryUrl = containerRegistryUrl ?? string.Empty;
    }

    public string ContainerRegistryUrl { get; }
}
