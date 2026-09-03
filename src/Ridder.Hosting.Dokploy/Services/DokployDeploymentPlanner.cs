using Aspire.Hosting.ApplicationModel;

namespace Ridder.Hosting.Dokploy.Services;

internal static class DokployDeploymentPlanner
{
    public static IReadOnlyList<IReadOnlyList<IComputeResource>> CreateBatches(
        IReadOnlyList<IComputeResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var resourcesByName = resources.ToDictionary(
            resource => resource.Name,
            StringComparer.OrdinalIgnoreCase);
        var dependenciesByResourceName = resources.ToDictionary(
            resource => resource.Name,
            resource => resource.Annotations
                .OfType<WaitAnnotation>()
                .Select(annotation => annotation.Resource.Name)
                .Where(resourcesByName.ContainsKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var pendingResourceNames = resourcesByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completedResourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batches = new List<IReadOnlyList<IComputeResource>>();

        while (pendingResourceNames.Count > 0)
        {
            var batch = resources
                .Where(resource => pendingResourceNames.Contains(resource.Name))
                .Where(resource => dependenciesByResourceName[resource.Name].IsSubsetOf(completedResourceNames))
                .ToList();

            if (batch.Count == 0)
            {
                var blockedResources = resources
                    .Where(resource => pendingResourceNames.Contains(resource.Name))
                    .Select(resource =>
                    {
                        var pendingDependencies = dependenciesByResourceName[resource.Name]
                            .Where(pendingResourceNames.Contains)
                            .Order(StringComparer.OrdinalIgnoreCase);
                        return $"{resource.Name} waits for [{string.Join(", ", pendingDependencies)}]";
                    });

                throw new InvalidOperationException(
                    $"Dokploy deployment dependencies contain a cycle: {string.Join("; ", blockedResources)}.");
            }

            batches.Add(batch);
            foreach (var resource in batch)
            {
                pendingResourceNames.Remove(resource.Name);
                completedResourceNames.Add(resource.Name);
            }
        }

        return batches;
    }
}
