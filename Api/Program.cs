using Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddCors();
builder.Services.AddMemoryCache();
builder.Services.AddEndpointsApiExplorer();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseCors(builder => builder
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod());

app.MapGet("/", () => new
{
    name = "yet-another-xiaomi-client-api",
    projectUrl = "https://github.com/lswiderski/yet-another-xiaomi-cloud-client",
    loginFlow = new
    {
        step1 = "POST /login - Get QR code and session ID",
        step2 = "GET /login/{sessionId} - Poll for login completion and get tokens"
    },
    postWeightsEndpoint = "/weights",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "Unknown"

});

app.MapGet("/health", () => Results.Ok("Healthy"));
app.MapGet("/ping", () => Results.Ok("pong"));

app.MapLoginEndpoint();
app.MapWeightsEndpoint();

app.Run();
