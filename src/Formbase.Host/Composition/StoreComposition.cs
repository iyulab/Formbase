using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Host.Declarations;

namespace Formbase.Host.Composition;

/// <summary>
/// Chooses what the engine's ports are backed by, from configuration.
/// <para>
/// The composition is the deployment decision this host exists to package: the engine itself is
/// indifferent to which store answers a port, and every profile below assembles the same engine.
/// So the only thing that varies is what is registered — and the only thing that can go wrong is a
/// profile asking for something the configuration did not supply.
/// </para>
/// <para>
/// A misconfigured durable profile <b>refuses to start</b>. Falling back to the in-process stores
/// would leave a deployment running, answering, and losing every document on restart — the failure
/// would surface as missing data long after the configuration that caused it.
/// </para>
/// </summary>
internal static class StoreComposition
{
    /// <summary>Configuration section this reads. Keys below are relative to it.</summary>
    public const string Section = "Formbase";

    public static IServiceCollection AddFormbaseStores(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        var profile = ReadProfile(section);

        services.AddSingleton(new StoreProfileSelection(profile));
        services.AddFormbaseCore();

        return profile switch
        {
            StoreProfile.InMemory => services.AddInMemoryStores(),
            StoreProfile.Durable => services.AddDurableStores(configuration, section),
            _ => throw new InvalidOperationException($"Unhandled store profile '{profile}'."),
        };
    }

    private static StoreProfile ReadProfile(IConfigurationSection section)
    {
        var configured = section["Store"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return StoreProfile.InMemory;
        }

        // A profile name the host does not know is a deployment that believes it configured
        // something. Answering with the default would run the wrong stores under the operator's
        // assumption that they had chosen.
        return Enum.TryParse<StoreProfile>(configured, ignoreCase: true, out var profile)
            ? profile
            : throw new InvalidOperationException(
                $"'{Section}:Store' is '{configured}', which is not a store profile. " +
                $"Use one of: {string.Join(", ", Enum.GetNames<StoreProfile>())}.");
    }

    private static IServiceCollection AddInMemoryStores(this IServiceCollection services)
    {
        // The concrete hint source stays resolvable: declaring is not on the port, so a host running
        // this profile has no other way to declare anything.
        services.AddSingleton<InMemoryFieldHintSource>();
        services.AddSingleton<IFieldHintSource>(sp => sp.GetRequiredService<InMemoryFieldHintSource>());
        services.AddSingleton<IRawStore, InMemoryRawStore>();
        services.AddSingleton<IProjectionStore, InMemoryProjectionStore>();
        services.AddSingleton<IProjectionState, InMemoryProjectionState>();
        services.AddSingleton<IDeclarationWriter, InMemoryDeclarationWriter>();
        return services;
    }

    private static IServiceCollection AddDurableStores(
        this IServiceCollection services,
        IConfiguration configuration,
        IConfigurationSection section)
    {
        var connectionString = Required(
            configuration.GetConnectionString("Formbase"),
            "ConnectionStrings:Formbase",
            "the PostgreSQL database holding the raw stream, projection state and declarations");

        // The three Postgres stores share one connection pool by design, and the adapter's
        // TryAdd means the first registration wins — so they are given the same string here rather
        // than each reading configuration for itself.
        var schema = section["Schema"] is { Length: > 0 } configured ? configured : "formbase";

        var morphDb = section.GetSection("MorphDb");
        var url = Required(
            Environment.GetEnvironmentVariable("FORMBASE_MORPHDB_URL") ?? morphDb["Url"],
            $"{Section}:MorphDb:Url",
            "the MorphDB service holding the projected tables");

        var projectId = RequiredProjectId(morphDb["ProjectId"]);

        services.AddPostgresRawStore(connectionString, schema);
        services.AddPostgresProjectionState(connectionString, schema);
        services.AddPostgresFieldHints(connectionString, schema);
        services.AddMorphDbProjectionStore(url, projectId);

        // The concrete PostgresFieldHintSource is already resolvable — AddPostgresFieldHints
        // registers it and points IFieldHintSource at it. Registering it again here, by casting the
        // interface back, made the two resolve each other: a cycle the container follows until the
        // stack runs out, which ends the process rather than throwing.
        services.AddSingleton<IDeclarationWriter, PostgresDeclarationWriter>();
        return services;
    }

    private static string Required(string? value, string key, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                $"The durable store profile needs '{key}' — {what}. " +
                "Set it, or run the in-memory profile deliberately by leaving " +
                $"'{Section}:Store' unset.")
            : value;

    private static Guid RequiredProjectId(string? value)
    {
        var raw = Required(value, $"{Section}:MorphDb:ProjectId", "the MorphDB project the projected tables live in");

        // MorphDB scopes every schema and data request to a project, and provisioning one is the
        // operator's job — the engine never administers MorphDB. A missing or unparseable id would
        // otherwise surface as MISSING_PROJECT on the first projection, long after startup.
        return Guid.TryParse(raw, out var projectId) && projectId != Guid.Empty
            ? projectId
            : throw new InvalidOperationException(
                $"'{Section}:MorphDb:ProjectId' is '{raw}', which is not the id of a provisioned " +
                "MorphDB project. Create one with POST /api/projects and use the id it returns.");
    }
}
