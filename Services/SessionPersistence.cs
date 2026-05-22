using System.IO;
using System.Text.Json;

namespace ReplaceValuesSql.Services;

public class SessionState
{
    public string QueryText { get; set; } = "";
    public string EnumText { get; set; } = "";
    public Dictionary<string, string> CastValues { get; set; } = new();
    public Dictionary<string, string> ParamValues { get; set; } = new();
    public Dictionary<string, bool?> BoolVariableValues { get; set; } = new();
    public Dictionary<string, string> GenericValues { get; set; } = new();
    public Dictionary<string, bool> ComplexTernaryValues { get; set; } = new();
    public Dictionary<string, bool> JsonParamModes { get; set; } = new(); // true = paste mode
    public Dictionary<string, string> JsonPasteValues { get; set; } = new();
    public Dictionary<string, List<Dictionary<string, string>>> JsonParamRows { get; set; } = new();
}

public static class SessionPersistence
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReplaceValuesSql",
        "session.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Save(SessionState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch { /* best-effort */ }
    }

    public static SessionState? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<SessionState>(json);
        }
        catch { return null; }
    }
}
