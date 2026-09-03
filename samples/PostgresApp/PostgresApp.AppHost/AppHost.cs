using Ridder.Aspire.Hosting.Dokploy;

var builder = DistributedApplication.CreateBuilder(args);

var dokploy = builder.AddDokployEnvironment("postgres-app")
    .WithHostedRegistry();

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("postgres-app-data")
    .PublishToDokploy(dokploy);

var database = postgres.AddDatabase("postgresdb");

builder.AddProject<Projects.PostgresApp_ApiService>("api")
    .WithReference(database)
    .WithExternalHttpEndpoints()
    .WaitFor(postgres)
    .PublishToDokploy(dokploy, options => options
        .WithDomain("http", "postgres-api.example.com"));

builder.Build().Run();
