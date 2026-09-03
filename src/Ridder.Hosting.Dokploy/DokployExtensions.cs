using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ridder.Hosting.Dokploy.Abstractions;
using Ridder.Hosting.Dokploy.Annotations;
using Ridder.Hosting.Dokploy.Services;

namespace Ridder.Hosting.Dokploy;

/// <summary>
/// Provides Aspire builder extensions for configuring Dokploy environments and publishing compute resources to Dokploy.
/// </summary>
public static class DokployExtensions
{
    private const string DefaultRegistryUsername = "docker";
    private const string DefaultRegistryPassword = "password";
    private const string DefaultRemoteImageTag = "latest";

    /// <summary>
    /// Adds a Dokploy environment resource to the distributed application.
    /// </summary>
    /// <remarks>
    /// After creating the environment, configure a registry by calling <see cref="WithSelfHostedRegistry(IResourceBuilder{DokployProjectEnvironmentResource})"/>
    /// or <see cref="WithHostedRegistry(IResourceBuilder{DokployProjectEnvironmentResource})"/> before publishing resources to Dokploy.
    /// </remarks>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The logical name of the Dokploy environment.</param>
    /// <returns>A resource builder for configuring the Dokploy environment.</returns>
    [AspireExport("addDokployEnvironment")]
    public static IResourceBuilder<DokployProjectEnvironmentResource> AddDokployEnvironment(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.Services.TryAddSingleton<IDokployEnvironmentProvisioner, DokployEnvironmentProvisioner>();

        ParameterResource? apiUrlParameter = null;
        ParameterResource? apiKeyParameter = null;

        IResourceBuilder<DokployProjectEnvironmentResource> resourceBuilder;

        if (builder.ExecutionContext.IsRunMode)
        {
            resourceBuilder = builder.CreateResourceBuilder(new DokployProjectEnvironmentResource(name));
        }
        else
        {
            apiUrlParameter = builder.AddParameter($"{name}-api-url").Resource;

#pragma warning disable ASPIREINTERACTION001
            var apiKey = builder.AddParameter($"{name}-api-key", secret: true)
                .WithCustomInput(ctx => new()
                {
                    InputType = InputType.SecretText,
                    Name = $"API Key for {name}",
                    Required = true,
                    Placeholder = "CoolApiKey123"
                });
#pragma warning restore ASPIREINTERACTION001

            apiKeyParameter = apiKey.Resource;

            resourceBuilder = builder.AddResource(new DokployProjectEnvironmentResource(name));
        }

        resourceBuilder.WithAnnotation(new DokployEnvironmentConnectionAnnotation(apiKeyParameter, apiUrlParameter), ResourceAnnotationMutationBehavior.Replace);

        builder.Eventing.Subscribe<BeforeStartEvent>(async (beforeStart, cancellationToken) =>
        {
            var computeResources = beforeStart.Model.GetComputeResources().OfType<IComputeResource>().GetDokployComputeResources(resourceBuilder.Resource);

            foreach (var resource in computeResources)
            {
#pragma warning disable ASPIRECOMPUTE003
                if (resource.TryGetAnnotationsOfType<RegistryTargetAnnotation>(out var annotations)
                    && annotations.Any(annotation => ReferenceEquals(annotation.Registry, resourceBuilder.Resource)))
                {
                    continue;
                }

                resource.Annotations.Add(new RegistryTargetAnnotation(resourceBuilder.Resource));
#pragma warning restore ASPIRECOMPUTE003
            }

            await Task.CompletedTask;
        });

        return resourceBuilder;
    }

    /// <summary>
    /// Configures the Dokploy environment to use a Dokploy-hosted container registry.
    /// </summary>
    /// <param name="builder">The Dokploy environment builder.</param>
    /// <returns>The same builder instance for chaining.</returns>
    [AspireExport("withSelfHostedRegistry")]
    public static IResourceBuilder<DokployProjectEnvironmentResource> WithSelfHostedRegistry(this IResourceBuilder<DokployProjectEnvironmentResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            builder.Resource.ReplaceDokployRegistryAnnotation(
                new DokploySelfHostedRegistryAnnotation(string.Empty, DefaultRegistryUsername, DefaultRegistryPassword));
            return builder;
        }

        var registryDomainParameter = builder.ApplicationBuilder.AddParameter($"{builder.Resource.Name}-registry-domain-url").Resource;
        builder.Resource.ReplaceDokployRegistryAnnotation(
            new DokploySelfHostedRegistryAnnotation(registryDomainParameter, DefaultRegistryUsername, DefaultRegistryPassword));
        return builder;
    }

    /// <summary>
    /// Configures the Dokploy environment to use a Dokploy-hosted container registry with an explicit registry domain.
    /// </summary>
    /// <param name="builder">The Dokploy environment builder.</param>
    /// <param name="registryDomainUrl">The external registry domain used by Dokploy to host images.</param>
    /// <returns>The same builder instance for chaining.</returns>
    [AspireExportIgnore(Reason = "Use withSelfHostedRegistry() in TypeScript AppHosts. This explicit domain overload remains .NET-only for now.")]
    public static IResourceBuilder<DokployProjectEnvironmentResource> WithSelfHostedRegistry(this IResourceBuilder<DokployProjectEnvironmentResource> builder, string registryDomainUrl)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (string.IsNullOrWhiteSpace(registryDomainUrl))
        {
            throw new ArgumentException("A registry domain URL is required for a self-hosted Dokploy registry.", nameof(registryDomainUrl));
        }

        builder.Resource.ReplaceDokployRegistryAnnotation(
            new DokploySelfHostedRegistryAnnotation(registryDomainUrl, DefaultRegistryUsername, DefaultRegistryPassword));
        return builder;
    }

#pragma warning disable ASPIREINTERACTION001
    /// <summary>
    /// Configures the Dokploy environment to use an existing hosted container registry.
    /// </summary>
    /// <param name="builder">The Dokploy environment builder.</param>
    /// <returns>The same builder instance for chaining.</returns>
    [AspireExport("withHostedRegistry")]
    public static IResourceBuilder<DokployProjectEnvironmentResource> WithHostedRegistry(this IResourceBuilder<DokployProjectEnvironmentResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            builder.Resource.ReplaceDokployRegistryAnnotation(
                new DokployHostedRegistryAnnotation(string.Empty, string.Empty, string.Empty));
            return builder;
        }

        var registryUrlParameter = builder.ApplicationBuilder.AddParameter($"{builder.Resource.Name}-registry-url").Resource;
        var registryUsernameParameter = builder.ApplicationBuilder.AddParameter($"{builder.Resource.Name}-registry-username").Resource;
        var registryPasswordParameter = builder.ApplicationBuilder.AddParameter($"{builder.Resource.Name}-registry-password", secret: true).WithCustomInput(ctx => new()
        {
            InputType = InputType.SecretText,
            Name = "Registry Password",
            Required = true,
            Placeholder = "CoolPassword123"
        }).Resource;

        builder.Resource.ReplaceDokployRegistryAnnotation(
            new DokployHostedRegistryAnnotation(registryUrlParameter, registryUsernameParameter, registryPasswordParameter));
        return builder;
    }
#pragma warning restore ASPIREINTERACTION001

    /// <summary>
    /// Configures the Dokploy environment to use an existing hosted container registry with explicit credentials.
    /// </summary>
    /// <param name="builder">The Dokploy environment builder.</param>
    /// <param name="registryUrl">The base URL of the registry.</param>
    /// <param name="username">The username used to push images to the registry.</param>
    /// <param name="password">The password used to push images to the registry.</param>
    /// <returns>The same builder instance for chaining.</returns>
    [AspireExportIgnore(Reason = "Use withHostedRegistry() in TypeScript AppHosts. This credential overload remains .NET-only for now.")]
    public static IResourceBuilder<DokployProjectEnvironmentResource> WithHostedRegistry(this IResourceBuilder<DokployProjectEnvironmentResource> builder, string registryUrl, string username, string password)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (string.IsNullOrWhiteSpace(registryUrl))
        {
            throw new ArgumentException("A registry URL is required for a hosted registry.", nameof(registryUrl));
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("A registry username is required for a hosted registry.", nameof(username));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("A registry password is required for a hosted registry.", nameof(password));
        }

        builder.Resource.ReplaceDokployRegistryAnnotation(
            new DokployHostedRegistryAnnotation(registryUrl, username, password));
        return builder;
    }

    /// <summary>
    /// Publishes a compute resource to Dokploy using the default Dokploy application options.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The compute resource builder.</param>
    /// <param name="environmentBuilder">The Dokploy environment that should provision the resource.</param>
    /// <returns>The same builder instance for chaining.</returns>
    [AspireExport("publishToDokploy")]
    public static IResourceBuilder<T> PublishToDokploy<T>(this IResourceBuilder<T> builder, IResourceBuilder<DokployProjectEnvironmentResource> environmentBuilder)
        where T : IResource, IComputeResource
    {
        return PublishToDokploy(builder, environmentBuilder, static _ => { });
    }

    /// <summary>
    /// Publishes a compute resource to Dokploy and configures Dokploy-specific application options.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The compute resource builder.</param>
    /// <param name="environmentBuilder">The Dokploy environment that should provision the resource.</param>
    /// <param name="configure">An action that configures Dokploy application options for the resource.</param>
    /// <returns>The same builder instance for chaining.</returns>
    [AspireExportIgnore(Reason = "Action<T> callback is not ATS-compatible. Use the DokployApplicationOptions overload.")]
    public static IResourceBuilder<T> PublishToDokploy<T>(this IResourceBuilder<T> builder, IResourceBuilder<DokployProjectEnvironmentResource> environmentBuilder, Action<DokployApplicationOptions> configure)
        where T : IResource, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(environmentBuilder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new DokployApplicationOptions();
        configure(options);

#pragma warning disable ASPIREPIPELINES003
        // A stable tag lets Dokploy keep one readable service image reference
        // and force-pull the newly published manifest on each deployment.
        builder.WithRemoteImageTag(DefaultRemoteImageTag);
#pragma warning restore ASPIREPIPELINES003

        builder.WithAnnotation(new DokployPublishApplicationAnnotation(environmentBuilder.Resource, options), ResourceAnnotationMutationBehavior.Replace);

#pragma warning disable ASPIRECOMPUTE003
        builder.WithAnnotation(new RegistryTargetAnnotation(environmentBuilder.Resource), ResourceAnnotationMutationBehavior.Replace);
#pragma warning restore ASPIRECOMPUTE003

        if (!builder.Resource.TryGetAnnotationsOfType<DokployManifestAnnotation>(out _))
        {
            builder.WithAnnotation(new DokployManifestAnnotation());
            builder.WithManifestPublishingCallback(context => DokployManifestPublishing.WriteApplicationManifest(context, builder.Resource));
        }

        return builder;
    }


    /// <summary>
    /// Publishes a compute resource to Dokploy with explicit application options.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The compute resource builder.</param>
    /// <param name="environmentBuilder">The Dokploy environment that should provision the resource.</param>
    /// <param name="options">The Dokploy application options for the resource.</param>
    /// <returns>The same builder instance for chaining.</returns>
    [AspireExportIgnore(Reason = "Generic ATS export is not compatible with current TypeScript code generation. Use concrete resource overloads.")]
    public static IResourceBuilder<T> PublishToDokploy<T>(this IResourceBuilder<T> builder, IResourceBuilder<DokployProjectEnvironmentResource> environmentBuilder, DokployApplicationOptions options)
        where T : IResource, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(environmentBuilder);
        ArgumentNullException.ThrowIfNull(options);

        return PublishToDokploy(builder, environmentBuilder, o =>
        {
            o.ApplicationName = options.ApplicationName;
            o.ConfigureEnvironmentVariables = options.ConfigureEnvironmentVariables;
            o.ConfigureMounts = options.ConfigureMounts;
            o.CreateDomainsForExternalEndpoints = options.CreateDomainsForExternalEndpoints;
            o.RunOnce = options.RunOnce;

            foreach (var domain in options.Domains)
            {
                o.WithDomain(domain.EndpointName, domain.Host);
            }
        });
    }

    /// <summary>
    /// Publishes a project resource to Dokploy with explicit application options.
    /// </summary>
    /// <param name="builder">The project resource builder.</param>
    /// <param name="environmentBuilder">The Dokploy environment that should provision the resource.</param>
    /// <param name="options">The Dokploy application options for the resource.</param>
    /// <returns>The same builder instance for chaining.</returns>
    [AspireExportIgnore(Reason = "Custom options DTO is not compatible with current TypeScript code generation.")]
    public static IResourceBuilder<ProjectResource> PublishProjectToDokployWithOptions(this IResourceBuilder<ProjectResource> builder, IResourceBuilder<DokployProjectEnvironmentResource> environmentBuilder, DokployApplicationOptions options)
    {
        return PublishToDokploy(builder, environmentBuilder, options);
    }

    /// <summary>
    /// Adds a Dokploy environment using the legacy convenience API and configures it for a Dokploy-hosted registry.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The logical name of the Dokploy environment.</param>
    /// <returns>A resource builder for configuring the Dokploy environment.</returns>
    [AspireExportIgnore(Reason = "Legacy. Use addDokployEnvironment + withSelfHostedRegistry.")]
    public static IResourceBuilder<DokployProjectEnvironmentResource> AddDokployProject(this IDistributedApplicationBuilder builder, string name)
    {
        return builder.AddDokployEnvironment(name).WithSelfHostedRegistry();
    }

    /// <summary>
    /// Adds a Dokploy environment using the legacy convenience API and configures it for a Dokploy-hosted registry.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The logical name of the Dokploy environment.</param>
    /// <returns>A resource builder for configuring the Dokploy environment.</returns>
    [AspireExportIgnore(Reason = "Legacy. Use addDokployEnvironment + withSelfHostedRegistry.")]
    public static IResourceBuilder<DokployProjectEnvironmentResource> AddDokployProjectSelfHostedRegistry(this IDistributedApplicationBuilder builder, string name)
    {
        return builder.AddDokployEnvironment(name).WithSelfHostedRegistry();
    }

    /// <summary>
    /// Adds a Dokploy environment using the legacy convenience API and configures it for a Dokploy-hosted registry with an explicit domain.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The logical name of the Dokploy environment.</param>
    /// <param name="registryDomainUrl">The external registry domain used by Dokploy to host images.</param>
    /// <returns>A resource builder for configuring the Dokploy environment.</returns>
    [AspireExportIgnore(Reason = "Legacy. Use addDokployEnvironment + withSelfHostedRegistryDomain.")]
    public static IResourceBuilder<DokployProjectEnvironmentResource> AddDokployProjectSelfHostedRegistry(this IDistributedApplicationBuilder builder, string name, string registryDomainUrl)
    {
        if (string.IsNullOrWhiteSpace(registryDomainUrl))
        {
            throw new ArgumentException("A registry domain URL is required for a self-hosted Dokploy registry.", nameof(registryDomainUrl));
        }

        return builder.AddDokployEnvironment(name).WithSelfHostedRegistry(registryDomainUrl);
    }

#pragma warning disable ASPIREINTERACTION001
    /// <summary>
    /// Adds a Dokploy environment using the legacy convenience API and configures it for an existing hosted registry.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The logical name of the Dokploy environment.</param>
    /// <returns>A resource builder for configuring the Dokploy environment.</returns>
    [AspireExportIgnore(Reason = "Legacy TypeScript shim. Use addDokployEnvironment(name).withHostedRegistry() instead.")]
    public static IResourceBuilder<DokployProjectEnvironmentResource> AddDokployProjectHostedRegistry(this IDistributedApplicationBuilder builder, string name)
    {
        return builder.AddDokployEnvironment(name).WithHostedRegistry();
    }
#pragma warning restore ASPIREINTERACTION001

    /// <summary>
    /// Adds a Dokploy environment using the legacy convenience API and configures it for an existing hosted registry with explicit credentials.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The logical name of the Dokploy environment.</param>
    /// <param name="registryUrl">The base URL of the registry.</param>
    /// <param name="username">The username used to push images to the registry.</param>
    /// <param name="password">The password used to push images to the registry.</param>
    /// <returns>A resource builder for configuring the Dokploy environment.</returns>
    [AspireExportIgnore(Reason = "Legacy. Use addDokployEnvironment + withHostedRegistryCredentials.")]
    public static IResourceBuilder<DokployProjectEnvironmentResource> AddDokployProjectHostedRegistry(this IDistributedApplicationBuilder builder, string name, string registryUrl, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(registryUrl))
        {
            throw new ArgumentException("A registry URL is required for a hosted registry.", nameof(registryUrl));
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("A registry username is required for a hosted registry.", nameof(username));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("A registry password is required for a hosted registry.", nameof(password));
        }

        return builder.AddDokployEnvironment(name).WithHostedRegistry(registryUrl, username, password);
    }
}
