using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavelit.Core.Storage;

internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads a JSON file; a missing or corrupt file yields null (corrupt files are kept as .bad for support).</summary>
    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (JsonException)
        {
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { }
            return null;
        }
    }

    /// <summary>Writes atomically (temp file + replace) so a crash or power loss never leaves a truncated file.</summary>
    public static void Write<T>(string path, T value)
    {
        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
