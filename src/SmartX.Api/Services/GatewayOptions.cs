namespace SmartX.Api.Services;

public sealed class GatewayOptions
{
    /// <summary>1 = Part 1 only, 2 = Parts 1-2, 3 = full PoE. Lets you demo each submission stage.</summary>
    public int ActivePillars { get; set; } = 3;

    /// <summary>Simulated gateway CPU budget: max packets processed per tick.</summary>
    public int MaxPacketsPerTick { get; set; } = 300;

    public int TickMilliseconds { get; set; } = 100;
    public int StaleAfterSeconds { get; set; } = 5;
    public int OfflineAfterSeconds { get; set; } = 12;
    public int SeedHistoryHours { get; set; } = 48;
    public int SeedIntervalSeconds { get; set; } = 60;
    public int MaxHistoryPerDevice { get; set; } = 150_000;
}

public sealed class AttachmentOptions
{
    public string StoragePath { get; set; } = "App_Data/attachments";
    public long MaxFileBytes { get; set; } = 15 * 1024 * 1024;
    public string EncryptionKeyBase64 { get; set; } = "";
}
