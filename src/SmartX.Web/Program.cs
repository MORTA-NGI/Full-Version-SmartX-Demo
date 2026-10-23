using SmartX.Web.Components;
using SmartX.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment());

// Blazor Server allows 32 KB SignalR messages by default; raise it so file uploads stream smoothly.
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 256 * 1024);

var apiBase = builder.Configuration["SmartXApi:BaseUrl"] ?? "http://localhost:5080";
builder.Services.AddHttpClient<SmartXApiClient>(c =>
{
    c.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(30);
});

// Scoped = one instance per user circuit → state survives navigation between the three pillars.
builder.Services.AddScoped<UiState>();
builder.Services.AddScoped<ToastService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();

// Same-origin proxy for decrypted attachments (photos/logs open directly in the browser).
app.MapGet("/files/{mac}/{id:guid}", async (string mac, Guid id, IHttpClientFactory factory, HttpContext ctx, CancellationToken ct) =>
{
    var client = factory.CreateClient();
    client.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/");
    using var resp = await client.GetAsync($"api/devices/{Uri.EscapeDataString(mac)}/attachments/{id}", HttpCompletionOption.ResponseHeadersRead, ct);
    if (!resp.IsSuccessStatusCode) return Results.StatusCode((int)resp.StatusCode);
    ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    if (resp.Content.Headers.ContentDisposition is { } cd) ctx.Response.Headers.ContentDisposition = cd.ToString();
    await resp.Content.CopyToAsync(ctx.Response.Body, ct);
    return Results.Empty;
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
