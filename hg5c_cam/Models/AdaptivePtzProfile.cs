namespace hg5c_cam.Models;

public sealed class AdaptivePtzProfile
{
    public double NormalPanTiltMinStep { get; init; }
    public double FastPanTiltMinStep { get; init; }
    public double NormalZoomMinStep { get; init; }
    public double FastZoomMinStep { get; init; }
    public double MaxScaleAtWide { get; init; }
    public double MaxScaleAtTele { get; init; }
}
