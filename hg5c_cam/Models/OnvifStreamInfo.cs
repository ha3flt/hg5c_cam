namespace hg5c_cam.Models;

public class OnvifStreamInfo
{
    public string DeviceServiceUrl { get; init; } = string.Empty;
    public string MediaServiceUrl { get; init; } = string.Empty;
    public string RtspUri { get; init; } = string.Empty;
    public int RtspPort { get; init; }
    public string ProfileToken { get; init; } = string.Empty;
    public int? StreamWidth { get; init; }
    public int? StreamHeight { get; init; }
}
