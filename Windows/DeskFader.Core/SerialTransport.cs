using System.IO.Ports;

namespace DeskFader.Core;

public interface IDeskFaderTransport : IDisposable
{
    event Action<int, int>? VolumeReceived;
    event Action<int>? SelectionReceived;
    Task RunAsync(CancellationToken cancellationToken);
    void UpdateVolumes(IEnumerable<int> volumes, bool send = true);
}

public sealed class SerialTransport(Action<string>? log = null) : IDeskFaderTransport
{
    private readonly object gate = new();
    private readonly Action<string> log = log ?? (_ => { });
    private List<int> volumes = Enumerable.Repeat(0, DeskFaderConstants.SlotCount).ToList();
    private SerialPort? port;
    private int sequence;
    private bool paired;

    public event Action<int, int>? VolumeReceived;
    public event Action<int>? SelectionReceived;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await ConnectAsync(cancellationToken))
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }
            var reader = new LineReader();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    SerialPort? active;
                    lock (gate) active = port;
                    if (active?.IsOpen != true) break;
                    byte[]? frame;
                    try { frame = reader.Read(active); } catch (TimeoutException) { continue; }
                    if (frame is not null) Handle(frame);
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException)
            {
                log($"Serial read failed: {ex.Message}");
            }
            finally
            {
                Close();
            }
        }
    }

    public void UpdateVolumes(IEnumerable<int> newVolumes, bool send = true)
    {
        lock (gate) volumes = newVolumes.ToList();
        if (send) SendState();
    }

    private async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        foreach (var name in SerialPort.GetPortNames())
        {
            var candidate = new SerialPort(name, 115200)
            {
                ReadTimeout = 200,
                WriteTimeout = 1000,
                DtrEnable = true,
                RtsEnable = true,
            };
            try
            {
                candidate.Open();
                log($"Connected to {name}; awaiting device hello");
                var deadline = DateTime.UtcNow.AddSeconds(3);
                var reader = new LineReader();
                while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
                {
                    byte[]? frame;
                    try { frame = reader.Read(candidate); } catch (TimeoutException) { continue; }
                    if (frame is null) continue;
                    try
                    {
                        if (Protocol.Decode(frame).Type != "hello") continue;
                        lock (gate) { port = candidate; paired = true; }
                        SendState();
                        return true;
                    }
                    catch (ProtocolException) { }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException) { log($"Could not open {name}: {ex.Message}"); }
            candidate.Dispose();
        }
        await Task.CompletedTask;
        return false;
    }

    private void Handle(byte[] raw)
    {
        try
        {
            var message = Protocol.Decode(raw);
            if (message.Type == "volume") VolumeReceived?.Invoke(message.Slot!.Value, message.Value!.Value);
            else if (message.Type == "select") SelectionReceived?.Invoke(message.Slot!.Value);
            else if (message.Type == "error") log($"Device error {message.Code}: {message.Message}");
        }
        catch (ProtocolException ex) { log($"Ignored invalid device frame: {ex.Message}"); }
    }

    private void SendState()
    {
        lock (gate)
        {
            if (!paired || port?.IsOpen != true) return;
            sequence = (sequence + 1) % int.MaxValue;
            try
            {
                var bytes = Protocol.Encode(new ProtocolMessage { Type = "state", Seq = sequence, Volumes = [.. volumes] });
                port.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                log($"Serial write failed: {ex.Message}");
                Close();
            }
        }
    }

    private void Close()
    {
        lock (gate)
        {
            paired = false;
            var active = port;
            port = null;
            try { active?.Dispose(); }
            catch { }
        }
    }

    public void Dispose() => Close();

    private sealed class LineReader
    {
        private readonly List<byte> buffer = [];
        private bool discarding;

        public byte[]? Read(SerialPort source)
        {
            var value = source.ReadByte();
            if (value != '\n')
            {
                if (discarding) return null;
                if (buffer.Count == DeskFaderConstants.MaxFrameBytes && value != '\r')
                {
                    buffer.Clear();
                    discarding = true;
                    return null;
                }
                buffer.Add((byte)value);
                return null;
            }

            if (discarding)
            {
                discarding = false;
                return null;
            }
            if (buffer.Count > 0 && buffer[^1] == '\r') buffer.RemoveAt(buffer.Count - 1);
            var frame = buffer.ToArray();
            buffer.Clear();
            return frame;
        }
    }
}
