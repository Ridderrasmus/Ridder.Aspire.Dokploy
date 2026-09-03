using Ridder.Hosting.Dokploy;

var builder = DistributedApplication.CreateBuilder(args);

var dokploy = builder.AddDokployEnvironment("simple-web-app")
    .WithHostedRegistry();

builder.AddProject<Projects.SimpleWebApp_ApiService>("api")
    .WithExternalHttpEndpoints()
    .PublishToDokploy(dokploy, options => options
        .WithDomain("http", "api.example.com")
        .WithDomain("https", "api.example.com"));

builder.Build().Run();
