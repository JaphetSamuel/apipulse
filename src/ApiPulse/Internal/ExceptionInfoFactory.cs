using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ApiPulse.Internal;

internal static class ExceptionInfoFactory
{
    private const int MaxStackTraceLength = 16 * 1024;
    private const int MaxInnerDepth = 3;

    public static ExceptionInfo Create(Exception exception, Masker masker, bool handled, int depth = 0)
    {
        var info = new ExceptionInfo
        {
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = masker.MaskText(exception.Message),
            StackTrace = Truncate(exception.StackTrace, MaxStackTraceLength),
            Fingerprint = Fingerprint(exception),
            Handled = handled,
        };

        if (exception.InnerException is { } inner && depth < MaxInnerDepth)
        {
            info.Inner = Create(inner, masker, handled, depth + 1);
        }

        return info;
    }

    /// <summary>
    /// Hashes the exception type and its first frames (method names only, no line numbers or file paths) so the same bug groups
    /// together across deployments.
    /// </summary>
    private static string Fingerprint(Exception exception)
    {
        var sb = new StringBuilder(exception.GetType().FullName);
        try
        {
            var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
            for (var i = 0; i < Math.Min(frames.Length, 3); i++)
            {
                var method = frames[i].GetMethod();
                sb.Append('|').Append(method?.DeclaringType?.FullName).Append('.').Append(method?.Name);
            }
        }
        catch
        {
            // A fingerprint of the type alone is still useful.
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    public static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}

internal static class ClientIpFormatter
{
    public static string? Format(IPAddress? address, ClientIpMode mode)
    {
        if (address is null || mode == ClientIpMode.None)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (mode == ClientIpMode.Full)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes[3] = 0;
        }
        else
        {
            Array.Clear(bytes, 6, bytes.Length - 6);
        }

        return new IPAddress(bytes).ToString();
    }
}
