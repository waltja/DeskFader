namespace DeskFader.Core;

public sealed class DeskFaderService : IDisposable
{
    private readonly object gate = new();
    private readonly SettingsStore store;
    private readonly IAudioSessionProvider audio;
    private readonly IDeskFaderTransport transport;
    private readonly Action<string> log;
    private CancellationTokenSource? stop;
    private Task? reconciliationTask;
    private VolumeController? controller;
    private SettingsDocument? settings;
    private string? error;
    private int? selectedSlot;
    private bool volumesPendingPersistence;
    private DateTime lastVolumeUpdate = DateTime.MinValue;

    public DeskFaderService(SettingsStore store, IAudioSessionProvider audio, IDeskFaderTransport transport, Action<string>? log = null)
    {
        this.store = store;
        this.audio = audio;
        this.transport = transport;
        this.log = log ?? (_ => { });
    }

    public event Action<ServiceState>? StateChanged;

    public void Start()
    {
        lock (gate)
        {
            if (stop is not null) return;
            settings = store.Load();
            controller = new VolumeController(settings.Slots, audio, log);
            stop = new CancellationTokenSource();
            transport.VolumeReceived += OnDeviceVolume;
            transport.SelectionReceived += OnDeviceSelection;
            transport.UpdateVolumes(controller.DesiredSnapshot(), send: false);
            var cancellation = stop;
            reconciliationTask = Task.Run(() => ReconcileAsync(cancellation.Token));
        }
        PublishState();
    }

    public async Task StopAsync()
    {
        Task? task;
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (stop is null) return;
            cancellation = stop;
            cancellation.Cancel();
            task = reconciliationTask;
            transport.VolumeReceived -= OnDeviceVolume;
            transport.SelectionReceived -= OnDeviceSelection;
            FlushPendingVolumes(force: true);
            transport.Dispose();
        }
        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
        lock (gate)
        {
            if (!ReferenceEquals(stop, cancellation)) return;
            stop = null;
            reconciliationTask = null;
            controller = null;
            settings = null;
            selectedSlot = null;
            cancellation.Dispose();
        }
        PublishState(running: false);
    }

    public void ApplyConfiguration(IEnumerable<Slot> slots, bool startAtLogin)
    {
        var replacement = SlotValidator.Validate(slots);
        lock (gate)
        {
            EnsureRunning();
            var desired = controller!.DesiredSnapshot();
            replacement = replacement.Select((slot, index) => new Slot { Process = slot.Process, DefaultVolume = desired[index] }).ToList();
            controller.ReplaceSlots(replacement);
            settings = new SettingsDocument { Slots = replacement, StartAtLogin = startAtLogin };
            store.Save(settings);
            transport.UpdateVolumes(desired);
        }
        PublishState();
    }

    public ServiceState CurrentState()
    {
        lock (gate) return CreateState(stop is not null);
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var serialTask = transport.RunAsync(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (gate) controller?.Reconcile();
                lock (gate) FlushPendingVolumes();
                if (!cancellationToken.IsCancellationRequested) PublishState();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (!cancellationToken.IsCancellationRequested) error = ex.Message;
            }
            if (!cancellationToken.IsCancellationRequested)
            {
                log($"Service reconciliation failed: {ex.Message}");
                PublishState();
            }
        }
        finally
        {
            try { await serialTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                lock (gate)
                {
                    if (!cancellationToken.IsCancellationRequested) error = $"Serial transport stopped unexpectedly: {ex.Message}";
                }
            }
        }
    }

    private void OnDeviceVolume(int slot, int value)
    {
        lock (gate)
        {
            if (stop is null || stop.IsCancellationRequested || controller is null || settings is null) return;
            controller.SetDesiredVolume(slot, value);
            var desired = controller.DesiredSnapshot();
            settings.Slots = controller.SlotsSnapshot().Select((mapped, index) => new Slot { Process = mapped.Process, DefaultVolume = desired[index] }).ToList();
            volumesPendingPersistence = true;
            lastVolumeUpdate = DateTime.UtcNow;
            FlushPendingVolumes();
            transport.UpdateVolumes(desired);
        }
        PublishState();
    }

    private void OnDeviceSelection(int slot)
    {
        lock (gate)
        {
            if (stop is null || stop.IsCancellationRequested || slot is < 0 or >= DeskFaderConstants.SlotCount) return;
            selectedSlot = slot;
        }
        PublishState();
    }

    private void FlushPendingVolumes(bool force = false)
    {
        if (!volumesPendingPersistence || settings is null) return;
        if (!force && DateTime.UtcNow - lastVolumeUpdate < TimeSpan.FromSeconds(1)) return;
        store.Save(settings);
        volumesPendingPersistence = false;
    }

    private ServiceState CreateState(bool running)
    {
        if (controller is null) return new ServiceState { Running = running, Error = error, UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d };
        List<string> activeApps;
        try { activeApps = controller.ActiveApplications().ToList(); }
        catch (Exception ex) { activeApps = []; error ??= $"Could not enumerate audio sessions: {ex.Message}"; }
        return new ServiceState { Running = running, Slots = controller.SlotsSnapshot(), Volumes = controller.DesiredSnapshot(), SelectedSlot = selectedSlot, ActiveApps = activeApps, Error = error, UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d };
    }

    private void PublishState(bool? running = null)
    {
        lock (gate)
        {
            if (running is null && (stop is null || stop.IsCancellationRequested)) return;
        }
        var state = CurrentState();
        if (running is not null) state.Running = running.Value;
        StateChanged?.Invoke(state);
    }

    private void EnsureRunning()
    {
        if (controller is null || settings is null) throw new InvalidOperationException("DeskFader service is not running");
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
