using Aspire.Hosting.ApplicationModel;
using Ridder.Aspire.Hosting.Dokploy.Models;

namespace Ridder.Aspire.Hosting.Dokploy.Abstractions;

internal interface IDokployRegistryAnnotation : IResourceAnnotation
{
    DokployRegistryMode Mode { get; }
    string RegistryType { get; }
    Task<DokployResolvedRegistrySettings> ResolveAsync(CancellationToken cancellationToken);
}
