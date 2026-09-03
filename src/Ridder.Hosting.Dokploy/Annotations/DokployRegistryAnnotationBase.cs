using Aspire.Hosting.ApplicationModel;
using Ridder.Hosting.Dokploy.Abstractions;
using Ridder.Hosting.Dokploy.Models;

namespace Ridder.Hosting.Dokploy.Annotations;

internal abstract class DokployRegistryAnnotationBase : IDokployRegistryAnnotation
{
    private readonly string? _registryUrl;
    private readonly string? _username;
    private readonly string? _password;
    private readonly ParameterResource? _registryUrlParameter;
    private readonly ParameterResource? _usernameParameter;
    private readonly ParameterResource? _passwordParameter;

    protected DokployRegistryAnnotationBase(
        string? registryUrl,
        string? username,
        string? password,
        ParameterResource? registryUrlParameter,
        ParameterResource? usernameParameter,
        ParameterResource? passwordParameter,
        string registryType)
    {
        _registryUrl = registryUrl;
        _username = username;
        _password = password;
        _registryUrlParameter = registryUrlParameter;
        _usernameParameter = usernameParameter;
        _passwordParameter = passwordParameter;
        RegistryType = registryType;
    }

    public abstract DokployRegistryMode Mode { get; }
    public string RegistryType { get; }

    public async Task<DokployResolvedRegistrySettings> ResolveAsync(CancellationToken cancellationToken)
    {
        var registryUrl = _registryUrlParameter is not null
            ? await _registryUrlParameter.GetValueAsync(cancellationToken)
            : _registryUrl;

        var username = _usernameParameter is not null
            ? await _usernameParameter.GetValueAsync(cancellationToken)
            : _username;

        var password = _passwordParameter is not null
            ? await _passwordParameter.GetValueAsync(cancellationToken)
            : _password;

        if (string.IsNullOrWhiteSpace(registryUrl))
        {
            throw new InvalidOperationException("Registry URL is required.");
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("Registry username is required.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Registry password is required.");
        }

        return new DokployResolvedRegistrySettings(Mode, registryUrl, username, password, RegistryType);
    }
}
