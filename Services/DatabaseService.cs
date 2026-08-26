using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using StreamBox.Models;

namespace StreamBox.Services;

public sealed class DatabaseService
{
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public string DatabaseDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamBox");

    public string DatabasePath => Path.Combine(DatabaseDirectory, "streambox.db");

    private string ConnectionString => $"Data Source={DatabasePath};Cache=Shared";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Log.Info("DB init started");

            try
            {
                Directory.CreateDirectory(DatabaseDirectory);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to create database directory", ex);
                throw;
            }

            await WithRecoveryAsync(async () =>
            {
                await using var connection = await OpenConnectionAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS channels (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL,
                        group_title TEXT NOT NULL,
                        logo_url TEXT NULL,
                        stream_url TEXT NOT NULL,
                        user_agent TEXT NULL,
                        extra_headers_json TEXT NULL,
                        sort_order INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS settings (
                        key TEXT PRIMARY KEY,
                        value TEXT NULL
                    );
                    """;

                await command.ExecuteNonQueryAsync(cancellationToken);
            });

            _initialized = true;
            Log.Info("DB init completed");
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<IReadOnlyList<Channel>> LoadChannelsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        return await WithRecoveryAsync(async () =>
        {
            var channels = new List<Channel>();

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, name, group_title, logo_url, stream_url, user_agent, extra_headers_json, sort_order
                FROM channels
                ORDER BY sort_order, name;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                channels.Add(new Channel
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    GroupTitle = reader.GetString(2),
                    LogoUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                    StreamUrl = reader.GetString(4),
                    UserAgent = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ExtraHeaders = reader.IsDBNull(6)
                        ? null
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6)),
                    SortOrder = reader.GetInt32(7)
                });
            }

            return (IReadOnlyList<Channel>)channels;
        });
    }

    public async Task SaveChannelsAsync(IReadOnlyList<Channel> channels, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await WithRecoveryAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            using var _ = transaction as IDisposable;

            await using (var clearCommand = connection.CreateCommand())
            {
                clearCommand.Transaction = (SqliteTransaction)transaction;
                clearCommand.CommandText = "DELETE FROM channels;";
                await clearCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            for (var i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                await using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = (SqliteTransaction)transaction;
                insertCommand.CommandText =
                    """
                    INSERT INTO channels
                    (name, group_title, logo_url, stream_url, user_agent, extra_headers_json, sort_order)
                    VALUES
                    ($name, $group, $logo, $stream, $userAgent, $headers, $sortOrder);
                    """;
                insertCommand.Parameters.AddWithValue("$name", channel.Name);
                insertCommand.Parameters.AddWithValue("$group", channel.GroupTitle);
                insertCommand.Parameters.AddWithValue("$logo", (object?)channel.LogoUrl ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("$stream", channel.StreamUrl);
                insertCommand.Parameters.AddWithValue("$userAgent", (object?)channel.UserAgent ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue(
                    "$headers",
                    channel.ExtraHeaders is { Count: > 0 }
                        ? JsonSerializer.Serialize(channel.ExtraHeaders)
                        : DBNull.Value);
                insertCommand.Parameters.AddWithValue("$sortOrder", i);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        return await WithRecoveryAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result as string;
        });
    }

    public async Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await WithRecoveryAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO settings (key, value)
                VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        });
    }

    public async Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await WithRecoveryAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            await command.ExecuteNonQueryAsync(cancellationToken);
        });
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText =
            """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            """;
        await pragmaCommand.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    private async Task WithRecoveryAsync(Func<Task> operation)
    {
        await WithRecoveryAsync(async () =>
        {
            await operation();
            return true;
        });
    }

    private async Task<T> WithRecoveryAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 14)
        {
            Log.Warn("SQLite error 14 detected; attempting one-time recovery");
            await RecoverFromOpenDatabaseErrorAsync();

            try
            {
                return await operation();
            }
            catch (Exception retryEx)
            {
                Log.Error("SQLite recovery retry failed", retryEx);
                NativeDialog.ShowError(
                    "StreamBox Database Error",
                    "StreamBox could not recover its local database.\n\n" +
                    $"Database folder:\n{DatabaseDirectory}\n\n" +
                    "Please close the app and check file permissions.");
                throw;
            }
        }
    }

    private Task RecoverFromOpenDatabaseErrorAsync()
    {
        try
        {
            Directory.CreateDirectory(DatabaseDirectory);
            SafeDelete(DatabasePath);
            SafeDelete(DatabasePath + "-shm");
            SafeDelete(DatabasePath + "-wal");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to recover SQLite database files", ex);
            throw;
        }

        return Task.CompletedTask;
    }

    private static void SafeDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
