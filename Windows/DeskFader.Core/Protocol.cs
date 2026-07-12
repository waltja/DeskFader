using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskFader.Core;

public sealed class ProtocolException(string message) : ArgumentException(message);

public sealed class ProtocolMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";
    [JsonPropertyName("version")]
    public int? Version { get; init; }
    [JsonPropertyName("seq")]
    public int? Seq { get; init; }
    [JsonPropertyName("volumes")]
    public List<int>? Volumes { get; init; }
    [JsonPropertyName("slot")]
    public int? Slot { get; init; }
    [JsonPropertyName("value")]
    public int? Value { get; init; }
    [JsonPropertyName("code")]
    public string? Code { get; init; }
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public static class Protocol
{
    public static ProtocolMessage Decode(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > DeskFaderConstants.MaxFrameBytes) throw new ProtocolException("frame is too large");
        try
        {
            using var doc = JsonDocument.Parse(raw.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ProtocolException("invalid JSON frame");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!fields.TryAdd(property.Name, property.Value)) throw new ProtocolException("duplicate message field");
            }
            var type = StringValue(fields, "type");
            var message = new ProtocolMessage
            {
                Type = type,
                Version = OptionalInt(fields, "version"),
                Seq = OptionalInt(fields, "seq"),
                Volumes = OptionalVolumes(fields),
                Slot = OptionalInt(fields, "slot"),
                Value = OptionalInt(fields, "value"),
                Code = OptionalString(fields, "code"),
                Message = OptionalString(fields, "message")
            };
            Validate(fields.Keys, message);
            return message;
        }
        catch (JsonException ex) { throw new ProtocolException($"invalid JSON frame: {ex.Message}"); }
        catch (ArgumentException ex) when (ex is not ProtocolException) { throw new ProtocolException($"invalid JSON frame: {ex.Message}"); }
    }

    public static byte[] Encode(ProtocolMessage message)
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var firstPass = JsonSerializer.SerializeToUtf8Bytes(message, options);
        var validated = Decode(firstPass);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(validated, options);
        if (bytes.Length > DeskFaderConstants.MaxFrameBytes) throw new ProtocolException("frame is too large");
        return [.. bytes, (byte)'\n'];
    }

    private static void Validate(IEnumerable<string> names, ProtocolMessage message)
    {
        static void RequireFields(IEnumerable<string> actual, params string[] expected)
        {
            if (!actual.OrderBy(name => name).SequenceEqual(expected.OrderBy(name => name)))
            {
                throw new ProtocolException("invalid message fields");
            }
        }

        static void RequireRange(int? number, string name, int minimum, int maximum)
        {
            if (number is null || number < minimum || number > maximum)
            {
                throw new ProtocolException($"{name} is out of range");
            }
        }

        switch (message.Type)
        {
            case "hello": RequireFields(names, "type", "version"); RequireRange(message.Version, "version", 1, 1); break;
            case "state": RequireFields(names, "type", "seq", "volumes"); RequireRange(message.Seq, "seq", 0, int.MaxValue); Volumes(message.Volumes); break;
            case "ack": RequireFields(names, "type", "seq"); RequireRange(message.Seq, "seq", 0, int.MaxValue); break;
            case "volume": RequireFields(names, "type", "seq", "slot", "value"); RequireRange(message.Seq, "seq", 0, int.MaxValue); RequireRange(message.Slot, "slot", 0, 5); RequireRange(message.Value, "value", 0, 100); break;
            case "select": RequireFields(names, "type", "seq", "slot"); RequireRange(message.Seq, "seq", 0, int.MaxValue); RequireRange(message.Slot, "slot", 0, 5); break;
            case "error": RequireFields(names, "type", "code", "message"); if (message.Code is null || message.Message is null) throw new ProtocolException("invalid error values"); break;
            default: throw new ProtocolException("unknown message type");
        }
    }
    private static void Volumes(List<int>? values) { if (values is null || values.Count != 6 || values.Any(x => x is < 0 or > 100)) throw new ProtocolException("volumes must contain six values"); }
    private static string StringValue(Dictionary<string, JsonElement> f, string n) => OptionalString(f, n) ?? throw new ProtocolException($"{n} must be a string");
    private static string? OptionalString(Dictionary<string, JsonElement> f, string n) => f.TryGetValue(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
    private static int? OptionalInt(Dictionary<string, JsonElement> f, string n) => f.TryGetValue(n, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var result) ? result : null;
    private static List<int>? OptionalVolumes(Dictionary<string, JsonElement> f) => f.TryGetValue("volumes", out var x) && x.ValueKind == JsonValueKind.Array ? x.EnumerateArray().Select(v => v.TryGetInt32(out var i) ? i : int.MinValue).ToList() : null;
}
