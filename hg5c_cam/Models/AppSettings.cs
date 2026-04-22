namespace hg5c_cam.Models;

public class AppSettings
{
    public const string AutoAspectRatio = "Auto";
    public const string FpsOverlayPositionTopLeft = "Top left";
    public const string FpsOverlayPositionTopRight = "Top right";
    public const string FpsOverlayPositionBottomLeft = "Bottom left";
    public const string FpsOverlayPositionBottomRight = "Bottom right";

    public string CameraName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool UseOnvif { get; set; } = true;
    public string OnvifDeviceServiceUrl { get; set; } = string.Empty;
    public string OnvifProfileToken { get; set; } = string.Empty;
    public bool AutoResolveRtspFromOnvif { get; set; } = true;
    public int ReconnectDelaySec { get; set; } = 3;
    public int ConnectionRetries { get; set; } = 25;
    public int NetworkTimeoutSec { get; set; } = 5;
    public int MaxFps { get; set; } = 0;
    public bool ShowFpsOverlay { get; set; }
    public string FpsOverlayPosition { get; set; } = FpsOverlayPositionBottomLeft;
    public bool SoundEnabled { get; set; }
    public int SoundLevel { get; set; } = 100;
    public string AspectRatioMode { get; set; } = AutoAspectRatio;
    public int? OnvifXSize { get; set; }
    public int? OnvifYSize { get; set; }
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
}
