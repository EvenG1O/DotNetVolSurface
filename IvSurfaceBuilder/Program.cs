using IvSurfaceBuilder.Models;
using IvSurfaceBuilder.Services;
using IvSurfaceBuilder.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddLogging();
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<InstrumentFilter>();
builder.Services.AddSingleton<IDeribitClient, DeribitClient>();
builder.Services.AddScoped<IIvSurfaceService, IvSurfaceService>();

// WebSocket streaming services
builder.Services.AddSingleton<IDeribitWsClient, DeribitWsClient>();
builder.Services.AddSingleton<WebSocketHub>();
builder.Services.AddHostedService<VolSurfaceStreamService>();

// CORS configuration
var allowedOrigin = builder.Configuration["AllowedOrigin"] ?? "https://eveng1o.github.io";

builder.Services.AddCors(options =>
{
    options.AddPolicy("ReactAppPolicy", policy =>
    {
        policy.WithOrigins(allowedOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// Configure middleware
app.UseCors("ReactAppPolicy");

// Enable WebSockets
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});
app.UseMiddleware<WebSocketMiddleware>();

app.UseHttpsRedirection();
app.MapControllers();

app.Run();