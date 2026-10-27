using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ApiPulse.Internal;

/// <summary>
/// Removes sensitive data from everything the SDK records. Structured formats (JSON, XML, form bodies, query strings, headers)
/// are masked by field name; free text is masked by regular expression. Masking never throws: on any failure the affected
/// value is replaced entirely rather than recorded unmasked.
/// </summary>
internal sealed class Masker
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex XmlLeaf = new(
        @"<(?<n>[A-Za-z_][\w.:-]*)(?<a>(?:\s[^>]*)?)>(?<v>[^<>]*)</\k<n>\s*>",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private readonly NameMatcher _headers;
    private readonly NameMatcher _query;
    private readonly NameMatcher _fields;
    private readonly Regex[] _patterns;
    private readonly JsonEncodedText _jsonMask;

    public Masker(ApiPulseOptions options, ILogger? logger = null)
    {
        Mask = string.IsNullOrEmpty(options.MaskValue) ? ApiPulseDefaults.DefaultMask : options.MaskValue;
        _jsonMask = JsonEncodedText.Encode(Mask);
        _headers = new NameMatcher(options.MaskHeaders);
        _query = new NameMatcher(options.MaskQueryParams);
        _fields = new NameMatcher(options.MaskBodyFields);
        _patterns = CompilePatterns(options.MaskValuePatterns, logger);
    }

    public string Mask { get; }

    public bool IsSensitiveField(string name) => _fields.IsMatch(name);

    public string MaskHeader(string name, string value) => _headers.IsMatch(name) ? Mask : MaskText(value);

    /// <summary>Applies the value patterns to free text.</summary>
    public string MaskText(string text)
    {
        if (_patterns.Length == 0 || text.Length == 0)
        {
            return text;
        }

        try
        {
            foreach (var pattern in _patterns)
            {
                text = pattern.Replace(text, Mask);
            }

            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            return Mask;
        }
    }

    /// <summary>Masks sensitive parameters of a raw query string, preserving order and encoding.</summary>
    public string? MaskQuery(string? rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery))
        {
            return null;
        }

        var lead = rawQuery[0] == '?';
        var result = MaskPairs(lead ? rawQuery.AsSpan(1) : rawQuery.AsSpan(), _query);
        return lead ? "?" + result : result;
    }

    /// <summary>Masks the query part of an absolute URL or any string that contains a <c>?query</c>.</summary>
    public string MaskUrl(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0)
        {
            return MaskText(url);
        }

        var fragment = url.IndexOf('#', q);
        var end = fragment < 0 ? url.Length : fragment;
        return string.Concat(MaskText(url.AsSpan(0, q).ToString()), "?", MaskPairs(url.AsSpan(q + 1, end - q - 1), _query), url.AsSpan(end));
    }

    /// <summary>Masks a captured body according to its media type and returns it as text.</summary>
    public string MaskBody(ReadOnlySpan<byte> body, string? contentType)
    {
        var media = MediaTypeOf(contentType);
        var encoding = EncodingOf(contentType);

        string text;
        if (IsJson(media))
        {
            var utf8 = encoding.CodePage == Encoding.UTF8.CodePage
                ? body
                : Encoding.Convert(encoding, Encoding.UTF8, body.ToArray());
            var masked = MaskJson(utf8, out var structured);
            text = structured ? masked : MaskText(encoding.GetString(body));
        }
        else if (media == "application/x-www-form-urlencoded")
        {
            text = MaskPairs(encoding.GetString(body), _fields);
        }
        else if (media.EndsWith("xml", StringComparison.Ordinal))
        {
            text = MaskXml(encoding.GetString(body));
        }
        else
        {
            text = encoding.GetString(body);
        }

        return MaskText(text);
    }

    // ------------------------------------------------------------------ JSON

    /// <summary>
    /// Walks the JSON token stream and replaces the value of every sensitive property, whatever its type or depth. Works on
    /// truncated or malformed input: everything up to the failure point is kept and the unparsed remainder is dropped, because
    /// it might hold a secret that could not be recognised.
    /// </summary>
    private string MaskJson(ReadOnlySpan<byte> json, out bool structured)
    {
        var sb = new StringBuilder(json.Length);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            MaxDepth = 256,
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var copied = 0;
        var tokens = 0;
        var pendingSensitive = false;
        var failed = false;

        try
        {
            while (reader.Read())
            {
                tokens++;
                if (reader.TokenType != JsonTokenType.PropertyName || !_fields.IsMatch(reader.GetString()!))
                {
                    continue;
                }

                pendingSensitive = true;
                if (!reader.Read())
                {
                    break;
                }

                tokens++;
                var valueStart = (int)reader.TokenStartIndex;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !reader.TrySkip())
                {
                    break;
                }

                var valueEnd = (int)reader.BytesConsumed;
                Append(sb, json[copied..valueStart]);
                sb.Append('"').Append(_jsonMask.ToString()).Append('"');
                copied = valueEnd;
                pendingSensitive = false;
            }
        }
        catch (JsonException)
        {
            failed = true;
        }

        structured = tokens > 0;
        if (!structured)
        {
            return string.Empty;
        }

        var good = failed || pendingSensitive ? (int)reader.BytesConsumed : json.Length;
        if (good > json.Length)
        {
            good = json.Length;
        }

        if (good > copied)
        {
            Append(sb, json[copied..good]);
        }

        if (pendingSensitive)
        {
            sb.Append('"').Append(_jsonMask.ToString()).Append('"');
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, ReadOnlySpan<byte> utf8) => sb.Append(Encoding.UTF8.GetString(utf8));

    // ------------------------------------------------------------------ forms and queries

    private string MaskPairs(ReadOnlySpan<char> pairs, NameMatcher names)
    {
        var sb = new StringBuilder(pairs.Length);
        var first = true;
        while (!pairs.IsEmpty)
        {
            var amp = pairs.IndexOf('&');
            var part = amp < 0 ? pairs : pairs[..amp];
            pairs = amp < 0 ? default : pairs[(amp + 1)..];

            if (!first)
            {
                sb.Append('&');
            }

            first = false;
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                sb.Append(part);
                continue;
            }

            var rawName = part[..eq];
            sb.Append(rawName).Append('=');
            if (names.IsMatch(DecodeName(rawName)))
            {
                sb.Append(Mask);
            }
            else
            {
                sb.Append(MaskText(part[(eq + 1)..].ToString()));
            }
        }

        return sb.ToString();
    }

    private static string DecodeName(ReadOnlySpan<char> raw)
    {
        var text = raw.ToString();
        try
        {
            return Uri.UnescapeDataString(text.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    // ------------------------------------------------------------------ XML

    private string MaskXml(string xml)
    {
        try
        {
            return XmlLeaf.Replace(xml, m =>
                _fields.IsMatch(m.Groups["n"].Value)
                    ? $"<{m.Groups["n"].Value}{m.Groups["a"].Value}>{Mask}</{m.Groups["n"].Value}>"
                    : m.Value);
        }
        catch (RegexMatchTimeoutException)
        {
            return Mask;
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static string MediaTypeOf(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        var semi = contentType.IndexOf(';');
        return (semi < 0 ? contentType : contentType[..semi]).Trim().ToLowerInvariant();
    }

    private static Encoding EncodingOf(string? contentType)
    {
        if (contentType is null)
        {
            return Encoding.UTF8;
        }

        var idx = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return Encoding.UTF8;
        }

        var name = contentType[(idx + 8)..].Split(';')[0].Trim().Trim('"');
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static bool IsJson(string media) =>
        media is "application/json" or "text/json" || media.EndsWith("+json", StringComparison.Ordinal);

    private static Regex[] CompilePatterns(IEnumerable<string> patterns, ILogger? logger)
    {
        var compiled = new List<Regex>();
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            try
            {
                compiled.Add(new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                logger?.LogWarning(ex, "Ignoring invalid ApiPulse mask pattern '{Pattern}'. Patterns must be valid non-backtracking regular expressions (no lookarounds or backreferences).", pattern);
            }
        }

        return compiled.ToArray();
    }
}
