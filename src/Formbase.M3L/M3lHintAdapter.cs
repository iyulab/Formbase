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

        foreach (var relation in model.Sections.Relations)
        {
            gaps.Add(new VocabularyGap(model.Name, null, VocabularyGapKind.Relation,
                $"relations: {relation}",
                "A declared relation has no slot in FormTypeHints — Formology rule 3/4 demand."));
        }

        var fields = new List<FieldHint>();
        foreach (var field in model.Fields)
        {
            AdaptField(model, field, fields, gaps);
        }

        return new FormTypeHints(
            FormTypeRef.Create(ToSnake(model.Name)),
            ToSnake(model.Name),
            fields);
    }

    private static void AdaptField(ModelNode model, FieldNode field, List<FieldHint> fields, List<VocabularyGap> gaps)
    {
        if (field.Kind is FieldKind.Lookup or FieldKind.Rollup or FieldKind.Computed)
        {
            // Derived fields are not raw extraction keys: a lookup is "true now" across a
            // relation, a rollup aggregates children — both presuppose the relation vocabulary.
            gaps.Add(new VocabularyGap(model.Name, field.Name, VocabularyGapKind.Derived,
                field.Kind.ToString().ToLowerInvariant(),
                "Derived fields cannot be declared as hints; they presuppose relations the vocabulary lacks."));
            return;
        }

        if (field.Label is not null && field.Label != field.Name)
        {
            // FieldHint.Name is simultaneously the extraction key and the display/column name —
            // the identity/display split M3L carries has nowhere to go.
            gaps.Add(new VocabularyGap(model.Name, field.Name, VocabularyGapKind.IdentityDisplay,
                $"label \"{field.Label}\"",
                "FieldHint.Name is both extraction key and display name; the label drops."));
        }

        if (field.Binding is not null)
        {
            gaps.Add(new VocabularyGap(model.Name, field.Name, VocabularyGapKind.TimeBinding,
                $"binding {(field.Binding.IsHard ? "hard" : "soft")} → {field.Binding.Entity}.{field.Binding.Column}",
                "Reference-vs-attachment (true now vs fixed then) has no axis in FieldHint — Formology rule 5 demand."));
        }

        foreach (var attribute in field.Attributes)
        {
            switch (attribute.Name)
            {
                case "reference":
                    gaps.Add(new VocabularyGap(model.Name, field.Name, VocabularyGapKind.Relation,
                        $"@reference({FirstArg(attribute)})",
                        "The FK target entity drops; only the raw key column survives — Formology rule 4 demand."));
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

        fields.Add(new FieldHint(field.Name, MapType(model, field, gaps), field.Nullable));
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

    private static string FirstArg(FieldAttribute attribute)
        => attribute.Args is { Count: > 0 } ? attribute.Args[0].ToString() : "?";

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
