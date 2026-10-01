using System.Globalization;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.Core.Tests.Contracts.Sqlite;
using Formbase.Sqlite;

namespace Formbase.Core.Tests.Projection;

/// <summary>
/// Bringing a projection forward must leave what a rebuild would: the same rows, the same recorded
/// skips in the same order. These drive one projector through seeded random histories — new documents,
/// records corrected, retired and brought back, documents that cannot be mapped, values that empty a
/// field — projecting at random points, and after every projection compare its table and skips with a
/// rebuild of the same raw store from nothing. The seeds are fixed so a failure replays.
/// </summary>
public sealed class IncrementalProjectionTests : IDisposable
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private const string Table = "qc";

    private readonly List<TemporarySqliteFile> _files = [];

    private static FormTypeHints Hints(params FieldHint[] extra) => new(Qc, Table,
    [
        new FieldHint("lot", ColumnType.Text, Nullable: false),
        new FieldHint("qty", ColumnType.Integer, Nullable: true),
        .. extra,
    ]);

    private sealed record Stores(IRawStore Raw, IProjectionStore Store, IProjectionState State);

    private Stores InMemory() => new(new InMemoryRawStore(), new InMemoryProjectionStore(), new InMemoryProjectionState());

    private Stores Sqlite()
    {
        var file = new TemporarySqliteFile();
        _files.Add(file);
        return new(new SqliteRawStore(file.Database), new SqliteProjectionStore(file.Database), new SqliteProjectionState(file.Database));
    }

    public static TheoryData<string, int> Histories()
    {
        var data = new TheoryData<string, int>();
        foreach (var backend in new[] { "in-memory", "sqlite" })
        {
            foreach (var seed in new[] { 1, 2, 3, 5, 8, 13, 21, 34 })
            {
                data.Add(backend, seed);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Histories))]
    public async Task Every_projection_along_a_random_history_equals_a_rebuild(string backend, int seed)
    {
        var stores = backend == "sqlite" ? Sqlite() : InMemory();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(Hints());
        var projector = new Projector(stores.Raw, new HintSchemaProposer(hints), stores.Store, stores.State);
        var random = new Random(seed);
        var keys = Enumerable.Range(0, 6).Select(i => RecordKey.Create($"k{i}")).ToArray();
        var modes = new List<ProjectionMode>();

        for (var step = 0; step < 120; step++)
        {
            var roll = random.Next(100);
            if (roll < 12)
            {
                modes.Add((await projector.ProjectAsync(Qc, TestContext.Current.CancellationToken)).Mode);
                await AssertEqualsRebuildAsync(stores, hints, $"{backend} seed {seed} step {step}");
                continue;
            }

            var key = roll < 30 ? (RecordKey?)null : keys[random.Next(keys.Length)];
            if (key is { } retired && roll >= 92)
            {
                await stores.Raw.RetireAsync(Qc, DocumentId.New(), retired, TestContext.Current.CancellationToken);
                continue;
            }

            await stores.Raw.AppendAsync(Qc, DocumentId.New(), DocumentBody.Parse(Body(random, step)), key, TestContext.Current.CancellationToken);
        }

        modes.Add((await projector.ProjectAsync(Qc, TestContext.Current.CancellationToken)).Mode);
        await AssertEqualsRebuildAsync(stores, hints, $"{backend} seed {seed} end");
        modes.Skip(1).Should().OnlyContain(m => m == ProjectionMode.Incremental, "nothing about the shape changed after the first run");
    }

    // Mostly good documents; some missing the required field (skipped), some with a value the optional
    // integer column cannot hold (the field is emptied and the row kept).
    private static string Body(Random random, int step) => random.Next(10) switch
    {
        0 => $$"""{"qty":{{step}}}""",
        1 => $$"""{"lot":"L-{{step}}","qty":"many"}""",
        _ => $$"""{"lot":"L-{{step}}","qty":{{step}}}""",
    };

    [Fact]
    public async Task A_changed_declaration_rebuilds_and_the_runs_after_it_bring_it_forward()
    {
        var stores = InMemory();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(Hints());
        var projector = new Projector(stores.Raw, new HintSchemaProposer(hints), stores.Store, stores.State);
        await Append(stores, """{"lot":"L-1","qty":1,"note":"a"}""");
        await projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        hints.Declare(Hints(new FieldHint("note", ColumnType.Text, Nullable: true)));
        await Append(stores, """{"lot":"L-2","qty":2,"note":"b"}""");
        var redeclared = await projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);
        await Append(stores, """{"lot":"L-3","qty":3,"note":"c"}""");
        var next = await projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        redeclared.Mode.Should().Be(ProjectionMode.Rebuild);
        next.Mode.Should().Be(ProjectionMode.Incremental);
        await AssertEqualsRebuildAsync(stores, hints, "after redeclaring");
    }

    [Fact]
    public async Task A_stamp_whose_skips_carry_no_keys_rebuilds()
    {
        var stores = InMemory();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(Hints());
        var projector = new Projector(stores.Raw, new HintSchemaProposer(hints), stores.Store, stores.State);
        await Append(stores, """{"lot":"L-1"}""");
        await projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);
        var recorded = (await stores.State.GetAsync(Qc, TestContext.Current.CancellationToken))!;
        // What a state written before skips carried keys reads back as.
        await stores.State.SetProjectedAsync(Qc, recorded with { SkipsKeyed = false }, [], [], TestContext.Current.CancellationToken);

        (await projector.ProjectAsync(Qc, TestContext.Current.CancellationToken)).Mode.Should().Be(ProjectionMode.Rebuild);
        (await stores.State.GetAsync(Qc, TestContext.Current.CancellationToken))!.SkipsKeyed.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_incremental_run_leaves_the_table_unverified_and_the_next_run_rebuilds_it()
    {
        var stores = InMemory();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(Hints());
        var failing = new FailingReplaceStore(stores.Store);
        var projector = new Projector(stores.Raw, new HintSchemaProposer(hints), failing, stores.State);
        var key = RecordKey.Create("k");
        await Append(stores, """{"lot":"L-1"}""", key);
        await projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);
        await Append(stores, """{"lot":"L-1 corrected"}""", key);

        failing.Fail = true;
        await FluentActions.Awaiting(() => projector.ProjectAsync(Qc)).Should().ThrowAsync<InvalidOperationException>();
        failing.Fail = false;

        (await stores.State.GetAsync(Qc, TestContext.Current.CancellationToken))!.Verified.Should().BeFalse(
            "the table holds part of a run, and a stamp still claiming it fresh would serve it");
        (await projector.ProjectAsync(Qc, TestContext.Current.CancellationToken)).Mode.Should().Be(ProjectionMode.Rebuild);
        await AssertEqualsRebuildAsync(stores, hints, "after recovering");
    }

    [Fact]
    public async Task A_run_that_finds_the_stamp_moved_under_it_leaves_the_table_to_a_rebuild()
    {
        var stores = InMemory();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(Hints());
        var racing = new RacingState(stores.State);
        var projector = new Projector(stores.Raw, new HintSchemaProposer(hints), stores.Store, racing);
        await Append(stores, """{"lot":"L-1"}""");
        await projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);
        await Append(stores, """{"lot":"L-2"}""");

        // Another process brings the projection forward between this run's read and its write.
        racing.MoveStampBeforeNextDelta = true;
        await FluentActions.Awaiting(() => projector.ProjectAsync(Qc)).Should().ThrowAsync<InvalidOperationException>();

        (await stores.State.GetAsync(Qc, TestContext.Current.CancellationToken))!.Verified.Should().BeFalse();
        (await projector.ProjectAsync(Qc, TestContext.Current.CancellationToken)).Mode.Should().Be(ProjectionMode.Rebuild);
        await AssertEqualsRebuildAsync(stores, hints, "after the race");
    }

    [Fact]
    public async Task Runs_of_one_form_type_in_one_process_do_not_overlap()
    {
        var stores = InMemory();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(Hints());
        var projector = new Projector(stores.Raw, new HintSchemaProposer(hints), stores.Store, stores.State);
        await Append(stores, """{"lot":"L-0"}""");
        await projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);
        for (var n = 1; n <= 20; n++)
        {
            await Append(stores, $$"""{"lot":"L-{{n}}"}""");
        }

        var runs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => projector.ProjectAsync(Qc), TestContext.Current.CancellationToken)));

        runs.Sum(r => r.Inserted).Should().Be(20, "each new document is read by exactly one of the runs");
        await AssertEqualsRebuildAsync(stores, hints, "after concurrent runs");
    }

    private static Task Append(Stores stores, string json, RecordKey? key = null) =>
        stores.Raw.AppendAsync(Qc, DocumentId.New(), DocumentBody.Parse(json), key, TestContext.Current.CancellationToken);

    /// <summary>The table and both skip lists equal a rebuild of the same raw store from nothing.</summary>
    private static async Task AssertEqualsRebuildAsync(Stores stores, InMemoryFieldHintSource hints, string because)
    {
        var reference = new Stores(stores.Raw, new InMemoryProjectionStore(), new InMemoryProjectionState());
        var rebuilt = await new Projector(reference.Raw, new HintSchemaProposer(hints), reference.Store, reference.State).ProjectAsync(Qc, TestContext.Current.CancellationToken);
        rebuilt.Mode.Should().Be(ProjectionMode.Rebuild);

        (await RowsAsync(stores.Store)).Should().BeEquivalentTo(await RowsAsync(reference.Store), because);
        (await stores.State.GetSkipsAsync(Qc, TestContext.Current.CancellationToken)).Should().Equal(
            await reference.State.GetSkipsAsync(Qc, TestContext.Current.CancellationToken), because);
        (await stores.State.GetFieldSkipsAsync(Qc, TestContext.Current.CancellationToken)).Should().Equal(
            await reference.State.GetFieldSkipsAsync(Qc, TestContext.Current.CancellationToken), because);
        (await stores.State.GetAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(
            await reference.State.GetAsync(Qc, TestContext.Current.CancellationToken), because);
    }

    // Each row as "column=value" pairs in column order, values in the invariant culture — a set of rows
    // compared regardless of the order a store returns them in or the CLR type it reads a value back as.
    private static async Task<List<string>> RowsAsync(IProjectionStore store) =>
        (await store.QueryAsync(Table, new QuerySpec(Limit: 100_000), TestContext.Current.CancellationToken))
            .Select(row => string.Join("; ", row.OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => $"{c.Key}={Convert.ToString(c.Value, CultureInfo.InvariantCulture)}")))
            .Order(StringComparer.Ordinal)
            .ToList();

    private sealed class FailingReplaceStore(IProjectionStore inner) : IProjectionStore
    {
        public bool Fail { get; set; }

        public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default) => inner.TableExistsAsync(tableName, cancellationToken);
        public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default) => inner.DropTableAsync(tableName, cancellationToken);
        public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default) => inner.CreateTableAsync(schema, cancellationToken);
        public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) => inner.BulkInsertAsync(tableName, rows, cancellationToken);

        public async Task<int> ReplaceRowsAsync(string tableName, IReadOnlyCollection<RecordKey> removeKeys, IReadOnlyCollection<DocumentId> removeDocuments, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
        {
            if (!Fail)
            {
                return await inner.ReplaceRowsAsync(tableName, removeKeys, removeDocuments, rows, cancellationToken);
            }

            // Half of the work done, as a store without transactions can leave it: the old rows gone,
            // the new ones not there.
            await inner.ReplaceRowsAsync(tableName, removeKeys, removeDocuments, [], cancellationToken);
            throw new InvalidOperationException("the store went away mid-run");
        }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default) => inner.QueryAsync(tableName, spec, cancellationToken);
        public Task<IReadOnlyList<AggregateGroup>> AggregateAsync(string tableName, AggregateSpec spec, CancellationToken cancellationToken = default) => inner.AggregateAsync(tableName, spec, cancellationToken);
    }

    private sealed class RacingState(IProjectionState inner) : IProjectionState
    {
        public bool MoveStampBeforeNextDelta { get; set; }

        public Task<ProjectionStamp?> GetAsync(FormTypeRef type, CancellationToken cancellationToken = default) => inner.GetAsync(type, cancellationToken);
        public Task SetProjectedAsync(FormTypeRef type, ProjectionStamp stamp, IReadOnlyList<ProjectionSkip> skips, IReadOnlyList<ProjectionFieldSkip> fieldSkips, CancellationToken cancellationToken = default)
            => inner.SetProjectedAsync(type, stamp, skips, fieldSkips, cancellationToken);

        public async Task<bool> ApplyProjectedDeltaAsync(FormTypeRef type, Watermark expectedWatermark, ProjectionStamp stamp, IReadOnlyCollection<RecordKey> withdrawnKeys, IReadOnlyCollection<DocumentId> withdrawnDocuments, IReadOnlyList<ProjectionSkip> addedSkips, IReadOnlyList<ProjectionFieldSkip> addedFieldSkips, CancellationToken cancellationToken = default)
        {
            if (MoveStampBeforeNextDelta)
            {
                MoveStampBeforeNextDelta = false;
                var current = (await inner.GetAsync(type, cancellationToken))!;
                await inner.ApplyProjectedDeltaAsync(type, current.Watermark, current with { Watermark = stamp.Watermark }, [], [], [], [], cancellationToken);
            }

            return await inner.ApplyProjectedDeltaAsync(type, expectedWatermark, stamp, withdrawnKeys, withdrawnDocuments, addedSkips, addedFieldSkips, cancellationToken);
        }

        public Task<IReadOnlyList<ProjectionSkip>> GetSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default) => inner.GetSkipsAsync(type, cancellationToken);
        public Task<IReadOnlyList<ProjectionFieldSkip>> GetFieldSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default) => inner.GetFieldSkipsAsync(type, cancellationToken);
        public Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default) => inner.ClearAsync(type, cancellationToken);
        public Task MarkUnverifiedAsync(FormTypeRef type, CancellationToken cancellationToken = default) => inner.MarkUnverifiedAsync(type, cancellationToken);
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            file.Dispose();
        }
    }
}
