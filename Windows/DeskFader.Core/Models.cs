using System.Text.Json.Serialization;

namespace DeskFader.Core;

public static class DeskFaderConstants
{
    public const int SettingsVersion = 1;
    public const int SlotCount = 6;
    public const int MaxFrameBytes = 512;
}

public sealed class Slot
{
    [JsonPropertyName("process")]
    public string? Process { get; set; }

    [JsonPropertyName("default_volume")]
    public int DefaultVolume { get; set; }
}

public sealed class SettingsDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = DeskFaderConstants.SettingsVersion;

    [JsonPropertyName("slots")]
    public List<Slot> Slots { get; set; } = [];

    [JsonPropertyName("start_at_login")]
    public bool StartAtLogin { get; set; }
}

public sealed class ServiceState
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = DeskFaderConstants.SettingsVersion;

    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("slots")]
    public List<Slot> Slots { get; set; } = [];

    [JsonPropertyName("volumes")]
    public List<int> Volumes { get; set; } = [];

    [JsonPropertyName("selected_slot")]
    public int? SelectedSlot { get; set; }

    [JsonPropertyName("active_apps")]
    public List<string> ActiveApps { get; set; } = [];

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; set; }
}

public static class SlotValidator
{
    public static List<Slot> Validate(IEnumerable<Slot>? slots)
    {
        var values = slots?.ToList() ?? throw new ArgumentException("controller configuration requires exactly six slots");
        if (values.Count != DeskFaderConstants.SlotCount)
        {
            throw new ArgumentException("controller configuration requires exactly six slots");
        }

        var processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < values.Count; index++)
        {
            var slot = values[index];
            if (slot is null)
            {
                throw new ArgumentException($"slot {index} is required");
            }

            if (slot.DefaultVolume is < 0 or > 100)
            {
                throw new ArgumentException($"slot {index} default_volume must be 0 through 100");
            }

            if (slot.Process is null) continue;

            if (string.IsNullOrWhiteSpace(slot.Process))
            {
                throw new ArgumentException($"slot {index} process must be a non-empty string");
            }

            slot.Process = slot.Process.Trim();
            if (!processes.Add(slot.Process))
            {
                throw new ArgumentException($"slot {index} process duplicates another slot");
            }
        }

        return values.Select(slot => new Slot { Process = slot.Process, DefaultVolume = slot.DefaultVolume }).ToList();
    }
}
