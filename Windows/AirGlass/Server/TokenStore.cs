using System;
using System.Security.Cryptography;
using System.Text;

namespace AirGlass.Server;

/// <summary>
/// Holds the secret that the QR code carries. Read from server threads and
/// written from the UI thread, hence the lock.
/// </summary>
public sealed class TokenStore
{
    /// <summary>192 bits of entropy, encoded as 32 URL-safe characters.</summary>
    private const int ByteCount = 24;

    private readonly object _gate = new();
    private string _token = Generate();

    public string Current
    {
        get { lock (_gate) return _token; }
    }

    public string Rotate()
    {
        var newToken = Generate();
        lock (_gate) _token = newToken;
        return newToken;
    }

    /// <summary>
    /// One-time use: if <paramref name="candidate"/> is the current token,
    /// replaces it with a fresh one and returns true. Check and replacement
    /// are atomic, so the same token can never be accepted twice.
    /// </summary>
    public bool Consume(string candidate)
    {
        lock (_gate)
        {
            if (!ConstantTimeEquals(_token, candidate)) return false;
            _token = Generate();
            return true;
        }
    }

    /// <summary>Comparison time does not depend on where the strings differ,
    /// so response timing reveals nothing about the token.</summary>
    private static bool ConstantTimeEquals(string expected, string given)
    {
        var lhs = Encoding.UTF8.GetBytes(expected);
        var rhs = Encoding.UTF8.GetBytes(given);
        return lhs.Length == rhs.Length && CryptographicOperations.FixedTimeEquals(lhs, rhs);
    }

    /// <summary>A fresh 192-bit random value, URL-safe base64.</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(ByteCount);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
