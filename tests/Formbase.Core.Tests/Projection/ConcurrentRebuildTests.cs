using System.Collections.Concurrent;
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
/// Two hosts rebuild the same form type over the same raw store, projection store and state at the same
/// time — the multi-host setup the README names. Each host is a <see cref="Projector"/> of its own (the
/// run gate is per process), and every run can be held at the step boundaries of a rebuild — after the
/// table is reset, after the rows are inserted — so each ordering of two rebuilds is driven
/// deterministically rather than hoped for.
/// <para>
/// The invariant is about what a reader may trust: <b>a verified stamp never sits over a table that
/// is not exactly one copy of the rows of the raw documents</b>. Whichever run loses the ordering must
/// leave the state saying so (cleared, so the next projection rebuilds) — never «fresh» over rows
/// doubled by the other run.
/// </para>
/// </summary>
public sealed class ConcurrentRebuildTests : IDisposable
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private const string Table = "qc";
    private const int Documents = 3;

    private readonly List<TemporarySqliteFile> _files = [];

    public static TheoryData<string> Backends() => ["in-memory", "sqlite"];

    /// <summary>Names the steps of a rebuild a run can be held at, and lets a test wait for and release them.</summary>
    private sealed class Choreography
    {
        private readonly ConcurrentDictionary<(string Run, string Step), TaskCompletionSource> _reached = new();
        private readonly ConcurrentDictionary<(string Run, string Step), TaskCompletionSource> _released = new();

        public void Hold(string run, string step)
        {
            _reached[(run, step)] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _released[(run, step)] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task Reached(string run, string step) => _reached[(run, step)].Task.WaitAsync(TimeSpan.FromSeconds(30));

        public void Release(string run, string step) => _released[(run, step)].SetResult();

        public async Task PassAsync(string run, string step)
        {
            if (_reached.TryGetValue((run, step), out var reached))
            {
                reached.SetResult();
                await _released[(run, step)].Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    private sealed class Host(string name, Projector projector)
    {
        public string Name { get; } = name;

        public Projector Projector { get; } = projector;

        public async Task<Exception?> RunAsync()
        {
            try
            {
                await Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                return ex;
            }
        }
    }

    private sealed class World
    {
        public required IRawStore Raw { get; init; }
        public required IProjectionStore Store { get; init; }
        public required IProjectionState State { get; init; }
        public required Func<(IRawStore Raw, IProjectionStore Store, IProjectionState State)> OpenHostConnection { get; init; }
        public Choreography Steps { get; } = new();
        public InMemoryFieldHintSource Hints { get; } = new();

        public Host NewHost(string name)
        {
            var (raw, store, state) = OpenHostConnection();
            return new Host(name, new Projector(raw, new HintSchemaProposer(Hints), new GatedStore(store, Steps, name), state));
        }
    }

    private World NewWorld(string backend)
    {
        World world;
        if (backend == "sqlite")
        {
            var file = new TemporarySqliteFile();
            _files.Add(file);

            // A host is its own set of connections over the same file, as two processes would be.
            world = new World
            {
                Raw = new SqliteRawStore(file.Database),
                Store = new SqliteProjectionStore(file.Database),
                State = new SqliteProjectionState(file.Database),
                OpenHostConnection = () => (new SqliteRawStore(file.Database), new SqliteProjectionStore(file.Database), new SqliteProjectionState(file.Database)),
            };
        }
        else
        {
            var raw = new InMemoryRawStore();
            var store = new InMemoryProjectionStore();
            var state = new InMemoryProjectionState();
            world = new World { Raw = raw, Store = store, State = state, OpenHostConnection = () => (raw, store, state) };
        }

        world.Hints.Declare(new FormTypeHints(Qc, Table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
        ]));
        return world;
    }

    private static async Task SeedAsync(World world)
    {
        for (var i = 0; i < Documents; i++)
        {
            await world.Raw.AppendAsync(Qc, DocumentId.New(), DocumentBody.Parse($$"""{"lot":"L{{i}}","qty":{{i}}}"""), cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private static async Task<List<string>> DocumentIdsAsync(World world)
    {
        var rows = await world.Store.QueryAsync(Table, new QuerySpec(Limit: 10_000), TestContext.Current.CancellationToken);
        return [.. rows.Select(r => Convert.ToString(r[ProjectionSystemColumns.DocumentId], System.Globalization.CultureInfo.InvariantCulture)!)];
    }

    /// <summary>The invariant, and that the next projection converges whatever the orderings left behind.</summary>
    private static async Task AssertNothingFalselyFreshAsync(World world)
    {
        var stamp = await world.State.GetAsync(Qc, TestContext.Current.CancellationToken);
        if (stamp is { Verified: true })
        {
            var ids = await DocumentIdsAsync(world);
            ids.Should().HaveCount(Documents, "a verified stamp over a table that is not exactly one copy of the documents reads wrong as right");
            ids.Distinct().Should().HaveCount(Documents);
        }

        var recovery = world.NewHost("recovery");
        (await recovery.RunAsync()).Should().BeNull("the next projection rebuilds whatever the race left in doubt");
        var after = await DocumentIdsAsync(world);
        after.Should().HaveCount(Documents);
        after.Distinct().Should().HaveCount(Documents);
        (await world.State.GetAsync(Qc, TestContext.Current.CancellationToken))!.Verified.Should().BeTrue();
    }

    // (a) A is held after it reset the table; B rebuilds completely; A resumes and inserts on top.
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task A_rebuild_resumed_after_another_host_finished_cannot_leave_a_fresh_stamp_over_doubled_rows(string backend)
    {
        var world = NewWorld(backend);
        await SeedAsync(world);
        var a = world.NewHost("A");
        var b = world.NewHost("B");
        world.Steps.Hold("A", "reset");

        var runA = Task.Run(a.RunAsync, TestContext.Current.CancellationToken);
        await world.Steps.Reached("A", "reset");
        (await b.RunAsync()).Should().BeNull();
        world.Steps.Release("A", "reset");
        var failureA = await runA;

        failureA.Should().NotBeNull("the run that lost the race says so instead of recording its rows as the projection");
        await AssertNothingFalselyFreshAsync(world);
    }

    // (b) Both reset, both insert; whichever finishes first must not make the other's rows look like its own.
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Two_rebuilds_that_both_reset_and_both_insert_leave_no_fresh_stamp_over_doubled_rows(string backend)
    {
        var world = NewWorld(backend);
        await SeedAsync(world);
        var a = world.NewHost("A");
        var b = world.NewHost("B");
        world.Steps.Hold("A", "reset");
        world.Steps.Hold("B", "reset");

        // B starts only once A's reset is done and held: the stores' reset is drop-then-create, which is
        // not atomic against a second reset of the same table, and this ordering is about what the two
        // runs leave behind, not about racing two resets.
        var runA = Task.Run(a.RunAsync, TestContext.Current.CancellationToken);
        await world.Steps.Reached("A", "reset");
        var runB = Task.Run(b.RunAsync, TestContext.Current.CancellationToken);
        await world.Steps.Reached("B", "reset");
        world.Steps.Release("A", "reset");
        await runA;
        world.Steps.Release("B", "reset");
        await runB;

        await AssertNothingFalselyFreshAsync(world);
    }

    // (c) A inserted its rows and is about to record them; B resets the table and rebuilds in between.
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task A_rebuild_that_inserted_before_another_host_rebuilt_cannot_record_a_fresh_stamp_over_the_other_hosts_table(string backend)
    {
        var world = NewWorld(backend);
        await SeedAsync(world);
        var a = world.NewHost("A");
        var b = world.NewHost("B");
        world.Steps.Hold("A", "insert");

        var runA = Task.Run(a.RunAsync, TestContext.Current.CancellationToken);
        await world.Steps.Reached("A", "insert");
        await b.RunAsync();
        world.Steps.Release("A", "insert");
        await runA;

        await AssertNothingFalselyFreshAsync(world);
    }

    // (c') A inserted and records its stamp while B is between its reset and its insert: A's stamp lands
    // over a table B just emptied, and B's insert then adds to A's rows.
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task A_rebuild_recording_while_another_host_is_between_reset_and_insert_leaves_no_fresh_stamp_over_the_wrong_rows(string backend)
    {
        var world = NewWorld(backend);
        await SeedAsync(world);
        var a = world.NewHost("A");
        var b = world.NewHost("B");
        world.Steps.Hold("A", "insert");
        world.Steps.Hold("B", "reset");

        var runA = Task.Run(a.RunAsync, TestContext.Current.CancellationToken);
        await world.Steps.Reached("A", "insert");
        var runB = Task.Run(b.RunAsync, TestContext.Current.CancellationToken);
        await world.Steps.Reached("B", "reset");
        world.Steps.Release("A", "insert");
        await runA;
        world.Steps.Release("B", "reset");
        await runB;

        await AssertNothingFalselyFreshAsync(world);
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            file.Dispose();
        }
    }

    private sealed class GatedStore(IProjectionStore inner, Choreography steps, string run) : IProjectionStore
    {
        public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default) => inner.TableExistsAsync(tableName, cancellationToken);
        public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default) => inner.DropTableAsync(tableName, cancellationToken);
        public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default) => inner.CreateTableAsync(schema, cancellationToken);

        public async Task ResetTableAsync(TableSchema schema, CancellationToken cancellationToken = default)
        {
            await inner.ResetTableAsync(schema, cancellationToken);
            await steps.PassAsync(run, "reset");
        }

        public async Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
        {
            var inserted = await inner.BulkInsertAsync(tableName, rows, cancellationToken);
            await steps.PassAsync(run, "insert");
            return inserted;
        }

        public Task<int> ReplaceRowsAsync(string tableName, IReadOnlyCollection<RecordKey> removeKeys, IReadOnlyCollection<DocumentId> removeDocuments, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
            => inner.ReplaceRowsAsync(tableName, removeKeys, removeDocuments, rows, cancellationToken);

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default) => inner.QueryAsync(tableName, spec, cancellationToken);
        public Task<IReadOnlyList<AggregateGroup>> AggregateAsync(string tableName, AggregateSpec spec, CancellationToken cancellationToken = default) => inner.AggregateAsync(tableName, spec, cancellationToken);
    }
}
