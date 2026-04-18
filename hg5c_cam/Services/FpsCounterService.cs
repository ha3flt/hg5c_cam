using System.Diagnostics;
using System.Windows.Threading;

namespace hg5c_cam.Services;

public class FpsCounterService
{
    private readonly Func<long> _frameCountAccessor;
    private readonly Func<long> _packetBytesAccessor;
    private readonly DispatcherTimer _timer;

    public int CurrentFps { get; private set; }
    public double CurrentMemoryMb { get; private set; }
    public double CurrentBitrateKbps { get; private set; }

    public event Action<int>? FpsChanged;
    public event Action<double>? MemoryChanged;

    public FpsCounterService(Func<long> frameCountAccessor, Func<long> packetBytesAccessor)
    {
        this._frameCountAccessor = frameCountAccessor;
        this._packetBytesAccessor = packetBytesAccessor;
        this._timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        this._timer.Tick += (_, _) => Tick();
    }

    public void Start() => this._timer.Start();
    public void Stop() => this._timer.Stop();

    private void Tick()
    {
        CurrentFps = (int)this._frameCountAccessor();
        var bytesPerSecond = this._packetBytesAccessor();
        CurrentBitrateKbps = bytesPerSecond > 0 ? bytesPerSecond * 8d / 1000d : 0d;
        CurrentMemoryMb = Process.GetCurrentProcess().PrivateMemorySize64 / (1024d * 1024d);
        FpsChanged?.Invoke(CurrentFps);
        MemoryChanged?.Invoke(CurrentMemoryMb);
    }
}
