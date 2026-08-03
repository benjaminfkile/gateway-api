var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/api/health", () => Results.Json(new
{
    gateway = "up",
    timestamp = DateTime.UtcNow,
}));

app.Run();

public partial class Program { }
