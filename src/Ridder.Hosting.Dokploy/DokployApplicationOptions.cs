using Aspire.Hosting;

namespace Ridder.Hosting.Dokploy;

/// <summary>
/// Configures how an Aspire compute resource should be published to Dokploy.
/// </summary>
[AspireDto]
public sealed class DokployApplicationOptions
{
    private readonly List<DokployDomainConfiguration> _domains = [];

    /// <summary>
    /// Gets or sets the application name to use in Dokploy. When omitted, a name is generated from the environment and resource names.
    /// </summary>
    public string? ApplicationName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether environment variables should be synchronized to Dokploy.
    /// </summary>
    public bool ConfigureEnvironmentVariables { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether container mounts should be synchronized to Dokploy.
    /// </summary>
    public bool ConfigureMounts { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Dokploy domains should be created for external Aspire endpoints.
    /// </summary>
    public bool CreateDomainsForExternalEndpoints { get; set; } = true;

    /// <summary>
    /// Configures a Dokploy domain for a named external Aspire endpoint.
    /// </summary>
    /// <param name="endpointName">The name of the Aspire endpoint to expose.</param>
    /// <param name="domain">The domain host to associate with the endpoint.</param>
    /// <returns>The same options instance for chaining.</returns>
    /// <remarks>
    /// Specify only the host name, for example <c>api.example.com</c>, without a URI scheme or path.
    /// This method can be called multiple times to configure additional endpoint and domain combinations.
    /// </remarks>
    public DokployApplicationOptions WithDomain(string endpointName, string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        var normalizedEndpointName = endpointName.Trim();
        var normalizedDomain = domain.Trim();

        if (Uri.CheckHostName(normalizedDomain) is UriHostNameType.Unknown)
        {
            throw new ArgumentException(
                $"The Dokploy domain '{domain}' is not a valid host name. Specify a host such as 'api.example.com' without a URI scheme or path.",
                nameof(domain));
        }

        if (!_domains.Any(config =>
            string.Equals(config.EndpointName, normalizedEndpointName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(config.Host, normalizedDomain, StringComparison.OrdinalIgnoreCase)))
        {
            _domains.Add(new DokployDomainConfiguration(normalizedEndpointName, normalizedDomain));
        }

        return this;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the resource should run to completion once instead of restarting continuously.
    /// </summary>
    public bool RunOnce { get; set; }

    internal IReadOnlyList<DokployDomainConfiguration> Domains => _domains;

    internal DokployApplicationOptions Clone()
    {
        var clone = new DokployApplicationOptions
        {
            ApplicationName = ApplicationName,
            ConfigureEnvironmentVariables = ConfigureEnvironmentVariables,
            ConfigureMounts = ConfigureMounts,
            CreateDomainsForExternalEndpoints = CreateDomainsForExternalEndpoints,
            RunOnce = RunOnce
        };

        foreach (var domain in _domains)
        {
            clone.WithDomain(domain.EndpointName, domain.Host);
        }

        return clone;
    }
}

internal sealed record DokployDomainConfiguration(string EndpointName, string Host);
