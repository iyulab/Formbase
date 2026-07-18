namespace Formbase.Core.Schema;

/// <summary>
/// A declared field for a form type — the input to hint-based schema proposal. This is the
/// (deliberately minimal) unit an input adapter such as M3L produces; the richer declaration
/// format is deferred (design §10).
/// </summary>
public sealed record FieldHint(string Name, ColumnType Type, bool Nullable = true);
