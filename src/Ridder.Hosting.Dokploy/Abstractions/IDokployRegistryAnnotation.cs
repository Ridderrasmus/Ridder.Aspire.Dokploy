using Aspire.Hosting.ApplicationModel;
using Ridder.Hosting.Dokploy.Models;

namespace Ridder.Hosting.Dokploy.Abstractions;

internal interface IDokployRegistryAnnotation : IResourceAnnotation
{
    DokployRegistryMode Mode { get; }
    string RegistryType { get; }
    Task<DokployResolvedRegistrySettings> ResolveAsync(CancellationToken cancellationToken);
}
