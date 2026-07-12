using System.Text.Json;

namespace DeskFader.Core;

public static class DeskFaderPaths
{
    public static string AppDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskFader");
}

public static class AtomicJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            JsonSchema.ValidateNoDuplicateProperties(document.RootElement);
            if (typeof(T) == typeof(SettingsDocument)) JsonSchema.ValidateSettings(document.RootElement);
            else if (typeof(T) == typeof(ServiceState)) JsonSchema.ValidateServiceState(document.RootElement);
            return JsonSerializer.Deserialize<T>(document.RootElement.GetRawText(), Options);
        }
        catch (JsonException ex) { throw new InvalidOperationException($"could not read DeskFader settings: {ex.Message}", ex); }
    }

    public static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonSerializer.Serialize(value, Options));
                writer.Write('\n');
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, overwrite: true); break; }
                catch (UnauthorizedAccessException) when (attempt < 2) { Thread.Sleep(50); }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal static class JsonSchema
{
    public static void ValidateNoDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException($"duplicate property '{property.Name}'");
                ValidateNoDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) ValidateNoDuplicateProperties(element);
    }

    public static void ValidateSettings(JsonElement root)
    {
        RequireFields(root, "version", "slots", "start_at_login");
        RequireVersion(root);
        RequireSlots(root.GetProperty("slots"));
        RequireKind(root.GetProperty("start_at_login"), JsonValueKind.True, JsonValueKind.False);
    }

    public static void ValidateServiceState(JsonElement root)
    {
        RequireKind(root, JsonValueKind.Object);
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "version": RequireInteger(property.Value); break;
                case "running": RequireKind(property.Value, JsonValueKind.True, JsonValueKind.False); break;
                case "slots": RequireSlots(property.Value); break;
                case "volumes": RequireVolumes(property.Value); break;
                case "selected_slot": RequireSelectedSlot(property.Value); break;
                case "active_apps": RequireStringArray(property.Value); break;
                case "error": RequireKind(property.Value, JsonValueKind.String, JsonValueKind.Null); break;
                case "updated_at": RequireKind(property.Value, JsonValueKind.Number); break;
            }
        }
    }

    private static void RequireFields(JsonElement root, params string[] expected)
    {
        RequireKind(root, JsonValueKind.Object);
        var actual = root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal);
        if (!actual.SequenceEqual(expected.OrderBy(name => name, StringComparer.Ordinal))) throw new JsonException("invalid JSON fields");
    }

    private static void RequireVersion(JsonElement root)
    {
        var version = root.GetProperty("version");
        RequireInteger(version);
    }

    private static void RequireSlots(JsonElement slots)
    {
        RequireKind(slots, JsonValueKind.Array);
        if (slots.GetArrayLength() != DeskFaderConstants.SlotCount) throw new JsonException("controller configuration requires exactly six slots");
        foreach (var slot in slots.EnumerateArray())
        {
            RequireFields(slot, "process", "default_volume");
            var process = slot.GetProperty("process");
            RequireKind(process, JsonValueKind.String, JsonValueKind.Null);
            if (process.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(process.GetString())) throw new JsonException("process must not be whitespace");
            var volume = slot.GetProperty("default_volume");
            RequireInteger(volume);
            if (volume.GetInt32() is < 0 or > 100) throw new JsonException("default_volume must be 0 through 100");
        }
    }

    private static void RequireVolumes(JsonElement volumes)
    {
        RequireKind(volumes, JsonValueKind.Array);
        foreach (var value in volumes.EnumerateArray()) RequireInteger(value);
    }

    private static void RequireSelectedSlot(JsonElement slot)
    {
        RequireKind(slot, JsonValueKind.Number, JsonValueKind.Null);
        if (slot.ValueKind == JsonValueKind.Number && (!slot.TryGetInt32(out var value) || value is < 0 or >= DeskFaderConstants.SlotCount)) throw new JsonException("selected_slot must be 0 through 5");
    }

    private static void RequireStringArray(JsonElement values)
    {
        RequireKind(values, JsonValueKind.Array);
        foreach (var value in values.EnumerateArray()) RequireKind(value, JsonValueKind.String);
    }

    private static void RequireInteger(JsonElement value)
    {
        RequireKind(value, JsonValueKind.Number);
        if (!value.TryGetInt32(out _)) throw new JsonException("expected integer");
    }

    private static void RequireKind(JsonElement value, params JsonValueKind[] kinds)
    {
        if (!kinds.Contains(value.ValueKind)) throw new JsonException("invalid JSON value type");
    }
}

public sealed class SettingsStore
{
    public SettingsStore(string? directory = null, string? legacyPath = null)
    {
        Directory = directory ?? DeskFaderPaths.AppDataDirectory;
        Path = System.IO.Path.Combine(Directory, "settings.json");
        LegacyPath = legacyPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "controller_config.json");
    }
    public string Directory { get; }
    public string Path { get; }
    public string LegacyPath { get; }

    public SettingsDocument Load()
    {
        var settings = AtomicJson.Read<SettingsDocument>(Path);
        if (settings is null)
        {
            var legacy = AtomicJson.Read<LegacyConfig>(LegacyPath) ?? throw new InvalidOperationException("could not read DeskFader settings: legacy configuration is missing");
            settings = new SettingsDocument { Slots = SlotValidator.Validate(legacy.Slots), StartAtLogin = false };
            Save(settings);
        }
        if (settings.Version != DeskFaderConstants.SettingsVersion) throw new InvalidOperationException("unsupported DeskFader settings version");
        settings.Slots = SlotValidator.Validate(settings.Slots);
        return settings;
    }

    public void Save(SettingsDocument settings)
    {
        settings.Slots = SlotValidator.Validate(settings.Slots);
        settings.Version = DeskFaderConstants.SettingsVersion;
        AtomicJson.Write(Path, settings);
    }

    private sealed class LegacyConfig
    {
        [System.Text.Json.Serialization.JsonPropertyName("slots")]
        public List<Slot> Slots { get; set; } = [];
    }
}
