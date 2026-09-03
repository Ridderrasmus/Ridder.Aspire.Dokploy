using Ridder.Aspire.Hosting.Dokploy.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ridder.Aspire.Hosting.Dokploy.Services;

internal static partial class ContainerRegistryDigestResolver
{
    private const string DockerManifestList = "application/vnd.docker.distribution.manifest.list.v2+json";
    private const string DockerManifest = "application/vnd.docker.distribution.manifest.v2+json";
    private const string OciImageIndex = "application/vnd.oci.image.index.v1+json";
    private const string OciImageManifest = "application/vnd.oci.image.manifest.v1+json";

    internal static async Task<string> ResolveAsync(
        string imageReference,
        DokployResolvedRegistrySettings registrySettings,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        return await ResolveAsync(imageReference, registrySettings, client, cancellationToken);
    }

    internal static async Task<string> ResolveAsync(
        string imageReference,
        DokployResolvedRegistrySettings registrySettings,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var reference = Parse(imageReference, registrySettings.RegistryUrl);
        var credentials = ResolveCredentials(reference.RegistryApiBase, registrySettings);
        var manifestUri = new Uri(
            reference.RegistryApiBase,
            $"v2/{reference.Repository}/manifests/{Uri.EscapeDataString(reference.Tag)}");

        using var initialResponse = await SendManifestRequestAsync(
            client,
            manifestUri,
            credentials.Username,
            credentials.Password,
            bearerToken: null,
            HttpMethod.Head,
            cancellationToken);

        if (initialResponse.StatusCode != HttpStatusCode.Unauthorized)
        {
            return await CreateDigestReferenceAsync(
                client,
                initialResponse,
                manifestUri,
                reference.ImageWithoutDigest,
                credentials,
                bearerToken: null,
                cancellationToken);
        }

        var challenge = initialResponse.Headers.WwwAuthenticate
            .FirstOrDefault(value => value.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
        if (challenge is null || string.IsNullOrWhiteSpace(challenge.Parameter))
        {
            initialResponse.EnsureSuccessStatusCode();
        }

        var bearerToken = await GetBearerTokenAsync(
            client,
            challenge!,
            reference.Repository,
            credentials,
            cancellationToken);

        using var authenticatedResponse = await SendManifestRequestAsync(
            client,
            manifestUri,
            credentials.Username,
            credentials.Password,
            bearerToken,
            HttpMethod.Head,
            cancellationToken);

        return await CreateDigestReferenceAsync(
            client,
            authenticatedResponse,
            manifestUri,
            reference.ImageWithoutDigest,
            credentials,
            bearerToken,
            cancellationToken);
    }

    private static async Task<string> CreateDigestReferenceAsync(
        HttpClient client,
        HttpResponseMessage response,
        Uri manifestUri,
        string imageWithoutDigest,
        RegistryCredentials credentials,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode && TryGetDigest(response, out var digest))
        {
            return $"{imageWithoutDigest}@{digest}";
        }

        if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
        {
            response.EnsureSuccessStatusCode();
        }

        using var getResponse = await SendManifestRequestAsync(
            client,
            manifestUri,
            credentials.Username,
            credentials.Password,
            bearerToken,
            HttpMethod.Get,
            cancellationToken);
        getResponse.EnsureSuccessStatusCode();

        if (TryGetDigest(getResponse, out digest))
        {
            return $"{imageWithoutDigest}@{digest}";
        }

        var manifest = await getResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(manifest))}";
        return $"{imageWithoutDigest}@{digest}";
    }

    private static async Task<HttpResponseMessage> SendManifestRequestAsync(
        HttpClient client,
        Uri manifestUri,
        string username,
        string password,
        string? bearerToken,
        HttpMethod method,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, manifestUri);
        request.Headers.Accept.ParseAdd(OciImageIndex);
        request.Headers.Accept.ParseAdd(OciImageManifest);
        request.Headers.Accept.ParseAdd(DockerManifestList);
        request.Headers.Accept.ParseAdd(DockerManifest);

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        else if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<string> GetBearerTokenAsync(
        HttpClient client,
        AuthenticationHeaderValue challenge,
        string repository,
        RegistryCredentials credentials,
        CancellationToken cancellationToken)
    {
        var values = BearerParameterRegex()
            .Matches(challenge.Parameter!)
            .ToDictionary(
                match => match.Groups["key"].Value,
                match => match.Groups["value"].Value,
                StringComparer.OrdinalIgnoreCase);

        if (!values.TryGetValue("realm", out var realm) || !Uri.TryCreate(realm, UriKind.Absolute, out var tokenUri))
        {
            throw new InvalidOperationException("The container registry returned a Bearer challenge without a valid token realm.");
        }

        var query = new List<string>();
        if (values.TryGetValue("service", out var service) && !string.IsNullOrWhiteSpace(service))
        {
            query.Add($"service={Uri.EscapeDataString(service)}");
        }

        var scope = values.TryGetValue("scope", out var challengeScope) && !string.IsNullOrWhiteSpace(challengeScope)
            ? challengeScope
            : $"repository:{repository}:pull";
        query.Add($"scope={Uri.EscapeDataString(scope)}");

        var separator = string.IsNullOrWhiteSpace(tokenUri.Query) ? "?" : "&";
        var requestUri = new Uri($"{tokenUri}{separator}{string.Join("&", query)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        if (!string.IsNullOrWhiteSpace(credentials.Username) || !string.IsNullOrWhiteSpace(credentials.Password))
        {
            var encodedCredentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCredentials);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var token = payload.RootElement.TryGetProperty("token", out var tokenProperty)
            ? tokenProperty.GetString()
            : payload.RootElement.TryGetProperty("access_token", out var accessTokenProperty)
                ? accessTokenProperty.GetString()
                : null;

        return !string.IsNullOrWhiteSpace(token)
            ? token
            : throw new InvalidOperationException("The container registry token endpoint returned no token.");
    }

    private static bool TryGetDigest(HttpResponseMessage response, out string digest)
    {
        digest = response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            ? values.FirstOrDefault()?.Trim() ?? string.Empty
            : string.Empty;
        return digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase);
    }

    private static RegistryCredentials ResolveCredentials(
        Uri registryApiBase,
        DokployResolvedRegistrySettings registrySettings)
    {
        if (!TryCreateRegistryUri(registrySettings.RegistryUrl, out var configuredRegistry)
            || !RegistryHostsMatch(registryApiBase, configuredRegistry))
        {
            return RegistryCredentials.Empty;
        }

        return new RegistryCredentials(registrySettings.Username, registrySettings.Password);
    }

    private static bool TryCreateRegistryUri(string registryUrl, out Uri registryUri)
    {
        var normalized = registryUrl.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"https://{normalized}";
        }

        if (!normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out registryUri!);
    }

    private static bool RegistryHostsMatch(Uri imageRegistry, Uri configuredRegistry)
    {
        if (IsDockerHubHost(imageRegistry.Host) && IsDockerHubHost(configuredRegistry.Host))
        {
            return imageRegistry.Port == configuredRegistry.Port;
        }

        return imageRegistry.IdnHost.Equals(configuredRegistry.IdnHost, StringComparison.OrdinalIgnoreCase)
            && imageRegistry.Port == configuredRegistry.Port;
    }

    private static RegistryImageReference Parse(string imageReference, string configuredRegistryUrl)
    {
        var imageWithoutDigest = imageReference.Trim();
        var digestIndex = imageWithoutDigest.IndexOf('@');
        if (digestIndex >= 0)
        {
            imageWithoutDigest = imageWithoutDigest[..digestIndex];
        }

        var lastSlash = imageWithoutDigest.LastIndexOf('/');
        var lastColon = imageWithoutDigest.LastIndexOf(':');
        var hasTag = lastColon > lastSlash;
        var tag = hasTag ? imageWithoutDigest[(lastColon + 1)..] : "latest";
        if (!hasTag)
        {
            imageWithoutDigest = $"{imageWithoutDigest}:{tag}";
        }

        var nameWithoutTag = hasTag ? imageWithoutDigest[..lastColon] : imageWithoutDigest[..^($":{tag}".Length)];
        var firstSlash = nameWithoutTag.IndexOf('/');
        var firstSegment = firstSlash >= 0 ? nameWithoutTag[..firstSlash] : string.Empty;
        var hasExplicitRegistry = firstSlash >= 0
            && (firstSegment.Contains('.', StringComparison.Ordinal)
                || firstSegment.Contains(':', StringComparison.Ordinal)
                || firstSegment.Equals("localhost", StringComparison.OrdinalIgnoreCase));

        if (!hasExplicitRegistry)
        {
            var repository = firstSlash >= 0 ? nameWithoutTag : $"library/{nameWithoutTag}";
            return new RegistryImageReference(
                new Uri("https://registry-1.docker.io/", UriKind.Absolute),
                repository,
                tag,
                imageWithoutDigest);
        }

        var repositoryPath = nameWithoutTag[(firstSlash + 1)..];
        if (IsDockerHubHost(firstSegment))
        {
            return new RegistryImageReference(
                new Uri("https://registry-1.docker.io/", UriKind.Absolute),
                repositoryPath,
                tag,
                imageWithoutDigest);
        }

        var scheme = Uri.TryCreate(configuredRegistryUrl, UriKind.Absolute, out var configuredRegistry)
            && configuredRegistry.Host.Equals(firstSegment.Split(':')[0], StringComparison.OrdinalIgnoreCase)
            ? configuredRegistry.Scheme
            : "https";

        return new RegistryImageReference(
            new Uri($"{scheme}://{firstSegment}/", UriKind.Absolute),
            repositoryPath,
            tag,
            imageWithoutDigest);
    }

    private static bool IsDockerHubHost(string host)
    {
        return host.Equals("docker.io", StringComparison.OrdinalIgnoreCase)
            || host.Equals("index.docker.io", StringComparison.OrdinalIgnoreCase)
            || host.Equals("registry-1.docker.io", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("(?<key>[A-Za-z_]+)=\"(?<value>[^\"]*)\"")]
    private static partial Regex BearerParameterRegex();

    private sealed record RegistryImageReference(
        Uri RegistryApiBase,
        string Repository,
        string Tag,
        string ImageWithoutDigest);

    private sealed record RegistryCredentials(string Username, string Password)
    {
        public static RegistryCredentials Empty { get; } = new(string.Empty, string.Empty);
    }
}
