using System.Text.Json.Serialization;

namespace FullScreenManager;

internal enum SessionState
{
    Creating,
    Active,
    Returning,
    Removing,
    RetryRequired
}

internal sealed class ManagedSession
{
    public long WindowHandle { get; set; }
    public uint ProcessId { get; set; }
    public DateTime? ProcessStartedUtc { get; set; }
    public string ExecutablePath { get; set; } = "";
    public Guid OriginDesktopId { get; set; }
    public Guid DedicatedDesktopId { get; set; }
    public string Name { get; set; } = "Fullscreen";
    public SessionState State { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public int RetryCount { get; set; }
    public DateTime NextRetryUtc { get; set; }

    [JsonIgnore] public DesktopService.Desktop? Origin { get; set; }
    [JsonIgnore] public DesktopService.Desktop? Dedicated { get; set; }
    [JsonIgnore] public long NextNameSyncScan { get; set; }
    [JsonIgnore] public int NameSyncRetryCount { get; set; }
    [JsonIgnore] public bool AwaitingFullscreenReactivation { get; set; }
    [JsonIgnore] public bool ActivationRequested { get; set; }
    [JsonIgnore] public bool WasOnDedicatedDesktop { get; set; }
    [JsonIgnore] public bool WasForeground { get; set; }
    [JsonIgnore] public int MissingDesktopObservations { get; set; }
    [JsonIgnore] public int MissingWindowObservations { get; set; }
    [JsonIgnore] public IntPtr Hwnd => new(WindowHandle);
}
