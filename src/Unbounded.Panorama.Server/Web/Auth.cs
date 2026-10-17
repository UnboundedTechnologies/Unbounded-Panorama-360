using System.Security.Cryptography;
using System.Text;
using Unbounded.Panorama.Server.Storage;

namespace Unbounded.Panorama.Server.Web;

/// <summary>
/// Optional password for the web page. Off until one is set in Settings; then the API needs a
/// session cookie, signed with a key kept in the data folder (so sessions survive restarts).
/// </summary>
public sealed class Auth
{
    public const string Cookie = "upn_session";
    private readonly DataStore _store;
    private readonly byte[] _key;

    public Auth(DataStore store)
    {
        _store = store;
        var keyFile = Path.Combine(store.Root, "session.key");
        if (!File.Exists(keyFile))
        {
            File.WriteAllBytes(keyFile, RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _key = File.ReadAllBytes(keyFile);
    }

    public bool Required => _store.Settings.PasswordHash is not null;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 200_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
    }

    public bool Verify(string password)
    {
        if (_store.Settings.PasswordHash is not { } stored || stored.Split(':') is not [var s, var h])
            return false;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(s), 200_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(h));
    }

    /// <summary>A session token tied to the current password (changing it signs everyone out).</summary>
    public string Token() =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes("session:" + _store.Settings.PasswordHash)));

    public bool IsSignedIn(HttpContext context) =>
        !Required || (context.Request.Cookies.TryGetValue(Cookie, out var value)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(Token())));
}
