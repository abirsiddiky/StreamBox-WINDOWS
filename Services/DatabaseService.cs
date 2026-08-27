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
                        sort_order INTEGER NOT NULL,
                        playlist_id INTEGER NOT NULL DEFAULT 0
                    );

                    CREATE TABLE IF NOT EXISTS settings (
                        key TEXT PRIMARY KEY,
                        value TEXT NULL
                    );

                    CREATE TABLE IF NOT EXISTS playlists (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL,
                        source_kind TEXT NOT NULL DEFAULT 'Builtin',
                        source_value TEXT NULL,
                        is_enabled INTEGER NOT NULL DEFAULT 1,
                        sort_order INTEGER NOT NULL DEFAULT 0
                    );
                    """;

                await command.ExecuteNonQueryAsync(cancellationToken);

                // Migrate existing databases: add playlist_id column if missing
                await MigrateAddPlaylistIdAsync(connection, cancellationToken);

                // Migrate existing single-playlist data to playlists table
                await MigrateExistingPlaylistAsync(connection, cancellationToken);
            });

            _initialized = true;
            Log.Info("DB init completed");
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    /// <summary>
    /// Adds the playlist_id column to channels if the database predates multi-playlist support.
    /// </summary>
    private static async Task MigrateAddPlaylistIdAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "PRAGMA table_info(channels);";
        var hasPlaylistId = false;
        await using (var reader = await checkCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetString(1) == "playlist_id")
                {
                    hasPlaylistId = true;
                    break;
                }
            }
        }

        if (!hasPlaylistId)
        {
            Log.Info("Migrating channels table: adding playlist_id column");
            await using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE channels ADD COLUMN playlist_id INTEGER NOT NULL DEFAULT 0;";
            await alterCmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// If channels exist but no playlists do, creates a "Built-in" playlist and assigns
    /// all existing channels to it. Preserves the old custom source setting if any.
    /// </summary>
    private static async Task MigrateExistingPlaylistAsync(SqliteConnection connection, CancellationToken ct)
    {
        // Check if any playlists exist
        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM playlists;";
        var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));
        if (count > 0)
        {
            return;
        }

        // Check if any channels exist
        await using var chCountCmd = connection.CreateCommand();
        chCountCmd.CommandText = "SELECT COUNT(*) FROM channels;";
        var chCount = Convert.ToInt64(await chCountCmd.ExecuteScalarAsync(ct));
        if (chCount == 0)
        {
            return;
        }

        Log.Info("Migrating existing channels to Built-in playlist");

        // Read old source settings
        var sourceKind = "Default";
        var sourceValue = (string?)null;

        await using (var settingsCmd = connection.CreateCommand())
        {
            settingsCmd.CommandText = "SELECT value FROM settings WHERE key = 'playlist_source_kind';";
            var result = await settingsCmd.ExecuteScalarAsync(ct);
            if (result is string s) sourceKind = s;
        }

        await using (var settingsValCmd = connection.CreateCommand())
        {
            settingsValCmd.CommandText = "SELECT value FROM settings WHERE key = 'playlist_source_value';";
            var result = await settingsValCmd.ExecuteScalarAsync(ct);
            if (result is string s) sourceValue = s;
        }

        // Create "Built-in" playlist
        await using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText =
            """
            INSERT INTO playlists (name, source_kind, source_value, is_enabled, sort_order)
            VALUES ($name, $kind, $value, 1, 0);
            """;
        insertCmd.Parameters.AddWithValue("$name", "Built-in");
        insertCmd.Parameters.AddWithValue("$kind", sourceKind);
        insertCmd.Parameters.AddWithValue("$value", (object?)sourceValue ?? DBNull.Value);
        await insertCmd.ExecuteNonQueryAsync(ct);

        // Get the new playlist ID
        long playlistId;
        await using (var idCmd = connection.CreateCommand())
        {
            idCmd.CommandText = "SELECT last_insert_rowid();";
            playlistId = Convert.ToInt64(await idCmd.ExecuteScalarAsync(ct));
        }

        // Assign all existing channels to the Built-in playlist
        await using var updateCmd = connection.CreateCommand();
        updateCmd.CommandText = "UPDATE channels SET playlist_id = $pid;";
        updateCmd.Parameters.AddWithValue("$pid", playlistId);
        await updateCmd.ExecuteNonQueryAsync(ct);

        Log.Info($"Migration complete: {chCount} channels assigned to Built-in playlist (id={playlistId})");
    }

    // ── Channel operations ──

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
                SELECT id, name, group_title, logo_url, stream_url, user_agent, extra_headers_json, sort_order, playlist_id
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
                    SortOrder = reader.GetInt32(7),
                    PlaylistId = reader.GetInt64(8)
                });
            }

            return (IReadOnlyList<Channel>)channels;
        });
    }

    public async Task<IReadOnlyList<Channel>> LoadPlaylistChannelsAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        return await WithRecoveryAsync(async () =>
        {
            var channels = new List<Channel>();

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, name, group_title, logo_url, stream_url, user_agent, extra_headers_json, sort_order, playlist_id
                FROM channels
                WHERE playlist_id = $playlistId
                ORDER BY sort_order, name;
                """;
            command.Parameters.AddWithValue("$playlistId", playlistId);

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
                    SortOrder = reader.GetInt32(7),
                    PlaylistId = reader.GetInt64(8)
                });
            }

            return (IReadOnlyList<Channel>)channels;
        });
    }

    /// <summary>
    /// Saves channels scoped to a specific playlist (replaces only that playlist's channels).
    /// </summary>
    public async Task SavePlaylistChannelsAsync(long playlistId, IReadOnlyList<Channel> channels, CancellationToken cancellationToken = default)
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
                clearCommand.CommandText = "DELETE FROM channels WHERE playlist_id = $pid;";
                clearCommand.Parameters.AddWithValue("$pid", playlistId);
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
                    (name, group_title, logo_url, stream_url, user_agent, extra_headers_json, sort_order, playlist_id)
                    VALUES
                    ($name, $group, $logo, $stream, $userAgent, $headers, $sortOrder, $playlistId);
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
                insertCommand.Parameters.AddWithValue("$playlistId", playlistId);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>
    /// Legacy save: replaces ALL channels (used for backward compatibility).
    /// </summary>
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
                    (name, group_title, logo_url, stream_url, user_agent, extra_headers_json, sort_order, playlist_id)
                    VALUES
                    ($name, $group, $logo, $stream, $userAgent, $headers, $sortOrder, $playlistId);
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
                insertCommand.Parameters.AddWithValue("$playlistId", channel.PlaylistId);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }

    // ── Playlist operations ──

    public async Task<IReadOnlyList<Playlist>> LoadPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        return await WithRecoveryAsync(async () =>
        {
            var playlists = new List<Playlist>();

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, name, source_kind, source_value, is_enabled, sort_order
                FROM playlists
                ORDER BY sort_order, name;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                playlists.Add(new Playlist
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    SourceKind = reader.GetString(2),
                    SourceValue = reader.IsDBNull(3) ? null : reader.GetString(3),
                    IsEnabled = reader.GetInt64(4) != 0,
                    SortOrder = reader.GetInt32(5)
                });
            }

            return (IReadOnlyList<Playlist>)playlists;
        });
    }

    public async Task<long> InsertPlaylistAsync(Playlist playlist, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        return await WithRecoveryAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO playlists (name, source_kind, source_value, is_enabled, sort_order)
                VALUES ($name, $kind, $value, $enabled, $sortOrder);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$name", playlist.Name);
            command.Parameters.AddWithValue("$kind", playlist.SourceKind);
            command.Parameters.AddWithValue("$value", (object?)playlist.SourceValue ?? DBNull.Value);
            command.Parameters.AddWithValue("$enabled", playlist.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$sortOrder", playlist.SortOrder);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        });
    }

    public async Task UpdatePlaylistAsync(Playlist playlist, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await WithRecoveryAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE playlists
                SET name = $name, source_kind = $kind, source_value = $value, is_enabled = $enabled, sort_order = $sortOrder
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", playlist.Id);
            command.Parameters.AddWithValue("$name", playlist.Name);
            command.Parameters.AddWithValue("$kind", playlist.SourceKind);
            command.Parameters.AddWithValue("$value", (object?)playlist.SourceValue ?? DBNull.Value);
            command.Parameters.AddWithValue("$enabled", playlist.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$sortOrder", playlist.SortOrder);
            await command.ExecuteNonQueryAsync(cancellationToken);
        });
    }

    public async Task DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await WithRecoveryAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            using var _ = transaction as IDisposable;

            // Delete channels belonging to this playlist
            await using (var deleteChannelsCmd = connection.CreateCommand())
            {
                deleteChannelsCmd.Transaction = (SqliteTransaction)transaction;
                deleteChannelsCmd.CommandText = "DELETE FROM channels WHERE playlist_id = $pid;";
                deleteChannelsCmd.Parameters.AddWithValue("$pid", playlistId);
                await deleteChannelsCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Delete the playlist record
            await using (var deletePlaylistCmd = connection.CreateCommand())
            {
                deletePlaylistCmd.Transaction = (SqliteTransaction)transaction;
                deletePlaylistCmd.CommandText = "DELETE FROM playlists WHERE id = $id;";
                deletePlaylistCmd.Parameters.AddWithValue("$id", playlistId);
                await deletePlaylistCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }

    public async Task DeletePlaylistChannelsAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await WithRecoveryAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM channels WHERE playlist_id = $pid;";
            command.Parameters.AddWithValue("$pid", playlistId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        });
    }

    // ── Settings ──

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

    // ── Internals ──

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
