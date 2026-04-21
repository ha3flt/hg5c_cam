namespace hg5c_cam.Services;

public static class LocalizationService
{
    private static readonly RegistryService RegistryService = new();
    public const string English = "en";
    public const string Hungarian = "hu";

    public static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return English;
        }

        var normalized = language.Trim().ToLowerInvariant();
        return normalized switch
        {
            "hu" or "magyar" => Hungarian,
            "en" or "english" => English,
            _ => English
        };
    }

    public static string GetLanguageDisplayName(string language)
    {
        return NormalizeLanguage(language) == Hungarian ? "magyar" : "English";
    }

    public static string TranslateCurrent(string key)
    {
        return Translate(RegistryService.LoadLanguage(), key);
    }

    public static IReadOnlyList<LanguageOption> GetLanguageOptions()
    {
        return
        [
            new LanguageOption(Hungarian, "magyar"),
            new LanguageOption(English, "English")
        ];
    }

    public static string Translate(string language, string key)
    {
        var isHu = NormalizeLanguage(language) == Hungarian;
        if (!isHu)
        {
            return key switch
            {
                "Settings" => "Settings",
                "CameraSettings" => "Camera settings",
                "GlobalSettings" => "Program settings",
                "File" => "_File",
                "SettingsMenu" => "_Settings",
                "CameraSettingsMenu" => "_Camera settings",
                "GlobalSettingsMenu" => "_Program settings",
                "Import" => "_Import camera settings",
                "Export" => "E_xport camera settings",
                "Defaults" => "Clea_r camera settings",
                "ConfirmClearSettings" => "Do you really want to clear the camera settings?",
                "ConfirmClearSettingsTitle" => "Clear settings",
                "Exit" => "_Exit",
                "Camera" => "_Camera",
                "View" => "_View",
                "StartStream" => "_Start stream",
                "StopStream" => "Sto_p stream",
                "HighQuality" => "_High quality",
                "LowBandwidth" => "_Low bandwidth",
                "StreamSound" => "Strea_m sound",
                "TopmostWindow" => "_Topmost window",
                "FpsOverlay" => "Status _overlay",
                "ToolbarStartStop" => "Start/stop stream",
                "ToolbarQuality" => "High/Low quality",
                "ToolbarSound" => "Stream sound on/off",
                "ToolbarGlobalSound" => "Program sounds on/off",
                "ToolbarTopmost" => "Topmost window on/off",
                "ToolbarFps" => "Status overlay on/off",
                "ToolbarOn" => "ON",
                "ToolbarOff" => "ON",
                "ToolbarSplitCameraCount" => "Number of cameras visible together",
                "ToolbarActiveCamera" => "Select active camera",
                "Presets" => "_Presets",
                "Help" => "_Help",
                "About" => "_About",
                "Navigation" => "Navigation",
                "Disconnected" => "Disconnected",
                "Connecting" => "Connecting...",
                "ConnectingAttempt" => "Connecting (attempt {0})...",
                "ConnectingToCamAttempt" => "Connecting to Cam{0} ({1})...",
                "Disconnecting" => "Disconnecting...",
                "DisconnectingFromCam" => "Disconnecting from Cam{0}...",
                "DisconnectedFromCam" => "Disconnected from Cam{0}",
                "ResolvingOnvifStream" => "Resolving ONVIF stream...",
                "ResolvingOnvifStreamForCam" => "Resolving ONVIF stream for Cam{0}...",
                "OnvifResolutionFailed" => "ONVIF resolution failed",
                "OnvifResolutionFailedForCam" => "ONVIF resolution failed for Cam{0}",
                "FailedToStartPlayback" => "Failed to start playback",
                "FailedToStartPlaybackForCam" => "Failed to start playback for Cam{0}",
                "CameraLabel" => "Camera",
                "CameraName" => "Camera name",
                "Size" => "Size",
                "Memory" => "Mem.",
                "UseOnvif" => "Use ONVIF",
                "AutoResolveOnvif" => "Resolve RTSP from ONVIF before playback",
                "OnvifDeviceServiceUrl" => "ONVIF device service URL",
                "OnvifProfileToken" => "ONVIF profile token (optional)",
                "Username" => "Name",
                "Password" => "Password",
                "ResolveOnvif" => "Resolve RTSP from ONVIF",
                "RtspUrl" => "RTSP URL",
                "PictureSize" => "Picture size",
                "AspectRatio" => "Aspect ratio",
                "StreamSoundSimple" => "Stream sound",
                "EnableSound" => "Enable sounds",
                "SoundLevel" => "Sound level",
                "SplitPlaybackCameraCount" => "Number of cameras shown simultaneously",
                "AlwaysMaximizedPlayback" => "Always maximized window",
                "TopmostMainWindow" => "Topmost main window",
                "Refresh" => "Refresh",
                "DefaultAudioDevice" => "Default audio device",
                "Language" => "Language",
                "MaxFps" => "Max. displayed FPS (0 = unlimited)",
                "ConnectionDelay" => "Connection delay (sec)",
                "ConnectionRetries" => "Connection retries",
                "NetworkTimeout" => "Network timeout (sec)",
                "TopmostWindowSimple" => "Topmost window",
                "FpsOverlaySimple" => "Status overlay",
                "FpsOverlayPosition" => "Status overlay position",
                "Maximized" => "Maximized playback window",
                "Ok" => "OK",
                "Yes" => "Yes",
                "No" => "No",
                "Cancel" => "Cancel",
                "Resolving" => "Resolving...",
                "ResolvedRtspPort" => "Resolved, RTSP port: {0}",
                "PleaseProvideOnvif" => "Please provide a valid ONVIF device service URL.",
                "OnvifResolutionFailedWithError" => "ONVIF resolution failed: {0}",
                "HelpBody" => "Under construction...",
                "AboutBody" => "ONVIF RTSP camera viewer by HA3FLT",
                "Framework" => ".NET 8 WPF on Win 7+, FFmpeg",
                "Version" => "Version",
                "InstanceAlreadyRunning" => "An instance with number {0} is already running.",
                "PasswordReentryRequired" => "Some camera passwords are encrypted but cannot be decoded with the current key (or contain {0}). Please re-enter all affected camera passwords.",
                "PasswordBraceSequenceInvalid" => "The password cannot contain '{{' or '}}' character sequences.",
                "Auto" => "Auto",
                "TopLeft" => "Top left",
                "TopRight" => "Top right",
                "BottomLeft" => "Bottom left",
                "BottomRight" => "Bottom right",
                "ExceptionOnvifDisabled" => "ONVIF is disabled.",
                "ExceptionOnvifResolvePresetsFailed" => "Unable to resolve camera presets from ONVIF endpoint.",
                "ExceptionOnvifPresetTokenRequired" => "Preset token is required.",
                "ExceptionOnvifMoveToPresetFailed" => "Unable to move camera to preset.",
                "ExceptionOnvifMoveRelativeFailed" => "Unable to move camera relatively.",
                "ExceptionOnvifZoomRelativeFailed" => "Unable to zoom camera relatively.",
                "ExceptionOnvifInvalidRtspUriInResponse" => "ONVIF response does not contain a valid RTSP URI.",
                "ExceptionOnvifResolveRtspFailedWithError" => "Unable to resolve RTSP stream from ONVIF endpoint. {0}",
                "ExceptionOnvifResolveProfilesFailedWithError" => "Unable to resolve ONVIF profiles. {0}",
                "ExceptionOnvifNoValidEndpoint" => "No valid ONVIF device service endpoint is configured.",
                "ExceptionOnvifServiceUrlInvalid" => "ONVIF {0} service URL is invalid.",
                "ExceptionOnvifMediaUrlMissing" => "ONVIF Media service URL was not found in capabilities response.",
                "ExceptionOnvifPtzUrlMissing" => "ONVIF PTZ service URL was not found in capabilities response.",
                "ExceptionOnvifNoMediaProfile" => "No ONVIF media profile was found.",
                "ExceptionOnvifGetStreamUriMissing" => "ONVIF GetStreamUri response does not contain an RTSP URI.",
                "ExceptionOnvifResolvePtzContextFailedWithError" => "Unable to resolve ONVIF PTZ context. {0}",
                "ExceptionOnvifRequestFailed" => "ONVIF request failed ({0} {1}).",
                "ExceptionOnvifSoapFault" => "ONVIF SOAP fault received.",
                "ExceptionNoFreeInstanceSlot" => "No free instance slot found.",
                "ExceptionInstanceSlotRange" => "Instance slot must be between 1 and {0}.",
                "ExceptionRegistryKeyCreateFailed" => "Could not create registry key for instance {0}.",
                "ExceptionPlayerOpenRtspFailed" => "Could not open RTSP stream.",
                "ExceptionPlayerReadStreamInfoFailed" => "Could not read stream info.",
                "ExceptionPlayerNoVideoStream" => "No video stream found.",
                "ExceptionPlayerNoDecoder" => "No decoder found for video stream.",
                "ExceptionPlayerAllocateCodecContextFailed" => "Could not allocate codec context.",
                "ExceptionPlayerCopyCodecParametersFailed" => "Could not copy codec parameters.",
                "ExceptionPlayerOpenCodecFailed" => "Could not open codec.",
                "ExceptionPlayerInvalidVideoDimensions" => "Invalid video dimensions.",
                "ExceptionPlayerInitializeScalerFailed" => "Could not initialize scaler.",
                "ExceptionPlayerAllocateFramePacketFailed" => "Could not allocate FFmpeg frame/packet.",
                "ExceptionPlayerReadPacketFailed" => "Error reading packet.",
                "ExceptionPlayerSendPacketFailed" => "Error sending packet to decoder.",
                "ExceptionPlayerReceiveFrameFailed" => "Error receiving decoded frame.",
                "ExceptionPlayerFfmpegError" => "FFmpeg error {0}",
                _ => key
            };
        }

        return key switch
        {
            "Settings" => "Beállítások",
            "CameraSettings" => "A kamera beállításai",
            "GlobalSettings" => "Programbeállítások",
            "File" => "_Fájl",
            "SettingsMenu" => "_Beállítások",
            "CameraSettingsMenu" => "A _kamera beállításai",
            "GlobalSettingsMenu" => "_Programbeállítások",
            "Import" => "Kameraadatok _betöltése",
            "Export" => "Kameraadatok fájlba _mentése",
            "Defaults" => "Saját kameraadatok tö_rlése",
            "ConfirmClearSettings" => "Biztosan törölni szeretnéd a kamerabeállításokat?",
            "ConfirmClearSettingsTitle" => "Beállítások törlése",
            "Exit" => "_Kilépés",
            "Camera" => "_Kamera",
            "View" => "_Nézet",
            "StartStream" => "_Lejátszás indítása",
            "StopStream" => "Lejá_tszás leállítása",
            "HighQuality" => "_Magas minőség",
            "LowBandwidth" => "_Alacsony forgalom",
            "StreamSound" => "_Hang lejátszása",
            "TopmostWindow" => "Mindig _felül",
            "FpsOverlay" => "_Státuszkijelzés",
            "ToolbarStartStop" => "Lejátszás indítása/leállítása",
            "ToolbarQuality" => "Magas minőség be/ki",
            "ToolbarSound" => "Hang lejátszása be/ki",
            "ToolbarGlobalSound" => "Programhangok be/ki",
            "ToolbarTopmost" => "Mindig felül be/ki",
            "ToolbarFps" => "Státuszkiírás be/ki",
            "ToolbarOn" => "BE",
            "ToolbarOff" => "BE",
            "ToolbarSplitCameraCount" => "Egyszerre látható kamerák száma",
            "ToolbarActiveCamera" => "Aktív kamera kiválasztása",
            "Presets" => "_Presetek",
            "Help" => "_Súgó",
            "About" => "_Névjegy",
            "Navigation" => "Navigáció",
            "Disconnected" => "Nincs kapcsolat",
            "Connecting" => "Kapcsolódás...",
            "ConnectingAttempt" => "Kapcsolódás ({0}. próbálkozás)...",
            "ConnectingToCamAttempt" => "Kapcsolódás a(z) {0}. kamerához ({1})...",
            "Disconnecting" => "Kapcsolat bontása...",
            "DisconnectingFromCam" => "Leválasztás (z) {0}. kameráról...",
            "DisconnectedFromCam" => "Nincs kapcsolat a(z) {0}. kamerával",
            "ResolvingOnvifStream" => "ONVIF képfolyam feloldása...",
            "ResolvingOnvifStreamForCam" => "ONVIF képfolyam feloldása a(z) {0}. kamerához...",
            "OnvifResolutionFailed" => "ONVIF feloldás sikertelen",
            "OnvifResolutionFailedForCam" => "ONVIF feloldás sikertelen a(z) {0}. kameránál",
            "FailedToStartPlayback" => "A lejátszás indítása sikertelen",
            "FailedToStartPlaybackForCam" => "A lejátszás indítása sikertelen a(z) {0}. kameránál",
            "CameraLabel" => "Kamera",
            "CameraName" => "Kameranév",
            "Size" => "Méret",
            "Memory" => "Mem.",
            "UseOnvif" => "ONVIF használata",
            "AutoResolveOnvif" => "RTSP feloldása ONVIF alapján lejátszás előtt",
            "OnvifDeviceServiceUrl" => "ONVIF eszközszolgáltatás URL",
            "OnvifProfileToken" => "ONVIF profil token (opcionális)",
            "Username" => "Felhasználónév",
            "Password" => "Jelszó",
            "ResolveOnvif" => "RTSP feloldása ONVIF alapján",
            "RtspUrl" => "RTSP URL",
            "PictureSize" => "Képméret",
            "AspectRatio" => "Képarány",
            "StreamSoundSimple" => "Hang lejátszása",
            "EnableSound" => "Hangok engedélyezése",
            "SoundLevel" => "Hangerő",
            "SplitPlaybackCameraCount" => "Egy időben megjelenített kamerák száma",
            "AlwaysMaximizedPlayback" => "Mindig teljes méretű ablak",
            "TopmostMainWindow" => "Főablak mindig felül",
            "Refresh" => "Frissítés",
            "DefaultAudioDevice" => "Alapértelmezett hangkimenet",
            "Language" => "Nyelv",
            "MaxFps" => "Max. lejátszott FPS (0 = korlátlan)",
            "ConnectionDelay" => "Kapcsolódási késleltetés (mp)",
            "ConnectionRetries" => "Kapcsolódási próbálkozások",
            "NetworkTimeout" => "Hálózati időkorlát (mp)",
            "TopmostWindowSimple" => "Mindig felül",
            "FpsOverlaySimple" => "Státuszkijelzés",
            "FpsOverlayPosition" => "Státuszkijelzés pozíciója",
            "Maximized" => "Lejátszás teljes méretű ablakban",
            "Ok" => "OK",
            "Yes" => "Igen",
            "No" => "Nem",
            "Cancel" => "Mégse",
            "Resolving" => "Folyamatban...",
            "ResolvedRtspPort" => "Feloldva, RTSP port: {0}",
            "PleaseProvideOnvif" => "Adj meg egy érvényes ONVIF eszközszolgáltatás-URL-t.",
            "OnvifResolutionFailedWithError" => "ONVIF-feloldás sikertelen: {0}",
            "HelpBody" => "Fejlesztés alatt...",
            "AboutBody" => "RTSP kamerakép-megjelenítő, készítette: HA3FLT",
            "Framework" => ".NET 8 WPF Win 7+ rendszeren, FFmpeg",
            "Version" => "Verzió",
            "InstanceAlreadyRunning" => "A(z) {0}. példány már fut.",
            "PasswordReentryRequired" => "Néhány kamera-jelszó titkosítva van, de a jelenlegi kulccsal nem dekódolható (vagy tartalmazza ezt: {0}). Add meg újra az összes érintett kamera jelszavát.",
            "PasswordBraceSequenceInvalid" => "A jelszó nem tartalmazhat '{{' vagy '}}' karaktersorozatot.",
            "Auto" => "Automatikus",
            "TopLeft" => "Bal felső",
            "TopRight" => "Jobb felső",
            "BottomLeft" => "Bal alsó",
            "BottomRight" => "Jobb alsó",
            "ExceptionOnvifDisabled" => "Az ONVIF le van tiltva.",
            "ExceptionOnvifResolvePresetsFailed" => "Nem sikerült lekérdezni a kamera-preseteket az ONVIF végpontról.",
            "ExceptionOnvifPresetTokenRequired" => "A preset token megadása kötelező.",
            "ExceptionOnvifMoveToPresetFailed" => "Nem sikerült a kamerát a kiválasztott presetre mozgatni.",
            "ExceptionOnvifMoveRelativeFailed" => "Nem sikerült a kamera relatív mozgatása.",
            "ExceptionOnvifZoomRelativeFailed" => "Nem sikerült a kamera relatív zoomolása.",
            "ExceptionOnvifInvalidRtspUriInResponse" => "Az ONVIF válasz nem tartalmaz érvényes RTSP URI-t.",
            "ExceptionOnvifResolveRtspFailedWithError" => "Nem sikerült feloldani az RTSP képfolyamot az ONVIF végpontról. {0}",
            "ExceptionOnvifResolveProfilesFailedWithError" => "Nem sikerült lekérdezni az ONVIF profilokat. {0}",
            "ExceptionOnvifNoValidEndpoint" => "Nincs érvényes ONVIF eszközszolgáltatás-végpont beállítva.",
            "ExceptionOnvifServiceUrlInvalid" => "Az ONVIF {0} szolgáltatás-URL érvénytelen.",
            "ExceptionOnvifMediaUrlMissing" => "Az ONVIF képességválaszban nem található Media szolgáltatás-URL.",
            "ExceptionOnvifPtzUrlMissing" => "Az ONVIF képességválaszban nem található PTZ szolgáltatás-URL.",
            "ExceptionOnvifNoMediaProfile" => "Nem található ONVIF médiaprofil.",
            "ExceptionOnvifGetStreamUriMissing" => "Az ONVIF GetStreamUri válasz nem tartalmaz RTSP URI-t.",
            "ExceptionOnvifResolvePtzContextFailedWithError" => "Nem sikerült feloldani az ONVIF PTZ-kontextust. {0}",
            "ExceptionOnvifRequestFailed" => "Az ONVIF kérés sikertelen ({0} {1}).",
            "ExceptionOnvifSoapFault" => "ONVIF SOAP hiba érkezett.",
            "ExceptionNoFreeInstanceSlot" => "Nem található szabad példányhely.",
            "ExceptionInstanceSlotRange" => "A példányhely értéke 1 és {0} közé kell essen.",
            "ExceptionRegistryKeyCreateFailed" => "Nem hozható létre registry kulcs a(z) {0}. példányhoz.",
            "ExceptionPlayerOpenRtspFailed" => "Nem sikerült megnyitni az RTSP képfolyamot.",
            "ExceptionPlayerReadStreamInfoFailed" => "Nem sikerült beolvasni a képfolyam információit.",
            "ExceptionPlayerNoVideoStream" => "Nem található videó képfolyam.",
            "ExceptionPlayerNoDecoder" => "Nem található dekóder a videó képfolyamhoz.",
            "ExceptionPlayerAllocateCodecContextFailed" => "Nem sikerült lefoglalni a kodekkontextust.",
            "ExceptionPlayerCopyCodecParametersFailed" => "Nem sikerült átmásolni a kodekparamétereket.",
            "ExceptionPlayerOpenCodecFailed" => "Nem sikerült megnyitni a kodeket.",
            "ExceptionPlayerInvalidVideoDimensions" => "Érvénytelen videóméretek.",
            "ExceptionPlayerInitializeScalerFailed" => "Nem sikerült inicializálni a skálázót.",
            "ExceptionPlayerAllocateFramePacketFailed" => "Nem sikerült lefoglalni az FFmpeg keret/csomag-erőforrásokat.",
            "ExceptionPlayerReadPacketFailed" => "Hiba a csomag olvasása közben.",
            "ExceptionPlayerSendPacketFailed" => "Hiba a csomag dekódernek küldése közben.",
            "ExceptionPlayerReceiveFrameFailed" => "Hiba a dekódolt képkocka fogadása közben.",
            "ExceptionPlayerFfmpegError" => "FFmpeg hiba: {0}",
            _ => key
        };
    }

    public static string GetDefaultCameraName(string language, int slot)
    {
        var safeSlot = slot > 0 ? slot : 1;
        return $"{Translate(language, "CameraLabel")} {safeSlot}";
    }

    public static string LocalizeAspectRatioMode(string language, string mode)
    {
        return string.Equals(mode, Models.AppSettings.AutoAspectRatio, StringComparison.OrdinalIgnoreCase)
            ? Translate(language, "Auto")
            : mode;
    }

    public static string LocalizeFpsOverlayPosition(string language, string position)
    {
        return position switch
        {
            Models.AppSettings.FpsOverlayPositionTopLeft => Translate(language, "TopLeft"),
            Models.AppSettings.FpsOverlayPositionTopRight => Translate(language, "TopRight"),
            Models.AppSettings.FpsOverlayPositionBottomLeft => Translate(language, "BottomLeft"),
            Models.AppSettings.FpsOverlayPositionBottomRight => Translate(language, "BottomRight"),
            _ => position
        };
    }

    public sealed record LanguageOption(string Value, string Label);
}
