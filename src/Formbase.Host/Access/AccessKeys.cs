using System.Security.Cryptography;
using System.Text;
using Formbase.Host.Composition;

namespace Formbase.Host.Access;

/// <summary>
/// The keys a caller may present to reach this host, read once from <c>Formbase:Access:Keys</c>.
/// <para>
/// A host is a service, not a directory of people: a key says which <em>service</em> may call, never
/// who a person is or what they may see, so it assumes nothing about what the host is used for.
/// None configured, the host answers everyone who can reach it — as it always has — and says so when
/// it starts. More than one may be configured so a key can be rotated without a moment in which the
/// old one is refused and the new one not yet deployed.
/// </para>
/// </summary>
internal sealed class AccessKeys
{
    /// <summary>
    /// The shortest key accepted. A key a person could type from memory is one an attacker can guess;
    /// 32 characters is what a generated key of 128 bits or more always reaches in hex or base64.
    /// </summary>
    internal const int MinimumLength = 32;

    private readonly byte[][] _digests;

    private AccessKeys(byte[][] digests) => _digests = digests;

    /// <summary>Whether a key is required at all — false when none is configured.</summary>
    public bool Required => _digests.Length > 0;

    /// <exception cref="HostConfigurationException">A configured key is shorter than <see cref="MinimumLength"/>.</exception>
    public static AccessKeys From(IConfiguration configuration)
    {
        var keys = configuration.GetSection("Formbase:Access:Keys").GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .ToArray();

        if (keys.Any(key => key.Length < MinimumLength))
        {
            // The key itself is not echoed: the message is written to a log.
            throw new HostConfigurationException(
                $"Formbase:Access:Keys holds a key shorter than {MinimumLength} characters; generate a longer one.");
        }

        return new AccessKeys(keys.Select(Digest).ToArray());
    }

    /// <summary>
    /// Whether <paramref name="presented"/> is one of the keys. Compared as fixed-length digests in
    /// constant time, so neither the length nor the first differing character of a key can be learned
    /// from how long a refusal takes.
    /// </summary>
    public bool Accepts(string presented)
    {
        var digest = Digest(presented);
        var accepted = false;
        foreach (var key in _digests)
        {
            accepted |= CryptographicOperations.FixedTimeEquals(digest, key);
        }

        return accepted;
    }

    private static byte[] Digest(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
