using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RobloxMultiLauncher.Services;

public sealed class AuthExpiredException : Exception
{
    public AuthExpiredException() : base("The saved Roblox session is no longer valid. Sign in again.") { }
}

/// <summary>Talks to Roblox web endpoints using a stored session cookie. Secrets are never logged.</summary>
public sealed class RobloxAuthService
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<(long Id, string Name)> GetAuthenticatedUserAsync(string cookie, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie);
        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new AuthExpiredException();
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return (doc.RootElement.GetProperty("id").GetInt64(), doc.RootElement.GetProperty("name").GetString() ?? "Unknown");
    }

    /// <summary>Gets a short-lived, single-use authentication ticket used to start the game client.</summary>
    public async Task<string> GetAuthTicketAsync(string cookie, CancellationToken ct)
    {
        string csrf = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var req = NewRequest(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket", cookie);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
            if (csrf.Length > 0) req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new AuthExpiredException();

            // First call is expected to return 403 plus a CSRF token; retry once with it.
            if (resp.StatusCode == HttpStatusCode.Forbidden && resp.Headers.TryGetValues("x-csrf-token", out var tokens))
            {
                csrf = tokens.First();
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Roblox returned HTTP {(int)resp.StatusCode} when requesting a launch ticket.");

            if (resp.Headers.TryGetValues("rbx-authentication-ticket", out var t) && t.FirstOrDefault() is { Length: > 0 } ticket)
                return ticket;
            throw new InvalidOperationException("Roblox did not return an authentication ticket (the API may have changed).");
        }
        throw new InvalidOperationException("Roblox rejected the CSRF token twice.");
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string cookie)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
        req.Headers.TryAddWithoutValidation("User-Agent", "RobloxMultiInstanceLauncher/1.0");
        return req;
    }
}
