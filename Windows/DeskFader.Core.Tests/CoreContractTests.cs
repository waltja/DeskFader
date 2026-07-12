using System.Text;
using DeskFader.Core;
using Xunit;

namespace DeskFader.Core.Tests;

public sealed class CoreContractTests
{
    [Fact]
    public void ProtocolV1StateRoundTrips()
    {
        var message = new ProtocolMessage { Type = "state", Seq = 42, Volumes = [0, 20, 40, 60, 80, 100] };

        var decoded = Protocol.Decode(Protocol.Encode(message));

        Assert.Equal("state", decoded.Type);
        Assert.Equal(42, decoded.Seq);
        Assert.Equal(new[] { 0, 20, 40, 60, 80, 100 }, decoded.Volumes);
    }

    [Fact]
    public void ProtocolRejectsDuplicateJsonFields()
    {
        var frame = Encoding.UTF8.GetBytes("{\"type\":\"ack\",\"seq\":1,\"seq\":2}");

        Assert.Throws<ProtocolException>(() => Protocol.Decode(frame));
    }

    [Fact]
    public void ProtocolSelectRoundTrips()
    {
        var decoded = Protocol.Decode(Protocol.Encode(new ProtocolMessage { Type = "select", Seq = 42, Slot = 3 }));
        Assert.Equal(3, decoded.Slot);
        Assert.Equal(42, decoded.Seq);
    }

    [Fact]
    public void ProtocolRejectsInvalidAndOversizedFrames()
    {
        Assert.Throws<ProtocolException>(() => Protocol.Decode(Encoding.UTF8.GetBytes("not json")));
        Assert.Throws<ProtocolException>(() => Protocol.Decode(new byte[DeskFaderConstants.MaxFrameBytes + 1]));
    }

    [Fact]
    public void SettingsJsonAllowsNullProcess()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.FilePath("settings.json");
        File.WriteAllText(path, SettingsJson("null"));

        var settings = AtomicJson.Read<SettingsDocument>(path);

        Assert.NotNull(settings);
        Assert.Null(settings!.Slots[0].Process);
    }

    [Fact]
    public void SettingsJsonRejectsMissingVersionAndWhitespaceProcess()
    {
        using var temporary = new TemporaryDirectory();
        var missingVersion = temporary.FilePath("missing-version.json");
        var whitespaceProcess = temporary.FilePath("whitespace-process.json");
        File.WriteAllText(missingVersion, "{\"slots\":[],\"start_at_login\":false}");
        File.WriteAllText(whitespaceProcess, SettingsJson("\"   \""));

        Assert.Throws<InvalidOperationException>(() => AtomicJson.Read<SettingsDocument>(missingVersion));
        Assert.Throws<InvalidOperationException>(() => AtomicJson.Read<SettingsDocument>(whitespaceProcess));
    }

    [Fact]
    public void SlotValidatorAllowsClearedMappingsAndRejectsDuplicateMappings()
    {
        var slots = Enumerable.Range(0, DeskFaderConstants.SlotCount).Select(index => new Slot { Process = index < 2 ? null : $"app{index}.exe", DefaultVolume = index }).ToList();

        var validated = SlotValidator.Validate(slots);

        Assert.Null(validated[0].Process);
        slots[1].Process = "app2.exe";
        Assert.Throws<ArgumentException>(() => SlotValidator.Validate(slots));
    }

    [Fact]
    public async Task ServiceRetainsTargetVolumesWhenMappingChanges()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SettingsStore(temporary.DirectoryPath, temporary.FilePath("missing-legacy.json"));
        AtomicJson.Write(store.Path, new SettingsDocument { Slots = Slots("first.exe"), StartAtLogin = false });
        var audio = new FakeAudioProvider();
        var transport = new FakeTransport();
        using var service = new DeskFaderService(store, audio, transport);

        service.Start();
        transport.EmitVolume(0, 73);
        transport.EmitSelection(2);
        var replacement = Slots(null);
        service.ApplyConfiguration(replacement, startAtLogin: true);
        var state = service.CurrentState();

        Assert.Null(state.Slots[0].Process);
        Assert.Equal(73, state.Volumes[0]);
        Assert.Equal(73, state.Slots[0].DefaultVolume);
        Assert.Contains(73, transport.Updates.SelectMany(values => values));
        Assert.Equal(2, state.SelectedSlot);
        await service.StopAsync();
    }

    [Fact]
    public async Task ServiceRetainsInitialPairedSelectionWithoutVolumeUpdate()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SettingsStore(temporary.DirectoryPath, temporary.FilePath("missing-legacy.json"));
        AtomicJson.Write(store.Path, new SettingsDocument { Slots = Slots("first.exe"), StartAtLogin = false });
        var transport = new FakeTransport { SelectionOnInitialUpdate = 4 };
        using var service = new DeskFaderService(store, new FakeAudioProvider(), transport);

        service.Start();

        Assert.Equal(4, service.CurrentState().SelectedSlot);
        Assert.Single(transport.Updates);
        await service.StopAsync();
    }

    private static string SettingsJson(string firstProcess) => "{\"version\":1,\"slots\":" + SlotsJson(firstProcess) + ",\"start_at_login\":false}";

    private static string SlotsJson(string firstProcess)
    {
        var slots = new[]
        {
            "{\"process\":" + firstProcess + ",\"default_volume\":0}",
            "{\"process\":\"app2\",\"default_volume\":20}",
            "{\"process\":\"app3\",\"default_volume\":40}",
            "{\"process\":\"app4\",\"default_volume\":60}",
            "{\"process\":\"app5\",\"default_volume\":80}",
            "{\"process\":\"app6\",\"default_volume\":100}"
        };
        return "[" + string.Join(',', slots) + "]";
    }

    private static List<Slot> Slots(string? firstProcess) => Enumerable.Range(0, DeskFaderConstants.SlotCount).Select(index => new Slot { Process = index == 0 ? firstProcess : $"app{index}.exe", DefaultVolume = index * 10 }).ToList();

    private sealed class FakeAudioProvider : IAudioSessionProvider
    {
        public IReadOnlyList<string> GetActiveApplications() => ["first.exe"];
        public void ApplyVolumes(IReadOnlyList<Slot> slots, IReadOnlyList<int> desired, Action<string> log) { }
    }

    private sealed class FakeTransport : IDeskFaderTransport
    {
        private readonly TaskCompletionSource stopped = new();
        public event Action<int, int>? VolumeReceived;
        public event Action<int>? SelectionReceived;
        public List<List<int>> Updates { get; } = [];
        public int? SelectionOnInitialUpdate { get; set; }
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => stopped.TrySetResult());
            await stopped.Task;
        }
        public void UpdateVolumes(IEnumerable<int> volumes, bool send = true)
        {
            Updates.Add(volumes.ToList());
            if (!send && SelectionOnInitialUpdate is int slot)
            {
                SelectionOnInitialUpdate = null;
                SelectionReceived?.Invoke(slot);
            }
        }
        public void EmitVolume(int slot, int value) => VolumeReceived?.Invoke(slot, value);
        public void EmitSelection(int slot) => SelectionReceived?.Invoke(slot);
        public void Dispose() => stopped.TrySetResult();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "DeskFader.Core.Tests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(directory);

        public string FilePath(string name) => Path.Combine(directory, name);
        public string DirectoryPath => directory;

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
