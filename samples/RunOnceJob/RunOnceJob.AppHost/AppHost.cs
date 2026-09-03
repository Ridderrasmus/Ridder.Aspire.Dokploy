using Ridder.Hosting.Dokploy;

var builder = DistributedApplication.CreateBuilder(args);

var dokploy = builder.AddDokployEnvironment("run-once-job")
    .WithHostedRegistry();

builder.AddProject<Projects.RunOnceJob_Service>("job")
    .WithEnvironment("GREETING", "Hello from a Dokploy run-once deployment.")
    .PublishToDokploy(dokploy, options => options.RunOnce = true);

builder.Build().Run();
