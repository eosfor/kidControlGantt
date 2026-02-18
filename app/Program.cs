using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

var runtime = RuntimeSettings.Load(builder.Configuration);
builder.Services.AddSingleton(runtime);
builder.Services.AddSingleton<AccessConfigProvider>();
builder.Services.AddSingleton<SessionRepository>();
builder.Services.AddSingleton<EmailNotificationService>();
builder.Services.AddHttpClient<MikrotikClient>((sp, client) =>
{
    var settings = sp.GetRequiredService<RuntimeSettings>();
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    if (!string.IsNullOrWhiteSpace(settings.AuthorizationHeader))
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", settings.AuthorizationHeader);
    }
});
builder.Services.AddSingleton<KidControlService>();
builder.Services.AddHostedService<SessionSweepHostedService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Text("OK"));

app.MapGet("/api/kid-control", async (KidControlService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.GetKidControlAsync(ct);
        return Results.Ok(result);
    }
    catch (AppHttpException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

app.MapGet("/api/state", async (KidControlService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.GetStateAsync(ct);
        return Results.Ok(result);
    }
    catch (AppHttpException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

app.MapGet("/api/users/{name}/stats", async (string name, int? days, KidControlService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.GetUserStatsAsync(name, days ?? 7, ct);
        return Results.Ok(result);
    }
    catch (AppHttpException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

app.MapPost("/api/users/{name}/request", async (string name, RequestWindowDto body, KidControlService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.RequestAccessAsync(name, body.WindowMinutes, ct);
        return Results.Ok(result);
    }
    catch (AppHttpException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

app.MapPost("/api/users/{name}/disable", async (string name, KidControlService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.DisableAccessAsync(name, ct);
        return Results.Ok(result);
    }
    catch (AppHttpException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

app.MapGet("/api/debug", async (KidControlService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.GetDebugAsync(ct);
        return Results.Ok(result);
    }
    catch (AppHttpException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

app.MapFallbackToFile("index.html");

app.Run();
