namespace hg5c_cam.Models;

public sealed class OnvifPtzCapabilities
{
    public bool HasPan { get; init; }
    public bool HasTilt { get; init; }
    public bool HasZoom { get; init; }
    public bool HasPanTilt => this.HasPan || this.HasTilt;

    public double? PanMin { get; init; }
    public double? PanMax { get; init; }
    public double? TiltMin { get; init; }
    public double? TiltMax { get; init; }
    public double? ZoomMin { get; init; }
    public double? ZoomMax { get; init; }

    public double? PanMinStep { get; init; }
    public double? TiltMinStep { get; init; }
    public double? ZoomMinStep { get; init; }
    public bool PanStepUsesDefault { get; init; }
    public bool TiltStepUsesDefault { get; init; }
    public bool ZoomStepUsesDefault { get; init; }

    public double? CurrentZoom { get; init; }
    public double? CurrentZoomNormalized { get; init; }
}
