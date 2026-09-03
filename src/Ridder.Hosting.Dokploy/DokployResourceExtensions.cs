using Aspire.Hosting.ApplicationModel;
using Ridder.Hosting.Dokploy.Abstractions;
using Ridder.Hosting.Dokploy.Annotations;

namespace Ridder.Hosting.Dokploy;

internal static class DokployResourceExtensions
{
    public static DokployEnvironmentConnectionAnnotation? GetDokployEnvironmentConnection(this IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return resource.TryGetAnnotationsOfType<DokployEnvironmentConnectionAnnotation>(out var annotations)
            ? annotations.LastOrDefault()
            : null;
    }

    public static DokployRegistryAccessAnnotation? GetDokployRegistryAccess(this IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return resource.TryGetAnnotationsOfType<DokployRegistryAccessAnnotation>(out var annotations)
            ? annotations.LastOrDefault()
            : null;
    }

    public static void ReplaceDokployRegistryAccessAnnotation(this IResource resource, DokployRegistryAccessAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(annotation);

        var existingAnnotations = resource.Annotations.OfType<DokployRegistryAccessAnnotation>().Cast<IResourceAnnotation>().ToList();
        foreach (var existingAnnotation in existingAnnotations)
        {
            resource.Annotations.Remove(existingAnnotation);
        }

        resource.Annotations.Add(annotation);
    }

    public static void ReplaceDokployRegistryAnnotation(this IResource resource, IDokployRegistryAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(annotation);

        var existingAnnotations = resource.Annotations.OfType<IDokployRegistryAnnotation>().Cast<IResourceAnnotation>().ToList();
        foreach (var existingAnnotation in existingAnnotations)
        {
            resource.Annotations.Remove(existingAnnotation);
        }

        resource.Annotations.Add(annotation);
    }

    public static bool TryGetDokployRegistryAnnotation(this IResource resource, out IDokployRegistryAnnotation? annotation)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (resource.TryGetAnnotationsOfType<IDokployRegistryAnnotation>(out var annotations))
        {
            annotation = annotations.LastOrDefault();
            return annotation is not null;
        }

        annotation = null;
        return false;
    }

    public static bool TryGetDokployPublishAnnotation(this IResource resource, out DokployPublishApplicationAnnotation? annotation)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (resource.TryGetAnnotationsOfType<DokployPublishApplicationAnnotation>(out var annotations))
        {
            annotation = annotations.LastOrDefault();
            return annotation is not null;
        }

        annotation = null;
        return false;
    }

    public static bool TryGetDokployPublishAnnotation(this IResource resource, DokployProjectEnvironmentResource environment, out DokployPublishApplicationAnnotation? annotation)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(environment);

        if (resource.TryGetAnnotationsOfType<DokployPublishApplicationAnnotation>(out var annotations))
        {
            annotation = annotations.LastOrDefault(a => ReferenceEquals(a.Environment, environment) || string.Equals(a.Environment.Name, environment.Name, StringComparison.OrdinalIgnoreCase));
            return annotation is not null;
        }

        annotation = null;
        return false;
    }

    public static IReadOnlyList<IComputeResource> GetDokployComputeResources(this IEnumerable<IComputeResource> computeResources, DokployProjectEnvironmentResource environment)
    {
        ArgumentNullException.ThrowIfNull(computeResources);
        ArgumentNullException.ThrowIfNull(environment);

        return computeResources
            .Where(resource => resource.TryGetDokployPublishAnnotation(environment, out _))
            .ToList();
    }
}