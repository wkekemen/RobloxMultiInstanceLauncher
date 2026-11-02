namespace RobloxMultiLauncher.Models;

/// <summary>Persisted account profile. The cookie is only ever stored DPAPI-encrypted.</summary>
public sealed class StoredProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = "";
    public long UserId { get; set; }
    /// <summary>Base64 of DPAPI(CurrentUser)-encrypted .ROBLOSECURITY value. Never plaintext.</summary>
    public string? ProtectedCookie { get; set; }
    public string PlaceInput { get; set; } = "";
}
