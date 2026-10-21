using System.Text.Json;

namespace Formbase.Core.Schema;

/// <summary>
/// Where in a document a field's value is read from. A key that starts with <c>/</c> is a JSON Pointer
/// (RFC 6901): <c>/commit/collection</c> reads the <c>collection</c> member of the <c>commit</c> object,
/// <c>/tags/0</c> the first item of the <c>tags</c> array, and <c>~1</c> · <c>~0</c> stand for a <c>/</c>
/// and a <c>~</c> inside a member name. Any other key is the name of one top-level member, read as
/// written — the pointer <c>/name</c> means the same, so a document whose fields sit at the top needs no
/// pointer. A top-level member whose name itself starts with <c>/</c> is reached by its pointer
/// (<c>/~1name</c>).
/// </summary>
/// <remarks>
/// Every reader of a document's fields resolves the key here, so the projection, a repeated section's
/// parent key and every store read the same key the same way.
/// </remarks>
public static class SourceKeys
{
    /// <summary>
    /// <paramref name="key"/> when it is null or a key a document can be read by.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The key is blank, or a JSON Pointer with a <c>~</c> not followed by <c>0</c> or <c>1</c>.
    /// </exception>
    public static string? Checked(string? key, string paramName)
    {
        if (key is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A source key, when given, must not be blank.", paramName);
        }

        if (IsPointer(key))
        {
            for (var i = 1; i < key.Length; i++)
            {
                if (key[i] == '~' && (i + 1 == key.Length || key[i + 1] is not ('0' or '1')))
                {
                    throw new ArgumentException(
                        $"The source key '{key}' is a JSON Pointer with a '~' at position {i} that is not followed by '0' or '1'. " +
                        "Write '~0' for a '~' and '~1' for a '/' inside a member name (RFC 6901).",
                        paramName);
                }
            }
        }

        return key;
    }

    /// <summary>
    /// The key that reads the top-level member named <paramref name="name"/> — the name itself, or its
    /// pointer when the name starts with <c>/</c> and would otherwise be read as one. A field with no source
    /// key is read by its own name this way.
    /// </summary>
    public static string ForMember(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return IsPointer(name)
            ? "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)
            : name;
    }

    /// <summary>Whether <paramref name="key"/> is a JSON Pointer rather than one top-level member name.</summary>
    public static bool IsPointer(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Length > 0 && key[0] == '/';
    }

    /// <summary>
    /// The value <paramref name="key"/> reads in <paramref name="root"/>, or false when the document does
    /// not reach that far — a member missing on the way, a value on the way that is not an object or an
    /// array, or an array index past the end. A present JSON <c>null</c> is found, not missing.
    /// </summary>
    public static bool TryResolve(JsonElement root, string key, out JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!IsPointer(key))
        {
            value = default;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out value);
        }

        var current = root;
        var rest = key.AsSpan(1);
        while (true)
        {
            var end = rest.IndexOf('/');
            var token = end < 0 ? rest : rest[..end];
            if (!TryStep(current, token, out current))
            {
                value = default;
                return false;
            }

            if (end < 0)
            {
                value = current;
                return true;
            }

            rest = rest[(end + 1)..];
        }
    }

    private static bool TryStep(JsonElement current, ReadOnlySpan<char> token, out JsonElement next)
    {
        switch (current.ValueKind)
        {
            case JsonValueKind.Object:
                return token.Contains('~')
                    ? current.TryGetProperty(Unescape(token), out next)
                    : current.TryGetProperty(token, out next);

            case JsonValueKind.Array:
                // RFC 6901 §4: an array index is "0" or digits without a leading zero.
                if (token.Length > 0 && (token.Length == 1 || token[0] != '0')
                    && int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                    && index < current.GetArrayLength())
                {
                    next = current[index];
                    return true;
                }

                break;
        }

        next = default;
        return false;
    }

    private static string Unescape(ReadOnlySpan<char> token) =>
        // ~1 first, then ~0: "~01" is "~1" read literally, not "/".
        token.ToString().Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
