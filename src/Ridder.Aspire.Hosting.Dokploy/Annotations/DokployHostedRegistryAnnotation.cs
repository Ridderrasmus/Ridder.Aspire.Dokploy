using Aspire.Hosting.ApplicationModel;
using Ridder.Aspire.Hosting.Dokploy.Models;

namespace Ridder.Aspire.Hosting.Dokploy.Annotations;

internal sealed class DokployHostedRegistryAnnotation : DokployRegistryAnnotationBase
{
    public DokployHostedRegistryAnnotation(string registryUrl, string username, string password)
        : base(registryUrl, username, password, null, null, null, "cloud")
    {
    }

    public DokployHostedRegistryAnnotation(ParameterResource registryUrlParameter, ParameterResource usernameParameter, ParameterResource passwordParameter)
        : base(null, null, null, registryUrlParameter, usernameParameter, passwordParameter, "cloud")
    {
    }

    public override DokployRegistryMode Mode => DokployRegistryMode.Hosted;
}
