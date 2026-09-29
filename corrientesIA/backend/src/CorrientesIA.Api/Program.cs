using CorrientesIA.Api.Services;
using CorrientesIA.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString =
    builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "No se configuró la cadena de conexión a MySQL.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(
        connectionString,
        new MySqlServerVersion(new Version(9, 4, 0))));

builder.Services.AddSingleton<InferenceService>();
builder.Services.AddScoped<GroundingService>();
var searxngUrl =
    builder.Configuration["SEARXNG_URL"]
    ?? "http://127.0.0.1:8080/";

builder.Services.AddHttpClient<WebSearchService>(client =>
{
    client.BaseAddress = new Uri(searxngUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors();
app.MapControllers();

app.Run();
