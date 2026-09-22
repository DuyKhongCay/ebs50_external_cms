using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ebs50_backend.DTOs;
using Microsoft.Data.Sqlite;

namespace ebs50_backend.Services.Database;

/// <summary>Bounded archive IO and SQLite snapshots. SQLite calls themselves are synchronous.</summary>
public sealed class DatabaseBackupFiles(DatabaseLocation location)
{
    public const long MaxUploadBytes = 100 * 1024 * 1024;
    public const long MaxDatabaseBytes = 512 * 1024 * 1024;
    public static readonly string[] Tables = ["MachineStates", "ModelItems", "EslTags", "DispatchJobs", "SystemSettings"];
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SqliteConnection Open(string path, bool readOnly = true)
    {
        var builder = new SqliteConnectionStringBuilder(location.ConnectionString)
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 10
        };
        var connection = new SqliteConnection(builder.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    public void Snapshot(string destination)
    {
        using var source = Open(location.DatabasePath);
        using var target = Open(destination, false);
        source.BackupDatabase(target);
        using var command = target.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        command.ExecuteScalar();
    }

    public string ReadSchema(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;";
        using var reader = command.ExecuteReader();
        var schema = new List<string>();
        while (reader.Read())
            schema.Add(JsonSerializer.Serialize(new[] { reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? "" : reader.GetString(3) }));
        return string.Join('\n', schema);
    }

    public Dictionary<string, long> Inspect(string path, string expectedSchema)
    {
        if (ReadSchema(path) != expectedSchema)
            throw new InvalidDataException("Schema database không tương thích với server hiện tại.");
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
            throw new InvalidDataException("Database không vượt qua integrity_check.");
        command.CommandText = "PRAGMA foreign_key_check;";
        using (var reader = command.ExecuteReader())
            if (reader.Read()) throw new InvalidDataException("Database có khóa ngoại không hợp lệ.");
        var counts = new Dictionary<string, long>();
        foreach (var table in Tables)
        {
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
            counts[table] = (long)command.ExecuteScalar()!;
        }
        command.CommandText = "SELECT COUNT(*) FROM EslTags WHERE length(trim(MacAddress)) = 0 OR length(trim(MachineNo)) = 0 OR DesiredRevision < 1 OR BindingVersion < 1;";
        if ((long)command.ExecuteScalar()! != 0)
            throw new InvalidDataException("Database có thông tin tag hoặc revision không hợp lệ.");
        return counts;
    }

    private long CountUnfinishedJobs(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DispatchJobs WHERE Status IN ('Pending', 'Dispatching', 'Uploaded');";
        return (long)command.ExecuteScalar()!;
    }

    public async Task<DatabaseBackupManifest> CreateArchiveAsync(string folder, CancellationToken cancellationToken)
    {
        var database = Path.Combine(folder, "database.db");
        Snapshot(database);
        cancellationToken.ThrowIfCancellationRequested();
        if (new FileInfo(database).Length > MaxDatabaseBytes)
            throw new InvalidDataException("Database vượt giới hạn backup 512 MiB.");
        var manifest = new DatabaseBackupManifest(1, 1,
            typeof(DatabaseBackupFiles).Assembly.GetName().Version?.ToString() ?? "unknown",
            DateTime.UtcNow, await HashAsync(database, cancellationToken), Inspect(database, ReadSchema(database)), CountUnfinishedJobs(database));
        await using (var output = new FileStream(Path.Combine(folder, "backup.zip"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, true);
            await using (var entry = archive.CreateEntry("manifest.json").Open())
                await JsonSerializer.SerializeAsync(entry, manifest, JsonOptions, cancellationToken);
            await using var dbEntry = archive.CreateEntry("database.db", CompressionLevel.Fastest).Open();
            await using var input = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await input.CopyToAsync(dbEntry, cancellationToken);
        }
        if (new FileInfo(Path.Combine(folder, "backup.zip")).Length > MaxUploadBytes)
            throw new InvalidDataException("Backup nén vượt giới hạn import 100 MiB.");
        return manifest;
    }

    public async Task<DatabaseBackupManifest> ExtractAndValidateAsync(string folder, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(Path.Combine(folder, "upload.zip"));
        if (archive.Entries.Count != 2 || archive.Entries.Count(e => e.FullName == "manifest.json") != 1
            || archive.Entries.Count(e => e.FullName == "database.db") != 1)
            throw new InvalidDataException("ZIP phải chứa đúng manifest.json và database.db.");
        var manifestEntry = archive.GetEntry("manifest.json")!;
        var databaseEntry = archive.GetEntry("database.db")!;
        if (manifestEntry.Length > 64 * 1024 || databaseEntry.Length > MaxDatabaseBytes)
            throw new InvalidDataException("Dung lượng giải nén vượt giới hạn.");
        DatabaseBackupManifest manifest;
        await using (var entry = manifestEntry.Open())
        await using (var bounded = new MemoryStream())
        {
            await CopyBoundedAsync(entry, bounded, 64 * 1024, cancellationToken);
            bounded.Position = 0;
            manifest = await JsonSerializer.DeserializeAsync<DatabaseBackupManifest>(bounded, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Metadata không hợp lệ.");
        }
        if (manifest.FormatVersion != 1 || manifest.SchemaVersion != 1 || manifest.TableCounts == null)
            throw new InvalidDataException("Phiên bản backup không được hỗ trợ.");
        var path = Path.Combine(folder, "database.db");
        await using (var input = databaseEntry.Open())
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await CopyBoundedAsync(input, output, MaxDatabaseBytes, cancellationToken);
        await ValidateAsync(path, manifest, cancellationToken);
        return manifest;
    }

    public async Task ValidateAsync(string path, DatabaseBackupManifest manifest, CancellationToken cancellationToken)
    {
        if (!string.Equals(await HashAsync(path, cancellationToken), manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Checksum database không khớp.");
        cancellationToken.ThrowIfCancellationRequested();
        var counts = Inspect(path, ReadSchema(location.DatabasePath));
        if (manifest.TableCounts.Count != counts.Count || counts.Any(p => !manifest.TableCounts.TryGetValue(p.Key, out var count) || count != p.Value))
            throw new InvalidDataException("Số bản ghi không khớp metadata.");
        if (CountUnfinishedJobs(path) != manifest.UnfinishedJobCount)
            throw new InvalidDataException("Số job chưa hoàn tất không khớp metadata.");
    }

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
    }

    public static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("File vượt giới hạn dung lượng.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    // Metadata is small. Flush to disk before atomic rename so recovery precedes any database replacement.
    public static void WriteDurable<T>(string path, T value)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, JsonOptions);
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }

    public static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("Maintenance metadata is invalid.");

    public static async Task CopyDurableAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(true);
    }

    public void PrepareReplacement(bool discardCurrent = false)
    {
        // All request and worker scopes must have been disposed before this is called.
        SqliteConnection.ClearAllPools();
        if (!discardCurrent && File.Exists(location.DatabasePath))
        {
            using var connection = Open(location.DatabasePath, false);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = command.ExecuteReader();
            if (reader.Read() && reader.GetInt32(0) != 0)
                throw new IOException("Không thể checkpoint database; còn kết nối đang sử dụng.");
        }
        // These exact sidecars belong to the drained database, never to a user-supplied path.
        File.Delete(location.DatabasePath + "-wal");
        File.Delete(location.DatabasePath + "-shm");
    }
}
