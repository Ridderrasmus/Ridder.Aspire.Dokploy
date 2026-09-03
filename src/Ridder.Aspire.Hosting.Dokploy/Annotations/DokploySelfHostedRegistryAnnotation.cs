using Aspire.Hosting.ApplicationModel;
using Ridder.Aspire.Hosting.Dokploy.Models;

namespace Ridder.Aspire.Hosting.Dokploy.Annotations;

internal sealed class DokploySelfHostedRegistryAnnotation : DokployRegistryAnnotationBase
{
    public DokploySelfHostedRegistryAnnotation(string registryUrl, string username, string password)
        : base(registryUrl, username, password, null, null, null, "cloud")
    {
    }

    public DokploySelfHostedRegistryAnnotation(ParameterResource registryUrlParameter, string username, string password)
        : base(null, username, password, registryUrlParameter, null, null, "cloud")
    {
    }

    public override DokployRegistryMode Mode => DokployRegistryMode.SelfHosted;
}
