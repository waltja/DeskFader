using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace DeskFader.Core;

public interface IAudioSessionProvider
{
    IReadOnlyList<string> GetActiveApplications();
    void ApplyVolumes(IReadOnlyList<Slot> slots, IReadOnlyList<int> desired, Action<string> log);
}

public sealed class CoreAudioSessionProvider : IAudioSessionProvider
{
    public IReadOnlyList<string> GetActiveApplications()
    {
        var result = new List<string>();
        EnumerateSessions((processName, _) => result.Add(processName));
        return result;
    }

    public void ApplyVolumes(IReadOnlyList<Slot> slots, IReadOnlyList<int> desired, Action<string> log)
    {
        EnumerateSessions((processName, session) =>
        {
            for (var index = 0; index < slots.Count; index++)
            {
                if (!string.Equals(processName, slots[index].Process, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var volume = session.SimpleAudioVolume;
                    volume.Volume = desired[index] / 100f;
                }
                catch (Exception ex) { log($"Could not set {slots[index].Process}: {ex.Message}"); }
            }
        });
    }

    private static void EnumerateSessions(Action<string, AudioSessionControl> action)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var index = 0; index < sessions.Count; index++)
                {
                    using var session = sessions[index];
                    try
                    {
                        var processId = session.GetProcessID;
                        if (processId == 0) continue;
                        using var process = Process.GetProcessById((int)processId);
                        action(process.ProcessName + ".exe", session);
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
            }
        }
    }
}

public sealed class VolumeController
{
    private readonly object gate = new();
    private readonly IAudioSessionProvider provider;
    private List<Slot> slots;
    private List<int> desired;
    private readonly Action<string> log;

    public VolumeController(IEnumerable<Slot> slots, IAudioSessionProvider? provider = null, Action<string>? log = null)
    {
        this.slots = SlotValidator.Validate(slots);
        desired = this.slots.Select(x => x.DefaultVolume).ToList();
        this.provider = provider ?? new CoreAudioSessionProvider();
        this.log = log ?? (_ => { });
    }

    public List<int> DesiredSnapshot() { lock (gate) return [.. desired]; }
    public List<Slot> SlotsSnapshot() { lock (gate) return slots.Select(x => new Slot { Process = x.Process, DefaultVolume = x.DefaultVolume }).ToList(); }
    public IReadOnlyList<string> ActiveApplications() => provider.GetActiveApplications().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    public void SetDesiredVolume(int slot, int value)
    {
        if (slot is < 0 or >= DeskFaderConstants.SlotCount || value is < 0 or > 100) throw new ArgumentException("invalid slot or volume");
        lock (gate) desired[slot] = value;
        Reconcile();
    }

    public void ReplaceSlots(IEnumerable<Slot> replacement)
    {
        var validated = SlotValidator.Validate(replacement);
        lock (gate) slots = validated;
        Reconcile();
    }

    public void Reconcile()
    {
        lock (gate)
        {
            try { provider.ApplyVolumes(slots, desired, log); }
            catch (Exception ex) { log($"Could not enumerate audio sessions: {ex.Message}"); }
        }
    }
}
