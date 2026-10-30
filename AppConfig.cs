using System.IO;
using System.Text.Json;

namespace TestFlow;

/// <summary>Settings from appsettings.json (next to the exe). An environment variable overrides the file.</summary>
public static class AppConfig
{
    public const string FileName = "appsettings.json";

    /// <summary>
    /// SQL Server connection string for every database check.
    /// Read on each call, so editing appsettings.json needs no restart. Error is null when the setting is usable.
    /// </summary>
    public static (string ConnectionString, string? Error) GetDb()
    {
        string cs = "";
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("Database", out var d) && d.ValueKind == JsonValueKind.Object &&
                    d.TryGetProperty("ConnectionString", out var v) && v.ValueKind == JsonValueKind.String)
                    cs = v.GetString() ?? "";
            }
        }
        catch (Exception ex) { return ("", $"{FileName} cannot be read: {ex.Message}"); }

        // the environment variable wins over the file (keeps the password out of files)
        var env = Environment.GetEnvironmentVariable("TESTFLOW_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env)) cs = env.Trim();

        if (string.IsNullOrWhiteSpace(cs))
            return ("", $"No database connection. Set Database:ConnectionString in {path} (or the TESTFLOW_DB_CONNECTION environment variable)");
        return (cs, null);
    }
}
