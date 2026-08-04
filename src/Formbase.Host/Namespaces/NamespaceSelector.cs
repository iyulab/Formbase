namespace Formbase.Host.Namespaces;

/// <summary>
/// Which data a request is addressed to. A host serves one namespace — the triple of connection,
/// schema and projection target it was composed with — and the selector is how a caller names it.
/// <para>
/// One host, one namespace, so the selection is degenerate today. It is on the surface anyway, and
/// enforced rather than accepted-and-ignored: a request naming a namespace this host does not serve
/// is refused. A selector that accepted anything would be the advertised boundary this project has
/// been burned by — and one added later would break every stored request written without it.
/// </para>
/// </summary>
internal sealed class NamespaceSelector
{
    /// <summary>
    /// RFC 6648 deprecates the <c>X-</c> convention for new headers, so the name carries the product
    /// rather than the prefix.
    /// </summary>
    public const string Header = "Formbase-Namespace";

    /// <summary>The name a host answers to when its configuration does not say otherwise.</summary>
    public const string Default = "default";

    public NamespaceSelector(string name) => Name = name;

    /// <summary>The single namespace this host serves.</summary>
    public string Name { get; }

    /// <summary>
    /// True when the request either names this host's namespace or names none at all. Omitting the
    /// header addresses the host's own namespace, which is what makes a single-namespace deployment
    /// usable without every caller knowing its name.
    /// </summary>
    public bool Accepts(string? requested) =>
        string.IsNullOrEmpty(requested) ||
        string.Equals(requested, Name, StringComparison.Ordinal);
}
