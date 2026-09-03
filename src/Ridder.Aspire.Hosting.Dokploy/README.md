# Ridder.Aspire.Hosting.Dokploy

`Ridder.Aspire.Hosting.Dokploy` adds Dokploy deployment support to .NET Aspire AppHosts.

## Features

- Add a Dokploy environment resource to an Aspire application model.
- Configure either Dokploy-hosted or self-hosted container registries.
- Publish project and container resources to Dokploy.
- Synchronize external endpoints, environment variables, mounts, and run-once behavior.
- Emit Aspire manifest metadata and deployment pipeline steps for Dokploy provisioning.

## Usage

```csharp
using Ridder.Aspire.Hosting.Dokploy;

var builder = DistributedApplication.CreateBuilder(args);

var dokploy = builder.AddDokployEnvironment("production")
    .WithHostedRegistry();

builder.AddProject<Projects.MyApi>("api")
    .WithExternalHttpEndpoints()
    .PublishToDokploy(dokploy, options => options
        .WithDomain("http", "api.example.com")
        .WithDomain("https", "api.example.com"));

builder.Build().Run();
```

## Samples

- `samples/SimpleWebApp`
- `samples/ContainerVolume`
- `samples/RunOnceJob`
- `samples/PostgresApp`

## Source provenance

This package was extracted from the Dokploy Aspire integration used in `ScarletPigs/ScarletPigsServices` and adapted into a standalone package repository.
