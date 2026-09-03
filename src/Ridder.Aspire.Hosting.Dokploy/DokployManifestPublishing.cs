using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Ridder.Aspire.Hosting.Dokploy.Annotations;

namespace Ridder.Aspire.Hosting.Dokploy;

internal static class DokployManifestPublishing
{
    public static void WriteEnvironmentManifest(ManifestPublishingContext context, DokployProjectEnvironmentResource resource)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);

        var writer = context.Writer;

        writer.WriteString("type", "dokploy.environment.v0");
        writer.WriteStartObject("dokploy");
        writer.WriteBoolean("hasRegistryConfiguration", resource.HasRegistryConfiguration);

        if (resource.RegistryMode is { } registryMode)
        {
            writer.WriteString("registryMode", registryMode.ToString().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(resource.RegistryType))
        {
            writer.WriteString("registryType", resource.RegistryType);
        }

        writer.WriteStartObject("inputs");
        if (!string.IsNullOrWhiteSpace(resource.ApiUrlParameterName))
        {
            writer.WriteString("apiUrlParameter", resource.ApiUrlParameterName);
        }

        if (!string.IsNullOrWhiteSpace(resource.ApiKeyParameterName))
        {
            writer.WriteString("apiKeyParameter", resource.ApiKeyParameterName);
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    public static void WriteApplicationManifest<T>(ManifestPublishingContext context, T resource)
        where T : IResource, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);

        if (!resource.TryGetDokployPublishAnnotation(out var annotation))
        {
            return;
        }

        var writer = context.Writer;

        writer.WriteStartObject("dokploy");
        writer.WriteString("type", "application.v0");
        writer.WriteString("environment", annotation!.Environment.Name);
        writer.WriteString("applicationName", annotation.ResolveApplicationName(resource.Name));
        writer.WriteBoolean("configureEnvironmentVariables", annotation.Options.ConfigureEnvironmentVariables);
        writer.WriteBoolean("configureMounts", annotation.Options.ConfigureMounts);
        writer.WriteBoolean("createDomainsForExternalEndpoints", annotation.Options.CreateDomainsForExternalEndpoints);

        writer.WriteStartArray("domains");
        foreach (var domain in annotation.Options.Domains)
        {
            writer.WriteStartObject();
            writer.WriteString("endpoint", domain.EndpointName);
            writer.WriteString("host", domain.Host);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteBoolean("runOnce", annotation.Options.RunOnce);

        writer.WriteStartArray("externalEndpoints");
        foreach (var endpoint in resource.ResolveEndpoints().Where(endpoint => endpoint.Endpoint?.IsExternal == true))
        {
            writer.WriteStartObject();
            writer.WriteString("name", endpoint.Endpoint?.Name);
            writer.WriteString("scheme", endpoint.Endpoint?.UriScheme ?? "http");
            writer.WriteNumber("targetPort", endpoint.TargetPort.Value ?? endpoint.ExposedPort.Value ?? 0);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("mounts");
        if (resource.TryGetContainerMounts(out var mounts))
        {
            foreach (var mount in mounts)
            {
                writer.WriteStartObject();
                writer.WriteString("source", mount.Source);
                writer.WriteString("target", mount.Target);
                writer.WriteString("type", mount.Type.ToString());
                writer.WriteBoolean("readOnly", mount.IsReadOnly);
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();

        writer.WriteEndObject();

        context.TryAddDependentResources(annotation.Environment);
    }
}
