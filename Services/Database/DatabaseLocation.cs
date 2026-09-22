using Microsoft.Data.Sqlite;

namespace ebs50_backend.Services.Database;

/// <summary>One canonical path for EF, snapshots and recovery, independent of service working directory.</summary>
public sealed class DatabaseLocation
{
    public string ConnectionString { get; }
    public string DatabasePath { get; }
    public string MaintenancePath { get; }

    public DatabaseLocation(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode == SqliteOpenMode.Memory || builder.DataSource is "" or ":memory:")
            throw new InvalidOperationException("Database backup requires a file-backed SQLite database.");
        DatabasePath = Path.GetFullPath(builder.DataSource, AppContext.BaseDirectory);
        builder.DataSource = DatabasePath;
        ConnectionString = builder.ToString();
        MaintenancePath = DatabasePath + ".maintenance";
    }
}
