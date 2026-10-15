using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace LANCommander.Plugins.GOG.Storage;

/// <summary>
/// Reads and writes the plugin's JSON state files.
/// </summary>
internal static class JsonFile
{
    /// <summary>
    /// Reads a file, or returns <see langword="null"/> when there is none yet.
    /// </summary>
    public static T? Read<T>(string path, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (!File.Exists(path))
            return null;

        using var stream = File.OpenRead(path);

        return JsonSerializer.Deserialize(stream, typeInfo);
    }

    /// <summary>
    /// Replaces a file atomically, so that a crash part-way through leaves the previous contents
    /// rather than half of the new ones.
    /// </summary>
    public static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, value, typeInfo);

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
