using Analyzer.Extension;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CopilotMessageHandler>();
builder.Services.AddHttpClient();

var app = builder.Build();

app.MapPost("/api/copilot", async (HttpContext context, CopilotMessageHandler handler) => { await handler.HandleAsync(context); });

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "csharp-modernization-extension" }));

app.Run();
