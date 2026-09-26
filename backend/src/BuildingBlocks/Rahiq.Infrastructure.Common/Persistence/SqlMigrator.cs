using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Rahiq.Infrastructure.Common.Persistence;

/// <summary>
/// Applies the reviewed SQL files in <c>backend/db/migrations</c> in name order, once each, under a
/// PostgreSQL advisory lock so two instances starting together cannot race (ADR-012).
/// Deploys run it before the new version starts: <c>dotnet Rahiq.Api.dll migrate</c>.
/// </summary>
public sealed partial class SqlMigrator(NpgsqlDataSource dataSource, ILogger<SqlMigrator> logger)
{
    private const long AdvisoryLockKey = 0x5241_4849_51; // "RAHIQ"

    public async Task<IReadOnlyList<string>> MigrateAsync(Assembly scriptsAssembly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scriptsAssembly);
        var scripts = scriptsAssembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && n.Contains(".migrations.", StringComparison.OrdinalIgnoreCase))
            .Select(n => (Resource: n, Name: ScriptName(n)))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await Execute(connection, $"SELECT pg_advisory_lock({AdvisoryLockKey});", cancellationToken);

        try
        {
            await Execute(connection, """
                CREATE SCHEMA IF NOT EXISTS infra;
                CREATE TABLE IF NOT EXISTS infra.schema_migrations (
                  name text PRIMARY KEY,
                  applied_at timestamptz NOT NULL DEFAULT now()
                );
                """, cancellationToken);

            var applied = new HashSet<string>(StringComparer.Ordinal);
            await using (var cmd = new NpgsqlCommand("SELECT name FROM infra.schema_migrations", connection))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    applied.Add(reader.GetString(0));
                }
            }

            var newlyApplied = new List<string>();
            foreach (var (resource, name) in scripts.Where(s => !applied.Contains(s.Name)))
            {
                await using var stream = scriptsAssembly.GetManifestResourceStream(resource)!;
                using var streamReader = new StreamReader(stream);
                var sql = await streamReader.ReadToEndAsync(cancellationToken);

                await using var tx = await connection.BeginTransactionAsync(cancellationToken);
                await using (var cmd = new NpgsqlCommand(sql, connection, tx))
                {
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var record = new NpgsqlCommand("INSERT INTO infra.schema_migrations (name) VALUES (@name)", connection, tx))
                {
                    record.Parameters.AddWithValue("name", name);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
                LogApplied(logger, name);
                newlyApplied.Add(name);
            }

            if (newlyApplied.Count > 0)
            {
                // New extensions or types (citext) must be visible to every pooled connection of this process.
                await connection.ReloadTypesAsync(cancellationToken);
            }

            return newlyApplied;
        }
        finally
        {
            await Execute(connection, $"SELECT pg_advisory_unlock({AdvisoryLockKey});", CancellationToken.None);
        }
    }

    /// <summary>Development convenience: creates the database when it does not exist yet.</summary>
    public static async Task EnsureDatabaseAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var database = target.Database ?? throw new InvalidOperationException("The connection string has no database.");
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", database);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null)
        {
            await Execute(connection, $"CREATE DATABASE \"{database.Replace("\"", string.Empty, StringComparison.Ordinal)}\"", cancellationToken);
        }
    }

    /// <summary>Development only (<c>reset</c> command): drops the database so it can be rebuilt from migrations.</summary>
    public static async Task DropDatabaseAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await Execute(connection, $"DROP DATABASE IF EXISTS \"{database.Replace("\"", string.Empty, StringComparison.Ordinal)}\" WITH (FORCE)", cancellationToken);
    }

    private static string ScriptName(string resource)
    {
        // Rahiq.Api.db.migrations.0001_initial.sql -> 0001_initial.sql
        var marker = resource.IndexOf(".migrations.", StringComparison.OrdinalIgnoreCase);
        return resource[(marker + ".migrations.".Length)..];
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied migration {Name}")]
    private static partial void LogApplied(ILogger logger, string name);
}
