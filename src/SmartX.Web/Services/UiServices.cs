using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Components;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;

namespace SmartX.Web.Services;

/// <summary>
/// Per-user (per Blazor circuit) state that survives page navigation - filters, selections, form drafts and the
/// last results of each module - so switching between the three pillars never loses the operator's context.
/// </summary>
public sealed class UiState
{
    // Part 1
    public string IngestionTab { get; set; } = "radar";
    public string DeviceSearch { get; set; } = "";
    public SensorCategory? DeviceCategory { get; set; }
    public RegisterForm Draft { get; set; } = new();
    public string? AggregateA { get; set; }
    public string? AggregateB { get; set; }

    // Part 2
    public string StreamQuery { get; set; } = "";
    public SensorCategory? StreamCategory { get; set; }
    public string StreamState { get; set; } = "";
    public string? StreamSelectedMac { get; set; }
    public string CriticalTarget { get; set; } = "MOIST-B2";

    // PoE
    public string? Facility { get; set; }
    public string RouteQuery { get; set; } = "";
    public RouteDto? LastRoute { get; set; }
    public string? HistoryMac { get; set; }
    public DateTime HistoryFromSast { get; set; } = TimeFormat.ToSast(DateTime.UtcNow).AddHours(-2);
    public DateTime HistoryToSast { get; set; } = TimeFormat.ToSast(DateTime.UtcNow);
    public HistoryQueryResultDto? LastHistory { get; set; }

    public int Navigations { get; set; }
}

/// <summary>Registration form model with client-side DataAnnotations validation.</summary>
public sealed class RegisterForm
{
    [Required(ErrorMessage = "MAC address is required.")]
    [RegularExpression(@"^\s*([0-9A-Fa-f]{2}[:\-]?){5}[0-9A-Fa-f]{2}\s*$", ErrorMessage = "Use the format AA:BB:CC:DD:EE:FF.")]
    public string Mac { get; set; } = "";

    [Required(ErrorMessage = "Give the sensor a name.")]
    [StringLength(32, MinimumLength = 3, ErrorMessage = "Name must be 3-32 characters.")]
    [RegularExpression(@"^[A-Za-z0-9\-_ ]+$", ErrorMessage = "Letters, numbers, spaces, - and _ only.")]
    public string DisplayName { get; set; } = "";

    public SensorCategory Category { get; set; } = SensorCategory.Environmental;

    [Required(ErrorMessage = "Choose what this sensor measures.")]
    public string Metric { get; set; } = "";

    public string FacilityId { get; set; } = "";
    public string ZoneId { get; set; } = "";
    public string SubZoneId { get; set; } = "";

    [Required(ErrorMessage = "Choose the deployment node.")]
    public string NodeId { get; set; } = "";
}

public static class TimeFormat
{
    /// <summary>The farms and sub-stations run on South African Standard Time (UTC+2, no DST).</summary>
    public static readonly TimeSpan SastOffset = TimeSpan.FromHours(2);

    public static DateTime ToSast(DateTime utc) => DateTime.SpecifyKind(utc.ToUniversalTime() + SastOffset, DateTimeKind.Unspecified);

    public static DateTime FromSast(DateTime sast) => DateTime.SpecifyKind(sast - SastOffset, DateTimeKind.Utc);

    public static string Clock(DateTime utc) => ToSast(utc).ToString("HH:mm:ss");

    public static string Ago(DateTime? utc)
    {
        if (utc is null) return "never";
        var s = (DateTime.UtcNow - utc.Value).TotalSeconds;
        return s switch
        {
            < 2 => "just now",
            < 60 => $"{s:0}s ago",
            < 3600 => $"{s / 60:0}m ago",
            < 86400 => $"{s / 3600:0}h ago",
            _ => $"{s / 86400:0}d ago"
        };
    }

    public static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        _ => $"{b / 1024.0 / 1024.0:0.##} MB"
    };
}

public enum ToastLevel { Info, Success, Warning, Error }

public sealed record Toast(Guid Id, ToastLevel Level, string Message, DateTime At);

/// <summary>Scoped toast notifications (crisp feedback loop for every user action).</summary>
public sealed class ToastService
{
    private readonly List<Toast> _toasts = [];
    public event Action? Changed;
    public IReadOnlyList<Toast> Items => _toasts;

    public void Show(string message, ToastLevel level = ToastLevel.Info)
    {
        var toast = new Toast(Guid.NewGuid(), level, message, DateTime.UtcNow);
        _toasts.Add(toast);
        if (_toasts.Count > 4) _toasts.RemoveAt(0);
        Changed?.Invoke();
        _ = DismissLater(toast.Id, level == ToastLevel.Error ? 7000 : 4000);
    }

    public void Info(string m) => Show(m, ToastLevel.Info);
    public void Success(string m) => Show(m, ToastLevel.Success);
    public void Warn(string m) => Show(m, ToastLevel.Warning);
    public void Error(string m) => Show(m, ToastLevel.Error);

    public void Dismiss(Guid id)
    {
        if (_toasts.RemoveAll(t => t.Id == id) > 0) Changed?.Invoke();
    }

    private async Task DismissLater(Guid id, int ms)
    {
        await Task.Delay(ms);
        Dismiss(id);
    }
}

/// <summary>
/// Base class for live dashboard pages: loads once, then refreshes on a <see cref="PeriodicTimer"/> started after the
/// first interactive render. All calls are async, so the UI never blocks while the API is working.
/// The loop is cancelled when the user navigates away, so no background polling leaks between pages.
/// </summary>
public abstract class LiveComponentBase : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private bool _loopStarted;

    [Inject] protected SmartXApiClient Api { get; set; } = default!;
    [Inject] protected ToastService Toasts { get; set; } = default!;
    [Inject] protected UiState Ui { get; set; } = default!;

    protected bool Loading { get; private set; } = true;
    protected bool ApiOnline { get; private set; } = true;
    protected DateTime? LastUpdatedUtc { get; private set; }
    protected virtual TimeSpan RefreshInterval => TimeSpan.FromSeconds(2);
    protected CancellationToken Token => _cts.Token;

    protected abstract Task RefreshAsync(CancellationToken ct);

    protected override async Task OnInitializedAsync()
    {
        await SafeRefreshAsync();
        Loading = false;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender && !_loopStarted)
        {
            _loopStarted = true;
            _ = LoopAsync();
        }
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await SafeRefreshAsync();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
            // page disposed
        }
    }

    protected async Task SafeRefreshAsync()
    {
        try
        {
            await RefreshAsync(_cts.Token);
            if (!ApiOnline) Toasts.Success("Reconnected to the gateway API.");
            ApiOnline = true;
            LastUpdatedUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (ApiOnline) Toasts.Error("Gateway API unreachable - retrying automatically.");
            ApiOnline = false;
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        _cts.Cancel(); // not disposed: an in-flight refresh may still read the token
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
