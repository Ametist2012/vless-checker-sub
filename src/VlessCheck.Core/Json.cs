using System.Text.Encodings.Web;
using System.Text.Json;

namespace VlessCheck.Core;

public static class Json
{
    /// <summary>Единый формат для API, файлов статуса и базы: snake_case, кириллица и эмодзи без экранирования.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T? Load<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    public static void WriteAtomic(string path, string content)
    {
        File.WriteAllText(path + ".tmp", content);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static void Save<T>(string path, T value) => WriteAtomic(path, JsonSerializer.Serialize(value, Options));
}
