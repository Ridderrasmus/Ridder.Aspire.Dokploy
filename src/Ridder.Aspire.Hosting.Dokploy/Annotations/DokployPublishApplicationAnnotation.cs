using Ridder.Aspire.Hosting.Dokploy.Abstractions;

namespace Ridder.Aspire.Hosting.Dokploy.Annotations;

/// <summary>
/// Stores Dokploy publishing intent and application-specific options for a compute resource.
/// </summary>
internal sealed class DokployPublishApplicationAnnotation : IDokployPublishAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DokployPublishApplicationAnnotation"/> class.
    /// </summary>
    /// <param name="environment">The Dokploy environment responsible for provisioning the resource.</param>
    /// <param name="options">The Dokploy application options associated with the resource.</param>
    public DokployPublishApplicationAnnotation(DokployProjectEnvironmentResource environment, DokployApplicationOptions? options = null)
    {
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        Options = (options ?? new DokployApplicationOptions()).Clone();
    }

    /// <summary>
    /// Gets the Dokploy environment responsible for provisioning the annotated resource.
    /// </summary>
    public DokployProjectEnvironmentResource Environment { get; }

    /// <summary>
    /// Gets the Dokploy application options applied to the annotated resource.
    /// </summary>
    public DokployApplicationOptions Options { get; }

    /// <summary>
    /// Resolves the Dokploy application name for a compute resource.
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <returns>The Dokploy application name to use.</returns>
    public string ResolveApplicationName(string resourceName)
    {
        if (!string.IsNullOrWhiteSpace(Options.ApplicationName))
        {
            return Options.ApplicationName;
        }

        return $"{Environment.Name}-app-{resourceName}";
    }
}
