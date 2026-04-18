namespace hg5c_cam.Models;

public class GlobalSettings
{
    public bool EnableSound { get; set; } = true;
    public string AudioOutputDeviceName { get; set; } = string.Empty;
    public int SoundLevel { get; set; } = 100;
    public int SplitPlaybackCameraCount { get; set; } = 1;
    public bool AlwaysMaximizedPlayback { get; set; }
    public bool TopmostMainWindow { get; set; }
    public int UseSecondStream { get; set; } = 0;
    public int LastUsedCameraSlot { get; set; } = 1;
}
