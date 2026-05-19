using System.Data;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppleMusicTranslator.Models;
using Microsoft.Data.Sqlite;

namespace AppleMusicTranslator.Services;

public sealed class LyricsCacheService
{
    public const string CachedSuffix = " cache";
    public const double DefaultConfidence = 0.92;
    public const double MinimumSaveConfidence = 0.80;

    private const int MaxCachedTracks = 600;
    private const string LegacyMigrationKey = "legacy_json_migrated";

    private readonly object _lock = new();
    private readonly string _cachePath;
    private readonly string _legacyJsonPath;
    private bool _isAvailable;

    public LyricsCacheService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AppleMusicTranslator");
        Directory.CreateDirectory(appData);

        _cachePath = Path.Combine(appData, "lyrics-cache.db");
        _legacyJsonPath = Path.Combine(appData, "lyrics-cache.json");

        lock (_lock)
        {
            _isAvailable = TryInitializeDatabase();
            if (!_isAvailable)
            {
                TryMoveCacheAside();
                _isAvailable = TryInitializeDatabase();
            }
        }
    }

    public bool TryGet(TrackInfo track, out LyricsCacheResult result)
    {
        result = new LyricsCacheResult(LyricsBundle.Empty("No cached lyrics"), 0, false, string.Empty);
        if (!_isAvailable)
        {
            return false;
        }

        lock (_lock)
        {
            try
            {
                using var connection = OpenConnection();
                using var trackCommand = connection.CreateCommand();
                trackCommand.CommandText = """
                    SELECT title, artist, album, duration_ms, source, is_synced, fingerprint, captured_at_utc, confidence
                    FROM tracks
                    WHERE track_key = $track_key
                    LIMIT 1;
                    """;
                trackCommand.Parameters.AddWithValue("$track_key", track.CacheKey);

                using var reader = trackCommand.ExecuteReader();
                if (!reader.Read())
                {
                    return false;
                }

                var entry = new CacheEntry
                {
                    Title = ReadString(reader, 0),
                    Artist = ReadString(reader, 1),
                    Album = ReadString(reader, 2),
                    DurationMs = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    Source = ReadString(reader, 4),
                    IsSynced = !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
                    Fingerprint = ReadString(reader, 6),
                    CapturedAtUtc = ParseDateTime(ReadString(reader, 7)),
                    Confidence = reader.IsDBNull(8) ? DefaultConfidence : Math.Clamp(reader.GetDouble(8), 0, 1),
                    Lines = LoadLines(connection, track.CacheKey)
                };

                if (entry.Lines.Count == 0)
                {
                    result = new LyricsCacheResult(LyricsBundle.Empty("No cached lyric lines"), 0, false, string.Empty);
                    return false;
                }

                var lyrics = ToBundle(entry).WithSource($"{entry.Source}{CachedSuffix}");
                result = new LyricsCacheResult(
                    lyrics,
                    entry.Confidence,
                    HasConflictLocked(connection, track.CacheKey, entry.Fingerprint, out var conflict),
                    conflict);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool HasConflict(TrackInfo track, LyricsBundle lyrics, out string conflictDescription)
    {
        conflictDescription = string.Empty;
        if (!_isAvailable)
        {
            return false;
        }

        var fingerprint = Fingerprint(lyrics);
        lock (_lock)
        {
            try
            {
                using var connection = OpenConnection();
                return HasConflictLocked(connection, track.CacheKey, fingerprint, out conflictDescription);
            }
            catch
            {
                conflictDescription = string.Empty;
                return false;
            }
        }
    }

    public bool Save(TrackInfo track, LyricsBundle lyrics, double confidence = DefaultConfidence)
    {
        confidence = Math.Clamp(confidence, 0, 1);
        if (!_isAvailable || !ShouldCache(lyrics, confidence))
        {
            return false;
        }

        lock (_lock)
        {
            try
            {
                using var connection = OpenConnection();
                if (TryReadExistingConfidence(connection, track.CacheKey, out var existingConfidence)
                    && confidence + 0.001 < existingConfidence)
                {
                    return false;
                }

                using var transaction = connection.BeginTransaction();

                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO tracks (
                            track_key, title, artist, album, duration_ms, source, is_synced, fingerprint, captured_at_utc, confidence
                        )
                        VALUES (
                            $track_key, $title, $artist, $album, $duration_ms, $source, $is_synced, $fingerprint, $captured_at_utc, $confidence
                        )
                        ON CONFLICT(track_key) DO UPDATE SET
                            title = excluded.title,
                            artist = excluded.artist,
                            album = excluded.album,
                            duration_ms = excluded.duration_ms,
                            source = excluded.source,
                            is_synced = excluded.is_synced,
                            fingerprint = excluded.fingerprint,
                            captured_at_utc = excluded.captured_at_utc,
                            confidence = excluded.confidence;
                        """;
                    command.Parameters.AddWithValue("$track_key", track.CacheKey);
                    command.Parameters.AddWithValue("$title", track.Title);
                    command.Parameters.AddWithValue("$artist", track.CanonicalArtist);
                    command.Parameters.AddWithValue("$album", track.CanonicalAlbum);
                    command.Parameters.AddWithValue("$duration_ms", (long)Math.Round(track.Duration.TotalMilliseconds));
                    command.Parameters.AddWithValue("$source", StripCacheSuffix(lyrics.Source));
                    command.Parameters.AddWithValue("$is_synced", lyrics.IsSynced ? 1 : 0);
                    command.Parameters.AddWithValue("$fingerprint", Fingerprint(lyrics));
                    command.Parameters.AddWithValue("$captured_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    command.Parameters.AddWithValue("$confidence", confidence);
                    command.ExecuteNonQuery();
                }

                DeleteLines(connection, transaction, track.CacheKey);
                InsertLines(connection, transaction, track.CacheKey, lyrics.Lines);
                PruneLocked(connection, transaction);

                transaction.Commit();
                return true;
            }
            catch
            {
                // Cache persistence should never interrupt lyric display.
                return false;
            }
        }
    }

    public bool Delete(TrackInfo track)
    {
        if (!_isAvailable)
        {
            return false;
        }

        lock (_lock)
        {
            try
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM tracks WHERE track_key = $track_key;";
                command.Parameters.AddWithValue("$track_key", track.CacheKey);
                return command.ExecuteNonQuery() > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public string CachePath => _cachePath;

    public string LegacyJsonPath => _legacyJsonPath;

    public static bool IsCachedSource(string source) =>
        source.EndsWith(CachedSuffix, StringComparison.OrdinalIgnoreCase);

    private static bool ShouldCache(LyricsBundle lyrics, double confidence) =>
        lyrics.IsSynced
        && lyrics.Lines.Count >= 4
        && confidence >= MinimumSaveConfidence
        && !IsCachedSource(lyrics.Source)
        && !string.Equals(lyrics.Source, LyricsBundle.AppleMusicVisibleLyricsSource, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(lyrics.Source, LyricsBundle.LrcLibPlainFallbackSource, StringComparison.OrdinalIgnoreCase);

    private bool TryInitializeDatabase()
    {
        try
        {
            InitializeDatabase();
            MigrateLegacyJsonIfNeeded();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TryMoveCacheAside()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var brokenPath = Path.Combine(
                Path.GetDirectoryName(_cachePath) ?? string.Empty,
                $"lyrics-cache.broken-{DateTime.UtcNow:yyyyMMddHHmmss}.db");
            File.Move(_cachePath, brokenPath, overwrite: false);
        }
        catch
        {
            // If the broken cache cannot be moved, public methods will simply run without cache.
        }
    }

    private void InitializeDatabase()
    {
        using var connection = OpenConnection();
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS tracks (
                track_key TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                artist TEXT NOT NULL,
                album TEXT NOT NULL,
                duration_ms INTEGER NOT NULL,
                source TEXT NOT NULL,
                is_synced INTEGER NOT NULL,
                fingerprint TEXT NOT NULL,
                captured_at_utc TEXT NOT NULL,
                confidence REAL NOT NULL DEFAULT 0.92
            );
            """);
        EnsureColumn(connection, "tracks", "confidence", "REAL NOT NULL DEFAULT 0.92");
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS lines (
                track_key TEXT NOT NULL,
                line_index INTEGER NOT NULL,
                begin_ms INTEGER NOT NULL,
                end_ms INTEGER NOT NULL,
                text TEXT NOT NULL,
                PRIMARY KEY (track_key, line_index),
                FOREIGN KEY (track_key) REFERENCES tracks(track_key) ON DELETE CASCADE
            );
            """);
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_tracks_fingerprint ON tracks(fingerprint);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_tracks_captured ON tracks(captured_at_utc);");
    }

    private void MigrateLegacyJsonIfNeeded()
    {
        if (!File.Exists(_legacyJsonPath))
        {
            return;
        }

        using var connection = OpenConnection();
        if (string.Equals(ReadMetadata(connection, LegacyMigrationKey), "1", StringComparison.Ordinal))
        {
            return;
        }

        var legacy = LoadLegacyCache(_legacyJsonPath);
        if (legacy.Tracks.Count == 0)
        {
            WriteMetadata(connection, LegacyMigrationKey, "1");
            return;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var (trackKey, entry) in legacy.Tracks)
        {
            if (entry.Lines.Count == 0)
            {
                continue;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO tracks (
                        track_key, title, artist, album, duration_ms, source, is_synced, fingerprint, captured_at_utc, confidence
                    )
                    VALUES (
                        $track_key, $title, $artist, $album, $duration_ms, $source, $is_synced, $fingerprint, $captured_at_utc, $confidence
                    );
                    """;
                command.Parameters.AddWithValue("$track_key", trackKey);
                command.Parameters.AddWithValue("$title", entry.Title);
                command.Parameters.AddWithValue("$artist", entry.Artist);
                command.Parameters.AddWithValue("$album", entry.Album);
                command.Parameters.AddWithValue("$duration_ms", entry.DurationMs);
                command.Parameters.AddWithValue("$source", StripCacheSuffix(entry.Source));
                command.Parameters.AddWithValue("$is_synced", entry.IsSynced ? 1 : 0);
                command.Parameters.AddWithValue("$fingerprint", string.IsNullOrWhiteSpace(entry.Fingerprint) ? Fingerprint(ToBundle(entry)) : entry.Fingerprint);
                command.Parameters.AddWithValue("$captured_at_utc", entry.CapturedAtUtc == default
                    ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    : entry.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$confidence", entry.Confidence <= 0 ? DefaultConfidence : Math.Clamp(entry.Confidence, 0, 1));
                command.ExecuteNonQuery();
            }

            DeleteLines(connection, transaction, trackKey);
            InsertLines(connection, transaction, trackKey, entry.Lines);
        }

        PruneLocked(connection, transaction);
        transaction.Commit();
        WriteMetadata(connection, LegacyMigrationKey, "1");
    }

    private bool HasConflictLocked(SqliteConnection connection, string currentTrackKey, string fingerprint, out string conflictDescription)
    {
        conflictDescription = string.Empty;
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT title, artist
            FROM tracks
            WHERE track_key <> $track_key
              AND fingerprint = $fingerprint
            ORDER BY captured_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$track_key", currentTrackKey);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        conflictDescription = $"{ReadString(reader, 0)} - {ReadString(reader, 1)}";
        return true;
    }

    private static bool TryReadExistingConfidence(SqliteConnection connection, string trackKey, out double confidence)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT confidence FROM tracks WHERE track_key = $track_key LIMIT 1;";
        command.Parameters.AddWithValue("$track_key", trackKey);
        var value = command.ExecuteScalar();
        if (value is null || value == DBNull.Value)
        {
            confidence = 0;
            return false;
        }

        confidence = Math.Clamp(Convert.ToDouble(value, CultureInfo.InvariantCulture), 0, 1);
        return true;
    }

    private static List<CacheLine> LoadLines(SqliteConnection connection, string trackKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT begin_ms, end_ms, text
            FROM lines
            WHERE track_key = $track_key
            ORDER BY line_index;
            """;
        command.Parameters.AddWithValue("$track_key", trackKey);

        var lines = new List<CacheLine>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            lines.Add(new CacheLine
            {
                BeginMs = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                EndMs = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                Text = ReadString(reader, 2)
            });
        }

        return lines;
    }

    private static void DeleteLines(SqliteConnection connection, SqliteTransaction transaction, string trackKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM lines WHERE track_key = $track_key;";
        command.Parameters.AddWithValue("$track_key", trackKey);
        command.ExecuteNonQuery();
    }

    private static void InsertLines(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string trackKey,
        IReadOnlyList<LyricLine> lines)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO lines (track_key, line_index, begin_ms, end_ms, text)
            VALUES ($track_key, $line_index, $begin_ms, $end_ms, $text);
            """;
        var trackKeyParameter = command.Parameters.Add("$track_key", SqliteType.Text);
        var indexParameter = command.Parameters.Add("$line_index", SqliteType.Integer);
        var beginParameter = command.Parameters.Add("$begin_ms", SqliteType.Integer);
        var endParameter = command.Parameters.Add("$end_ms", SqliteType.Integer);
        var textParameter = command.Parameters.Add("$text", SqliteType.Text);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            trackKeyParameter.Value = trackKey;
            indexParameter.Value = index;
            beginParameter.Value = (long)Math.Round(line.Begin.TotalMilliseconds);
            endParameter.Value = (long)Math.Round(line.End.TotalMilliseconds);
            textParameter.Value = line.Text ?? string.Empty;
            command.ExecuteNonQuery();
        }
    }

    private static void InsertLines(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string trackKey,
        IReadOnlyList<CacheLine> lines)
    {
        var lyricLines = lines
            .Select(line => new LyricLine(
                TimeSpan.FromMilliseconds(Math.Max(0, line.BeginMs)),
                TimeSpan.FromMilliseconds(Math.Max(line.BeginMs + 1000, line.EndMs)),
                line.Text ?? string.Empty))
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .ToArray();

        InsertLines(connection, transaction, trackKey, lyricLines);
    }

    private static LyricsBundle ToBundle(CacheEntry entry)
    {
        var lines = entry.Lines
            .Select(line => new LyricLine(
                TimeSpan.FromMilliseconds(Math.Max(0, line.BeginMs)),
                TimeSpan.FromMilliseconds(Math.Max(line.BeginMs + 1000, line.EndMs)),
                line.Text ?? string.Empty))
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .ToArray();

        return new LyricsBundle(
            entry.Source,
            lines,
            entry.IsSynced,
            TimeSpan.FromMilliseconds(Math.Max(0, entry.DurationMs)),
            string.Join('\n', lines.Select(line => line.Text)));
    }

    private static string Fingerprint(LyricsBundle lyrics)
    {
        var text = string.Join('\n', lyrics.Lines
            .Select(line => NormalizeLyricText(line.Text))
            .Where(line => line.Length > 0)
            .Take(40));

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string NormalizeLyricText(string text) =>
        string.Concat((text ?? string.Empty)
            .Where(ch => !char.IsWhiteSpace(ch)))
            .Trim()
            .ToLowerInvariant();

    private static string StripCacheSuffix(string source) =>
        IsCachedSource(source)
            ? source[..^CachedSuffix.Length].TrimEnd()
            : source;

    private static CacheFile LoadLegacyCache(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<CacheFile>(json) ?? new CacheFile();
        }
        catch
        {
            return new CacheFile();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _cachePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 4
        }.ToString());
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
        ExecuteNonQuery(connection, "PRAGMA synchronous = NORMAL;");
        return connection;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string declaration)
    {
        using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = checkCommand.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(ReadString(reader, 1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        ExecuteNonQuery(connection, $"ALTER TABLE {tableName} ADD COLUMN {columnName} {declaration};");
    }

    private static string? ReadMetadata(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteMetadata(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metadata (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void PruneLocked(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var countCommand = connection.CreateCommand();
        countCommand.Transaction = transaction;
        countCommand.CommandText = "SELECT COUNT(*) FROM tracks;";
        var count = Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (count <= MaxCachedTracks)
        {
            return;
        }

        using var pruneCommand = connection.CreateCommand();
        pruneCommand.Transaction = transaction;
        pruneCommand.CommandText = """
            DELETE FROM tracks
            WHERE track_key IN (
                SELECT track_key
                FROM tracks
                ORDER BY captured_at_utc ASC
                LIMIT $limit
            );
            """;
        pruneCommand.Parameters.AddWithValue("$limit", count - MaxCachedTracks);
        pruneCommand.ExecuteNonQuery();
    }

    private static string ReadString(IDataRecord reader, int index) =>
        reader.IsDBNull(index) ? string.Empty : reader.GetString(index);

    private static DateTime ParseDateTime(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTime.MinValue;

    private sealed class CacheFile
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, CacheEntry> Tracks { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CacheEntry
    {
        public string Title { get; set; } = string.Empty;

        public string Artist { get; set; } = string.Empty;

        public string Album { get; set; } = string.Empty;

        public long DurationMs { get; set; }

        public string Source { get; set; } = string.Empty;

        public bool IsSynced { get; set; }

        public DateTime CapturedAtUtc { get; set; }

        public string Fingerprint { get; set; } = string.Empty;

        public double Confidence { get; set; } = DefaultConfidence;

        public List<CacheLine> Lines { get; set; } = [];
    }

    private sealed class CacheLine
    {
        public long BeginMs { get; set; }

        public long EndMs { get; set; }

        public string? Text { get; set; }
    }
}
