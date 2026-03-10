using IvSurfaceBuilder.Models;
using IvSurfaceBuilder.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddLogging();
builder.Services.AddMemoryCache(); 


builder.Services.AddSingleton<InstrumentFilter>();
builder.Services.AddScoped<IDeribitClient, DeribitClient>();
builder.Services.AddScoped<IIvSurfaceService, IvSurfaceService>();

// CORS configuration
builder.Services.AddCors(options =>
{
    options.AddPolicy("ReactAppPolicy", policy =>
    {
        policy.WithOrigins(
            "http://localhost:5173",  // Vite default
            "http://localhost:3000"   
        )
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
    });
});

var app = builder.Build();

// Configure middleware
app.UseCors("ReactAppPolicy");
app.UseHttpsRedirection();
app.MapControllers();

app.Run();