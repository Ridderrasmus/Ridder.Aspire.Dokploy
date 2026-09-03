var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "PostgresApp.ApiService",
    database = Environment.GetEnvironmentVariable("ConnectionStrings__postgresdb") is not null
}));

app.Run();
