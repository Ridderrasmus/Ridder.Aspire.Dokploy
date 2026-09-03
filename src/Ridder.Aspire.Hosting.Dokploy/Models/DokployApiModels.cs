using System.Text.Json.Serialization;

namespace Ridder.Aspire.Hosting.Dokploy.Models;

internal sealed class DokployProject
{
    [JsonPropertyName("projectId")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("env")]
    public string? Env { get; init; }

    [JsonPropertyName("environments")]
    public List<DokployEnvironment> Environments { get; init; } = [];
}

internal sealed class DokployCompose
{
    [JsonPropertyName("composeId")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("domains")]
    public List<DokployDomain> Domains { get; init; } = [];

    [JsonPropertyName("env")]
    public string Env { get; set; } = string.Empty;

    [JsonPropertyName("composeFile")]
    public string ComposeFile { get; set; } = string.Empty;
}

internal sealed class DokployEnvironment
{
    [JsonPropertyName("environmentId")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("compose")]
    public List<DokployCompose> Compose { get; init; } = [];

    [JsonPropertyName("applications")]
    public List<DokployApplication> Applications { get; init; } = [];
}

internal sealed class DokployRegistry
{
    [JsonPropertyName("registryId")]
    public string? RegistryId { get; init; }

    [JsonPropertyName("registryUrl")]
    public string RegistryUrl { get; init; } = string.Empty;

    [JsonPropertyName("projectId")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; init; }

    [JsonPropertyName("composeId")]
    public string? ComposeId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "registry";

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("password")]
    public string? Password { get; init; }

    public string PushPrefix { get; init; } = string.Empty;
}

internal sealed class DokployRemoteRegistry
{
    [JsonPropertyName("registryId")]
    public string? RegistryId { get; init; }

    [JsonPropertyName("registryName")]
    public string RegistryName { get; init; } = string.Empty;

    [JsonPropertyName("imagePrefix")]
    public string? ImagePrefix { get; init; }

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;

    [JsonPropertyName("registryUrl")]
    public string RegistryUrl { get; init; } = string.Empty;

    [JsonPropertyName("registryType")]
    public string RegistryType { get; init; } = string.Empty;
}

internal sealed class DokployDomain
{
    [JsonPropertyName("domainId")]
    public string? Id { get; init; }

    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int? Port { get; init; }
}

internal sealed class DokployMount
{
    private string? _type;
    private string? _hostPath;
    private string? _volumeName;
    private string? _mountPath;

    [JsonPropertyName("mountId")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string Type
    {
        get => _type ?? string.Empty;
        init => _type = value;
    }

    [JsonPropertyName("hostPath")]
    public string? HostPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_hostPath))
            {
                return _hostPath;
            }

            return string.Equals(Type, "bind", StringComparison.OrdinalIgnoreCase) ? Source : null;
        }
        init => _hostPath = value;
    }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("volumeName")]
    public string? VolumeName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_volumeName))
            {
                return _volumeName;
            }

            return string.Equals(Type, "volume", StringComparison.OrdinalIgnoreCase) ? Name : null;
        }
        init => _volumeName = value;
    }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("mountPath")]
    public string MountPath
    {
        get => _mountPath ?? Destination ?? string.Empty;
        init => _mountPath = value;
    }

    [JsonPropertyName("destination")]
    public string? Destination { get; init; }

    [JsonPropertyName("serviceType")]
    public string? ServiceType { get; init; }
}

internal sealed class DokployApplication
{
    [JsonPropertyName("applicationId")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("appName")]
    public string AppName { get; init; } = string.Empty;

    [JsonPropertyName("dockerImage")]
    public string? DockerImage { get; init; } = string.Empty;

    [JsonPropertyName("registryUrl")]
    public string? RegistryUrl { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string? Username { get; init; } = string.Empty;

    [JsonPropertyName("password")]
    public string? Password { get; init; } = string.Empty;
}

internal sealed class DokployServiceTask
{
    [JsonPropertyName("containerId")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("currentState")]
    public string CurrentState { get; init; } = string.Empty;

    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;
}

internal sealed class TrpcEnvelope<T>
{
    [JsonPropertyName("result")]
    public TrpcResult<T>? Result { get; init; }
}

internal sealed class TrpcResult<T>
{
    [JsonPropertyName("data")]
    public TrpcData<T>? Data { get; init; }
}

internal sealed class TrpcData<T>
{
    [JsonPropertyName("json")]
    public T? Json { get; init; }
}

internal sealed class GeneratedDomainData
{
    [JsonPropertyName("json")]
    public string? Json { get; init; }

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("domain")]
    public string? Domain { get; init; }
}
