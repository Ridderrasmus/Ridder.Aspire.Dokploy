using Ridder.Aspire.Hosting.Dokploy;

var builder = DistributedApplication.CreateBuilder(args);

var dokploy = builder.AddDokployEnvironment("container-volume")
    .WithSelfHostedRegistry();

builder.AddContainer("whoami", "traefik/whoami", "v1.11.0")
    .WithHttpEndpoint(targetPort: 80, name: "http")
    .WithExternalHttpEndpoints()
    .WithVolume("whoami-data", "/tmp")
    .PublishToDokploy(dokploy, options => options
        .WithDomain("http", "whoami.example.com"));

builder.Build().Run();
