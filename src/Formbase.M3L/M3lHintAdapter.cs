using System.Globalization;
using System.Text;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using M3L.Native;

namespace Formbase.M3L;

/// <summary>
/// Maps parsed M3L models onto today's flat declaration vocabulary
/// (<see cref="FormTypeHints"/> / <see cref="FieldHint"/>) and records everything that drops on
/// the way as <see cref="VocabularyGap"/> entries. The adapter is deliberately lossy — its
/// purpose is to make the loss visible and countable, not to work around it: the generated gap
/// list is the measured demand the richer vocabulary design (§10) starts from.
/// </summary>
public static class M3lHintAdapter
{
    /// <summary>The result of adapting one M3L document: what fit, and what did not.</summary>
    public sealed record Result(
        IReadOnlyList<FormTypeHints> Hints,
        IReadOnlyList<VocabularyGap> Gaps);

    public static Result Adapt(string m3lContent, string filename = "form.m3l.md")
    {
        var parsed = M3lNative.ParseToAst(m3lContent, filename)
            ?? throw new InvalidOperationException("The M3L parser returned no result.");
        if (!parsed.Success || parsed.Data is null)
        {
            throw new InvalidOperationException($"M3L parsing failed: {parsed.Error ?? "(no message)"}");
        }

        var hints = new List<FormTypeHints>();
        var gaps = new List<VocabularyGap>();
        foreach (var model in parsed.Data.Models.Where(m => m.Type == ModelType.Model))
        {
            hints.Add(AdaptModel(model, gaps));
        }

        return new Result(hints, gaps);
    }

    private static FormTypeHints AdaptModel(ModelNode model, List<VocabularyGap> gaps)
    {
        if (model.Inherits.Count > 0)
        {
            gaps.Add(new VocabularyGap(model.Name, null, VocabularyGapKind.Unresolved,
                $"inherits {string.Join(", ", model.Inherits)}",
                "Inherited fields are not resolved by the spike; the flat vocabulary has no composition either."));
        }

        // The free-form Relations section is a distinct M3L construct the spike does not decode
        // structurally (its entries are opaque here) — still a measured gap. Field-level @reference,
        // which is structured, is filled below.
        foreach (var relation in model.Sections.Relations)
        {
            gaps.Add(new VocabularyGap(model.Name, null, VocabularyGapKind.Unresolved,
                $"relations section: {relation}",
                "The free-form Relations section is not decoded by the spike; use field @reference for structured relations."));
        }

        var fields = new List<FieldHint>();
        var relations = new List<RelationHint>();
        foreach (var field in model.Fields)
        {
            AdaptField(model, field, fields, relations, gaps);
        }

        return new FormTypeHints(
            FormTypeRef.Create(ToSnake(model.Name)),
            ToSnake(model.Name),
            fields,
            relations.Count > 0 ? relations : null);
    }

    private static void AdaptField(ModelNode model, FieldNode field, List<FieldHint> fields, List<RelationHint> relations, List<VocabularyGap> gaps)
    {
        if (field.Kind is FieldKind.Lookup or FieldKind.Rollup or FieldKind.Computed)
        {
            // Derived fields are not raw extraction keys: a lookup is "true now" across a
            // relation, a rollup aggregates children. These are a query-layer concern the
            // declaration vocabulary deliberately does not carry — a genuine remaining gap.
            gaps.Add(new VocabularyGap(model.Name, field.Name, VocabularyGapKind.Derived,
                field.Kind.ToString().ToLowerInvariant(),
                "Derived fields are query-layer, not stored declarations; the vocabulary does not carry them."));
            return;
        }

        if (field.Label is not null && field.Label != field.Name)
        {
            // The vocabulary's SourceKey/Name split is extraction-key-vs-column-name, both machine
            // names; M3L's label is a human display string with no home in it — still a gap.
            gaps.Add(new VocabularyGap(model.Name, field.Name, VocabularyGapKind.IdentityDisplay,
                $"label \"{field.Label}\"",
                "A human display label has no slot; SourceKey/Name are machine names, not labels."));
        }

        // Time binding — FILLED: hard binding fixes the value then (Snapshot), soft reads true now
        // (Reference). The declared target rides along as an EntityRef.
        FieldBinding binding = FieldBinding.Stored;
        EntityRef? target = null;
        if (field.Binding is not null)
        {
            binding = field.Binding.IsHard ? FieldBinding.Snapshot : FieldBinding.Reference;
            target = new EntityRef(FormTypeRef.Create(ToSnake(field.Binding.Entity)), field.Binding.Column);
        }

        foreach (var attribute in field.Attributes)
        {
            switch (attribute.Name)
            {
                case "reference":
                    // Relation — FILLED: the FK column survives as a normal field (below) and the
                    // link is declared as a RelationHint. KeyField is this table's FK column.
                    relations.Add(new RelationHint(
                        field.Name,
                        RelationKind.Reference,
                        FormTypeRef.Create(ToSnake(RefTarget(attribute))),
                        field.Name));
                    break;
                case "unique" or "min" or "max" or "pk":
                    gaps.Add(new VocabularyGap(model.Name, field.Name, VocabularyGapKind.Constraint,
                        $"@{attribute.Name}", "Declared constraints have no hint slot."));
                    break;
                default:
                    break; // Other attributes (@generated, @searchable, ...) are host concerns, not vocabulary demand.
            }
        }

        if (field.EnumValues is { Count: > 0 })
        {
            gaps.Add(new VocabularyGap(model.Name, field.Name, VocabularyGapKind.Constraint,
                "inline enum", "Enum membership degrades to Text; the value set drops."));
        }

        fields.Add(new FieldHint(field.Name, MapType(model, field, gaps), field.Nullable, Binding: binding, Target: target));
    }

    private static ColumnType MapType(ModelNode model, FieldNode field, List<VocabularyGap> gaps)
    {
        if (field.Array)
        {
            return ColumnType.Jsonb;
        }

        switch (field.Type)
        {
            case "string" or "text" or "email" or "phone" or "url" or "enum":
                return ColumnType.Text;
            case "integer":
                return ColumnType.Integer;
            case "decimal" or "float" or "money":
                return ColumnType.Decimal;
            case "boolean":
                return ColumnType.Boolean;
            case "timestamp" or "date" or "datetime" or "time":
                return ColumnType.Timestamp;
            case "identifier" or "uuid":
                return ColumnType.Uuid;
            case "json" or "object":
                return ColumnType.Jsonb;
            case null:
                // A named type that is another model/enum in the document — degrade to Text.
                return ColumnType.Text;
            default:
                gaps.Add(new VocabularyGap(model.Name, field.Name, VocabularyGapKind.Unresolved,
                    $"type {field.Type}", "Unmapped M3L type; degraded to Text."));
                return ColumnType.Text;
        }
    }

    private static string RefTarget(FieldAttribute attribute)
    {
        if (attribute.Args is not { Count: > 0 })
        {
            return "unknown";
        }

        var arg = attribute.Args[0];
        return arg.ValueKind == System.Text.Json.JsonValueKind.String ? arg.GetString()! : arg.ToString();
    }

    private static string ToSnake(string name)
    {
        var result = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    result.Append('_');
                }

                result.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
}
