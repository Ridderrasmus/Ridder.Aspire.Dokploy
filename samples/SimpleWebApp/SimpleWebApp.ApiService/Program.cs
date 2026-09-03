var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "SimpleWebApp.ApiService",
    message = "Hello from the Dokploy Aspire sample."
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
