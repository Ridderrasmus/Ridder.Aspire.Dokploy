using Aspire.Hosting.ApplicationModel;

namespace Ridder.Hosting.Dokploy.Annotations;

internal sealed class DokployEnvironmentConnectionAnnotation : IResourceAnnotation
{
    public DokployEnvironmentConnectionAnnotation(ParameterResource? apiKeyParameter, ParameterResource? apiUrlParameter)
    {
        ApiKeyParameter = apiKeyParameter;
        ApiUrlParameter = apiUrlParameter;
    }

    public ParameterResource? ApiKeyParameter { get; }
    public ParameterResource? ApiUrlParameter { get; }

    public async Task<string> ResolveApiKeyAsync(string environmentName, CancellationToken cancellationToken)
    {
        var apiKey = ApiKeyParameter is null
            ? null
            : await ApiKeyParameter.GetValueAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"API key for project {environmentName} is not set.");
        }

        return apiKey;
    }

    public async Task<string> ResolveApiUrlAsync(string environmentName, CancellationToken cancellationToken)
    {
        var apiUrl = ApiUrlParameter is null
            ? null
            : await ApiUrlParameter.GetValueAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            throw new InvalidOperationException($"Dokploy API URL for project {environmentName} is not set.");
        }

        return apiUrl;
    }
}
