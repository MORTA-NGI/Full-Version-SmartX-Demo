using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SmartX.Core.Contracts;
using SmartX.Core.Deployment;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Web.Services;

/// <summary>Result wrapper so pages can show server-side validation messages next to the right fields.</summary>
public sealed record ApiResult<T>(T? Value, string? Error, IReadOnlyDictionary<string, string[]>? FieldErrors = null)
{
    public bool Ok => Value is not null && Error is null;
    public static ApiResult<T> Success(T value) => new(value, null);
    public static ApiResult<T> Fail(string error, IReadOnlyDictionary<string, string[]>? fields = null) => new(default, error, fields);
}

/// <summary>Typed, fully asynchronous client for the Smart-X gateway API.</summary>
public sealed class SmartXApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = SmartXJson.Options;

    // ───────── gateway / devices ─────────
    public Task<GatewayStatusDto?> GetStatusAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<GatewayStatusDto>("api/gateway/status", Json, ct);

    public Task<DeviceSearchResultDto?> SearchDevicesAsync(string? query, SensorCategory? category, string? state, bool record, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(query)) qs.Add($"query={Uri.EscapeDataString(query)}");
        if (category is not null) qs.Add($"category={category}");
        if (!string.IsNullOrWhiteSpace(state)) qs.Add($"state={state}");
        if (record) qs.Add("record=true");
        return http.GetFromJsonAsync<DeviceSearchResultDto>("api/devices" + (qs.Count > 0 ? "?" + string.Join('&', qs) : ""), Json, ct);
    }

    public async Task<DeviceDto?> GetDeviceAsync(string mac, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"api/devices/{Uri.EscapeDataString(mac)}", ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<DeviceDto>(Json, ct) : null;
    }

    public async Task<List<MetricDefinition>> GetMetricsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<MetricDefinition>>("api/metrics", Json, ct) ?? [];

    public Task<ApiResult<RegisterSensorResponse>> RegisterAsync(RegisterSensorRequest req, CancellationToken ct = default) =>
        SendAsync<RegisterSensorResponse>(HttpMethod.Post, "api/devices", req, ct);

    public Task InspectAsync(string mac, CancellationToken ct = default) =>
        http.PostAsync($"api/devices/{Uri.EscapeDataString(mac)}/inspect", null, ct);

    public Task<DeviceTimelineDto?> GetTimelineAsync(string mac, int minutes, CancellationToken ct = default) =>
        http.GetFromJsonAsync<DeviceTimelineDto>($"api/devices/{Uri.EscapeDataString(mac)}/timeline?minutes={minutes}", Json, ct);

    // ───────── deployment ─────────
    public Task<DeploymentOverviewDto?> GetDeploymentAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<DeploymentOverviewDto>("api/deployment", Json, ct);

    public Task<PathValidationResult?> ValidatePathAsync(string nodeId, CancellationToken ct = default) =>
        http.GetFromJsonAsync<PathValidationResult>($"api/deployment/validate/{Uri.EscapeDataString(nodeId)}", Json, ct);

    public Task<ApiResult<DeploymentOverviewDto>> AddDeploymentNodeAsync(AddDeploymentNodeRequest req, CancellationToken ct = default) =>
        SendAsync<DeploymentOverviewDto>(HttpMethod.Post, "api/deployment/nodes", req, ct);

    // ───────── attachments ─────────
    public async Task<List<AttachmentDto>> GetAttachmentsAsync(string mac, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<AttachmentDto>>($"api/devices/{Uri.EscapeDataString(mac)}/attachments", Json, ct) ?? [];

    /// <summary>Streams the files to the API as multipart/form-data (no full in-memory copy).</summary>
    public async Task<ApiResult<UploadResultDto>> UploadAsync(string mac, IEnumerable<(string Name, string ContentType, Stream Content)> files, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (name, type, content) in files)
        {
            var part = new StreamContent(content, 81920);
            part.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(type) ? "application/octet-stream" : type);
            form.Add(part, "files", name);
        }
        using var resp = await http.PostAsync($"api/devices/{Uri.EscapeDataString(mac)}/attachments", form, ct);
        return await ReadAsync<UploadResultDto>(resp, ct);
    }

    public Task DeleteAttachmentAsync(string mac, Guid id, CancellationToken ct = default) =>
        http.DeleteAsync($"api/devices/{Uri.EscapeDataString(mac)}/attachments/{id}", ct);

    // ───────── telemetry / OOP demos ─────────
    public Task<PipelineStatsDto?> GetPipelineAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<PipelineStatsDto>("api/telemetry/pipeline", Json, ct);

    public Task<ApiResult<AggregateDto>> AggregateAsync(string a, string b, CancellationToken ct = default) =>
        SendAsync<AggregateDto>(HttpMethod.Get, $"api/telemetry/aggregate?a={Uri.EscapeDataString(a)}&b={Uri.EscapeDataString(b)}", null, ct);

    // ───────── engagement ─────────
    public Task<RadarDto?> GetRadarAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<RadarDto>("api/engagement/radar", Json, ct);

    public Task<ApiResult<IssueDto>> ReportIssueAsync(ReportIssueRequest req, CancellationToken ct = default) =>
        SendAsync<IssueDto>(HttpMethod.Post, "api/engagement/issues", req, ct);

    public Task<ApiResult<IssueDto>> TransitionIssueAsync(int id, bool resolve, CancellationToken ct = default) =>
        SendAsync<IssueDto>(HttpMethod.Post, $"api/engagement/issues/{id}/{(resolve ? "resolve" : "ack")}", null, ct);

    // ───────── Part 2: stream ─────────
    public Task<StreamStatsDto?> GetStreamStatsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<StreamStatsDto>("api/stream/stats", Json, ct);

    public Task<ApiResult<IngestAck>> InjectCriticalAsync(string? device, CancellationToken ct = default) =>
        SendAsync<IngestAck>(HttpMethod.Post, "api/stream/simulate/critical" + (string.IsNullOrEmpty(device) ? "" : $"?device={Uri.EscapeDataString(device)}"), null, ct);

    public async Task FloodAsync(int count, CancellationToken ct = default) =>
        await http.PostAsync($"api/stream/simulate/flood?count={count}", null, ct);

    public Task<CommandStateDto?> GetCommandsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<CommandStateDto>("api/commands", Json, ct);

    public Task<ApiResult<CommandRecordDto>> IssueCommandAsync(CommandRequest req, CancellationToken ct = default) =>
        SendAsync<CommandRecordDto>(HttpMethod.Post, "api/commands", req, ct);

    public Task<ApiResult<CommandRecordDto>> UndoAsync(CancellationToken ct = default) =>
        SendAsync<CommandRecordDto>(HttpMethod.Post, "api/commands/undo", null, ct);

    public Task<InsightsDto?> GetInsightsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<InsightsDto>("api/insights", Json, ct);

    // ───────── PoE: topology & history ─────────
    public Task<TopologyDto?> GetTopologyAsync(string? facility, CancellationToken ct = default) =>
        http.GetFromJsonAsync<TopologyDto>("api/topology" + (string.IsNullOrEmpty(facility) ? "" : $"?facility={facility}"), Json, ct);

    public Task<ApiResult<RouteDto>> GetRouteAsync(string query, CancellationToken ct = default) =>
        SendAsync<RouteDto>(HttpMethod.Get, $"api/topology/route?query={Uri.EscapeDataString(query)}", null, ct);

    public Task ToggleNodeAsync(string mac, CancellationToken ct = default) =>
        http.PostAsync($"api/topology/nodes/{Uri.EscapeDataString(mac)}/toggle", null, ct);

    public Task<HistoryQueryResultDto?> GetHistoryAsync(string mac, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        http.GetFromJsonAsync<HistoryQueryResultDto>(
            $"api/history/{Uri.EscapeDataString(mac)}?from={fromUtc:yyyy-MM-ddTHH:mm:ssZ}&to={toUtc:yyyy-MM-ddTHH:mm:ssZ}", Json, ct);

    // ───────── helpers ─────────
    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
            using var resp = await http.SendAsync(req, ct);
            return await ReadAsync<T>(resp, ct);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<T>.Fail($"Gateway API unreachable: {ex.Message}");
        }
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            var value = await resp.Content.ReadFromJsonAsync<T>(Json, ct);
            return value is null ? ApiResult<T>.Fail("Empty response from gateway.") : ApiResult<T>.Success(value);
        }

        // Parse either ProblemDetails {title, errors{}} or {error: "..."}.
        string text = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = doc.RootElement;
            Dictionary<string, string[]>? fields = null;
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object)
            {
                fields = errs.EnumerateObject().ToDictionary(p => p.Name,
                    p => p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : [p.Value.ToString()],
                    StringComparer.OrdinalIgnoreCase);
            }
            string message = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()!
                : fields is { Count: > 0 } ? string.Join(" ", fields.Values.SelectMany(v => v))
                : root.TryGetProperty("title", out var t) ? t.GetString() ?? resp.ReasonPhrase ?? "Request failed"
                : resp.ReasonPhrase ?? "Request failed";
            return ApiResult<T>.Fail(message, fields);
        }
        catch (JsonException)
        {
            return ApiResult<T>.Fail($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
    }
}
