using Ridder.Hosting.Dokploy.Models;

namespace Ridder.Hosting.Dokploy.Utilities;

internal static class DokployMountReconciler
{
    public static string GetMountIdentity(string type, string mountPath, string? hostPath, string? volumeName)
    {
        var normalizedType = NormalizeMountType(type);
        var normalizedMountPath = NormalizeMountPath(mountPath);
        var normalizedBackingPath = normalizedType == "bind"
            ? NormalizeMountValue(hostPath)
            : NormalizeMountValue(volumeName);

        return $"{normalizedType}|{normalizedMountPath}|{normalizedBackingPath}";
    }

    public static bool MountIdentityMatches(DokployMount existingMount, string type, string mountPath, string? hostPath, string? volumeName)
    {
        ArgumentNullException.ThrowIfNull(existingMount);

        return GetMountIdentity(existingMount.Type, existingMount.MountPath, existingMount.HostPath, existingMount.VolumeName)
            == GetMountIdentity(type, mountPath, hostPath, volumeName);
    }

    public static bool MountLocationMatches(DokployMount existingMount, string mountPath)
    {
        ArgumentNullException.ThrowIfNull(existingMount);

        return string.Equals(
            NormalizeMountPath(existingMount.MountPath),
            NormalizeMountPath(mountPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMountType(string? type)
    {
        var normalized = (type ?? string.Empty)
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            "bindmount" => "bind",
            "bind" => "bind",
            "volumemount" => "volume",
            "volume" => "volume",
            _ => normalized
        };
    }

    private static string NormalizeMountPath(string? mountPath)
    {
        var normalized = (mountPath ?? string.Empty).Trim().Replace("\\", "/", StringComparison.Ordinal);

        while (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    private static string? NormalizeMountValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
