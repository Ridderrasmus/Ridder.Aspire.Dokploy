using Aspire.Hosting.ApplicationModel;

namespace Ridder.Hosting.Dokploy.Abstractions;

/// <summary>
/// Represents a Dokploy publishing annotation attached to an Aspire resource.
/// </summary>
internal interface IDokployPublishAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets the Dokploy environment that will provision the annotated resource.
    /// </summary>
    DokployProjectEnvironmentResource Environment { get; }
}
