using hg5c_cam.Models;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace hg5c_cam.Services;

public class RegistryService
{
    public const int MaxInstanceSlots = 16;
    public const int PrimaryStreamNumber = 1;
    public const int SecondaryStreamNumber = 2;
    public const string PasswordNoKeyMarker = "{{{NO KEY}}}";
    private const string RootPath = @"Software\hg5c_cam";
    private const string SharedSettingsPath = @"Software\hg5c_cam\Shared";
    private const string SharedHistoryPath = @"Software\hg5c_cam\Shared\UrlHistory";
    private const string SlotLockName = @"Global\hg5c_cam_slot_lock";
    private const string PasswordEncryptionGuidValueName = "PasswordEncryptionGuidHexValue";
    private const string EncryptedPasswordPrefix = "{{{";
    private const string EncryptedPasswordSuffix = "}}}";
    private static readonly byte[] PasswordIv = [0x2A, 0x4C, 0x07, 0xD1, 0x6E, 0x83, 0x19, 0xF0, 0xA5, 0x34, 0xBC, 0x5D, 0xE8, 0x11, 0x92, 0x6F];

    private enum PasswordReadMode
    {
        Runtime,
        Export
    }

    public sealed class PasswordMaintenanceResult
    {
        public bool HasUndecodablePasswords { get; init; }
        public bool ConvertedPlainTextPasswords { get; init; }
    }

    public string LoadLanguage()
    {
        using var key = Registry.CurrentUser.CreateSubKey(SharedSettingsPath);
        var language = key?.GetValue("Language") as string;
        if (string.IsNullOrWhiteSpace(language))
        {
            return LocalizationService.English;
        }

        return LocalizationService.NormalizeLanguage(language);
    }

    public void SaveLanguage(string language)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SharedSettingsPath);
        key?.SetValue("Language", LocalizationService.NormalizeLanguage(language), RegistryValueKind.String);
    }

    public bool LoadTopmostWindow()
    {
        return LoadGlobalSettings().TopmostMainWindow;
    }

    public void SaveTopmostWindow(bool isTopmost)
    {
        var settings = LoadGlobalSettings();
        settings.TopmostMainWindow = isTopmost;
        SaveGlobalSettings(settings);
    }

    public GlobalSettings LoadGlobalSettings()
    {
        var settings = new GlobalSettings();
        using var key = Registry.CurrentUser.CreateSubKey(SharedSettingsPath);
        if (key is null)
        {
            return settings;
        }

        settings.EnableSound = ToInt(key.GetValue("GlobalSoundEnabled"), settings.EnableSound ? 1 : 0) == 1;
        settings.ForceSoftwareDecoding = ToInt(key.GetValue("ForceSoftwareDecoding"), settings.ForceSoftwareDecoding ? 1 : 0) == 1;
        settings.AudioOutputDeviceName = (key.GetValue("AudioOutputDeviceName") as string) ?? settings.AudioOutputDeviceName;
        settings.SoundLevel = Math.Clamp(ToInt(key.GetValue("GlobalSoundLevel"), settings.SoundLevel), 0, 100);
        settings.SplitPlaybackCameraCount = Math.Clamp(ToInt(key.GetValue("SplitPlaybackCameraCount"), settings.SplitPlaybackCameraCount), 1, MaxInstanceSlots);
        settings.AlwaysMaximizedPlayback = ToInt(key.GetValue("AlwaysMaximizedPlayback"), settings.AlwaysMaximizedPlayback ? 1 : 0) == 1;
        settings.TopmostMainWindow = ToInt(key.GetValue("TopMost"), settings.TopmostMainWindow ? 1 : 0) == 1;
        settings.UseSecondStream = ToInt(key.GetValue("useSecondStream"), settings.UseSecondStream) == 1 ? 1 : 0;
        settings.LastUsedCameraSlot = Math.Clamp(ToInt(key.GetValue("LastUsedCameraSlot"), settings.LastUsedCameraSlot), 1, MaxInstanceSlots);
        return settings;
    }

    public void SaveGlobalSettings(GlobalSettings settings)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SharedSettingsPath);
        if (key is null)
        {
            return;
        }

        key.SetValue("GlobalSoundEnabled", settings.EnableSound ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("ForceSoftwareDecoding", settings.ForceSoftwareDecoding ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AudioOutputDeviceName", settings.AudioOutputDeviceName ?? string.Empty, RegistryValueKind.String);
        key.SetValue("GlobalSoundLevel", Math.Clamp(settings.SoundLevel, 0, 100), RegistryValueKind.DWord);
        key.SetValue("SplitPlaybackCameraCount", Math.Clamp(settings.SplitPlaybackCameraCount, 1, MaxInstanceSlots), RegistryValueKind.DWord);
        key.SetValue("AlwaysMaximizedPlayback", settings.AlwaysMaximizedPlayback ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("TopMost", settings.TopmostMainWindow ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("useSecondStream", settings.UseSecondStream == 1 ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("LastUsedCameraSlot", Math.Clamp(settings.LastUsedCameraSlot, 1, MaxInstanceSlots), RegistryValueKind.DWord);
    }

    public int LoadLastUsedCameraSlot()
    {
        return LoadGlobalSettings().LastUsedCameraSlot;
    }

    public void SaveLastUsedCameraSlot(int slot)
    {
        var settings = LoadGlobalSettings();
        settings.LastUsedCameraSlot = Math.Clamp(slot, 1, MaxInstanceSlots);
        SaveGlobalSettings(settings);
    }

    public int AcquireInstanceSlot()
    {
        using var mutex = new Mutex(false, SlotLockName);
        mutex.WaitOne();
        try
        {
            for (var i = 1; i <= MaxInstanceSlots; i++)
            {
                using var slotKey = Registry.CurrentUser.CreateSubKey($@"{RootPath}\Instance_{i}");
                if (slotKey is null) continue;
                var pidValue = slotKey.GetValue("ProcessId") as int?;
                if (pidValue is null || !IsProcessAlive(pidValue.Value))
                {
                    slotKey.SetValue("ProcessId", Environment.ProcessId, RegistryValueKind.DWord);
                    return i;
                }
            }
            throw new InvalidOperationException(LocalizationService.TranslateCurrent("ExceptionNoFreeInstanceSlot"));
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public int AcquireSpecificInstanceSlot(int slotNumber)
    {
        if (slotNumber <= 0 || slotNumber > MaxInstanceSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slotNumber), string.Format(CultureInfo.CurrentCulture, LocalizationService.TranslateCurrent("ExceptionInstanceSlotRange"), MaxInstanceSlots));
        }

        using var mutex = new Mutex(false, SlotLockName);
        mutex.WaitOne();
        try
        {
            using var slotKey = Registry.CurrentUser.CreateSubKey($@"{RootPath}\Instance_{slotNumber}");
            if (slotKey is null)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, LocalizationService.TranslateCurrent("ExceptionRegistryKeyCreateFailed"), slotNumber));
            }

            slotKey.SetValue("ProcessId", Environment.ProcessId, RegistryValueKind.DWord);
            return slotNumber;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public void ReleaseInstanceSlot(int slot)
    {
        using var slotKey = Registry.CurrentUser.CreateSubKey($@"{RootPath}\Instance_{slot}");
        slotKey?.DeleteValue("ProcessId", false);
    }

    public AppSettings LoadSettings(int slot)
    {
        var streamNumber = GetStreamNumberFromGlobalSettings(LoadGlobalSettings());
        return LoadSettings(slot, streamNumber);
    }

    public AppSettings LoadSettings(int slot, int streamNumber)
    {
        return LoadSettings(slot, streamNumber, PasswordReadMode.Runtime);
    }

    private AppSettings LoadSettings(int slot, int streamNumber, PasswordReadMode passwordReadMode)
    {
        streamNumber = NormalizeStreamNumber(streamNumber);
        var settings = new AppSettings();
        using var key = OpenSettingsKey(slot, streamNumber, writable: false);
        if (key is null) return settings;
        settings.CameraName = (key.GetValue("CameraName") as string) ?? settings.CameraName;
        settings.Url = (key.GetValue("Url") as string) ?? settings.Url;
        settings.Username = (key.GetValue("Username") as string) ?? settings.Username;
        var storedPassword = (key.GetValue("Password") as string) ?? settings.Password;
        settings.Password = passwordReadMode == PasswordReadMode.Export
            ? ConvertPasswordForExport(storedPassword)
            : DecodePasswordForRuntime(storedPassword);
        settings.UseOnvif = ToInt(key.GetValue("UseOnvif"), 1) == 1;
        settings.OnvifDeviceServiceUrl = (key.GetValue("OnvifDeviceServiceUrl") as string) ?? settings.OnvifDeviceServiceUrl;
        settings.OnvifProfileToken = (key.GetValue("OnvifProfileToken") as string) ?? settings.OnvifProfileToken;
        settings.AutoResolveRtspFromOnvif = ToInt(key.GetValue("AutoResolveRtspFromOnvif"), 1) == 1;
        settings.ReconnectDelaySec = ToInt(key.GetValue("ReconnectDelay"), 3);
        settings.ConnectionRetries = ToInt(key.GetValue("ConnectionRetries"), 25);
        settings.NetworkTimeoutSec = ToInt(key.GetValue("NetworkTimeoutSec"), 5);
        settings.MaxFps = ToInt(key.GetValue("MaxFps"), 0);
        settings.ShowFpsOverlay = ToInt(key.GetValue("ShowFpsOverlay"), 0) == 1;
        settings.FpsOverlayPosition = (key.GetValue("FpsOverlayPosition") as string) ?? AppSettings.FpsOverlayPositionBottomLeft;
        settings.SoundEnabled = ToInt(key.GetValue("SoundEnabled"), 0) == 1;
        settings.SoundLevel = Math.Clamp(ToInt(key.GetValue("SoundLevel"), settings.SoundLevel), 0, 100);
        settings.AspectRatioMode = (key.GetValue("AspectRatioMode") as string) ?? AppSettings.AutoAspectRatio;
        settings.OnvifXSize = key.GetValue("OnvifXSize") is int onvifXSize && onvifXSize > 0 ? onvifXSize : null;
        settings.OnvifYSize = key.GetValue("OnvifYSize") is int onvifYSize && onvifYSize > 0 ? onvifYSize : null;
        settings.WindowLeft = key.GetValue("WindowLeft") is int wl ? wl : null;
        settings.WindowTop = key.GetValue("WindowTop") is int wt ? wt : null;
        settings.WindowWidth = key.GetValue("WindowWidth") is int ww ? ww : null;
        settings.WindowHeight = key.GetValue("WindowHeight") is int wh ? wh : null;
        settings.WindowMaximized = ToInt(key.GetValue("WindowMaximized"), 0) == 1;
        return settings;
    }

    public void SaveSettings(int slot, AppSettings settings)
    {
        var streamNumber = GetStreamNumberFromGlobalSettings(LoadGlobalSettings());
        SaveSettings(slot, settings, streamNumber);
    }

    public void SaveSettings(int slot, AppSettings settings, int streamNumber)
    {
        streamNumber = NormalizeStreamNumber(streamNumber);
        using var key = OpenSettingsKey(slot, streamNumber, writable: true);
        if (key is null) return;
        key.SetValue("CameraName", settings.CameraName ?? string.Empty, RegistryValueKind.String);
        key.SetValue("Url", settings.Url ?? string.Empty, RegistryValueKind.String);
        key.SetValue("Username", settings.Username ?? string.Empty, RegistryValueKind.String);
        key.SetValue("Password", EncryptPasswordForStorage(settings.Password), RegistryValueKind.String);
        key.SetValue("UseOnvif", settings.UseOnvif ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("OnvifDeviceServiceUrl", settings.OnvifDeviceServiceUrl ?? string.Empty, RegistryValueKind.String);
        key.SetValue("OnvifProfileToken", settings.OnvifProfileToken ?? string.Empty, RegistryValueKind.String);
        key.SetValue("AutoResolveRtspFromOnvif", settings.AutoResolveRtspFromOnvif ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("ReconnectDelay", settings.ReconnectDelaySec, RegistryValueKind.DWord);
        key.SetValue("ConnectionRetries", settings.ConnectionRetries, RegistryValueKind.DWord);
        key.SetValue("NetworkTimeoutSec", settings.NetworkTimeoutSec, RegistryValueKind.DWord);
        key.SetValue("MaxFps", settings.MaxFps, RegistryValueKind.DWord);
        key.SetValue("ShowFpsOverlay", settings.ShowFpsOverlay ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("FpsOverlayPosition", settings.FpsOverlayPosition ?? AppSettings.FpsOverlayPositionBottomLeft, RegistryValueKind.String);
        key.DeleteValue("TopMost", false);
        key.SetValue("SoundEnabled", settings.SoundEnabled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("SoundLevel", Math.Clamp(settings.SoundLevel, 0, 100), RegistryValueKind.DWord);
        key.SetValue("AspectRatioMode", settings.AspectRatioMode ?? AppSettings.AutoAspectRatio, RegistryValueKind.String);
        if (settings.OnvifXSize.HasValue && settings.OnvifXSize.Value > 0)
        {
            key.SetValue("OnvifXSize", settings.OnvifXSize.Value, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue("OnvifXSize", false);
        }

        if (settings.OnvifYSize.HasValue && settings.OnvifYSize.Value > 0)
        {
            key.SetValue("OnvifYSize", settings.OnvifYSize.Value, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue("OnvifYSize", false);
        }
        if (settings.WindowLeft.HasValue) key.SetValue("WindowLeft", settings.WindowLeft.Value, RegistryValueKind.DWord);
        if (settings.WindowTop.HasValue) key.SetValue("WindowTop", settings.WindowTop.Value, RegistryValueKind.DWord);
        if (settings.WindowWidth.HasValue) key.SetValue("WindowWidth", settings.WindowWidth.Value, RegistryValueKind.DWord);
        if (settings.WindowHeight.HasValue) key.SetValue("WindowHeight", settings.WindowHeight.Value, RegistryValueKind.DWord);
        key.SetValue("WindowMaximized", settings.WindowMaximized ? 1 : 0, RegistryValueKind.DWord);
    }

    public List<string> LoadUrlHistory()
    {
        var result = new List<string>();
        using var key = Registry.CurrentUser.CreateSubKey(SharedHistoryPath);
        if (key is null) return result;
        for (var i = 0; i < 10; i++)
        {
            var value = key.GetValue(i.ToString()) as string;
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
        }
        return result;
    }

    public void SaveUrlHistory(List<string> urls)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SharedHistoryPath);
        if (key is null) return;
        for (var i = 0; i < 10; i++) key.DeleteValue(i.ToString(), false);
        for (var i = 0; i < Math.Min(10, urls.Count); i++)
        {
            key.SetValue(i.ToString(), urls[i], RegistryValueKind.String);
        }
    }

    public void AddUrlToHistory(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var history = LoadUrlHistory();
        history.RemoveAll(x => string.Equals(x, url, StringComparison.OrdinalIgnoreCase));
        history.Insert(0, url.Trim());
        if (history.Count > 10) history = history.Take(10).ToList();
        SaveUrlHistory(history);
    }

    public Dictionary<int, Dictionary<int, AppSettings>> LoadAllCameraSettings(bool forExport = false)
    {
        var result = new Dictionary<int, Dictionary<int, AppSettings>>();
        var passwordReadMode = forExport ? PasswordReadMode.Export : PasswordReadMode.Runtime;
        using var rootKey = Registry.CurrentUser.OpenSubKey(RootPath);
        if (rootKey is null)
        {
            return result;
        }

        foreach (var subKeyName in rootKey.GetSubKeyNames())
        {
            if (!TryParseInstanceSlot(subKeyName, out var slot))
            {
                continue;
            }

            using var slotKey = rootKey.OpenSubKey(subKeyName);
            if (slotKey is null)
            {
                continue;
            }

            var streamSettings = new Dictionary<int, AppSettings>();
            foreach (var streamSubKeyName in slotKey.GetSubKeyNames())
            {
                if (!TryParseStreamNumber(streamSubKeyName, out var streamNumber))
                {
                    continue;
                }

                using var streamKey = slotKey.OpenSubKey(streamSubKeyName);
                if (streamKey is null)
                {
                    continue;
                }

                if (!HasUserSettings(streamKey.GetValueNames()))
                {
                    continue;
                }

                streamSettings[streamNumber] = LoadSettings(slot, streamNumber, passwordReadMode);
            }

            if (streamSettings.Count == 0 && HasUserSettings(slotKey.GetValueNames()))
            {
                streamSettings[PrimaryStreamNumber] = LoadSettings(slot, PrimaryStreamNumber, passwordReadMode);
            }

            if (streamSettings.Count > 0)
            {
                result[slot] = streamSettings;
            }
        }

        return result;
    }

    public void ResetAllCameraSettings()
    {
        using var rootKey = Registry.CurrentUser.OpenSubKey(RootPath, writable: true);
        if (rootKey is null)
        {
            return;
        }

        foreach (var subKeyName in rootKey.GetSubKeyNames())
        {
            if (!TryParseInstanceSlot(subKeyName, out _))
            {
                continue;
            }

            rootKey.DeleteSubKeyTree(subKeyName, false);
        }
    }

    public void SaveAllCameraSettings(IReadOnlyDictionary<int, IReadOnlyDictionary<int, AppSettings>> settingsBySlot)
    {
        foreach (var slotPair in settingsBySlot)
        {
            if (slotPair.Key <= 0)
            {
                continue;
            }

            foreach (var streamPair in slotPair.Value)
            {
                if (streamPair.Key <= 0)
                {
                    continue;
                }

                SaveSettings(slotPair.Key, streamPair.Value, streamPair.Key);
            }
        }
    }

    public PasswordMaintenanceResult EnsurePasswordEncryptionState()
    {
        var convertedPlainTextPasswords = false;
        var hasUndecodablePasswords = false;
        _ = GetOrCreatePasswordKey();

        using var rootKey = Registry.CurrentUser.OpenSubKey(RootPath, writable: true);
        if (rootKey is null)
        {
            return new PasswordMaintenanceResult
            {
                ConvertedPlainTextPasswords = false,
                HasUndecodablePasswords = false
            };
        }

        foreach (var subKeyName in rootKey.GetSubKeyNames())
        {
            if (!TryParseInstanceSlot(subKeyName, out _))
            {
                continue;
            }

            using var slotKey = rootKey.OpenSubKey(subKeyName, writable: true);
            if (slotKey is null)
            {
                continue;
            }

            var streamSubKeys = slotKey.GetSubKeyNames().Where(name => TryParseStreamNumber(name, out _)).ToList();
            if (streamSubKeys.Count == 0)
            {
                ProcessPasswordValue(slotKey, ref convertedPlainTextPasswords, ref hasUndecodablePasswords);
                continue;
            }

            foreach (var streamSubKeyName in streamSubKeys)
            {
                using var streamKey = slotKey.OpenSubKey(streamSubKeyName, writable: true);
                if (streamKey is null)
                {
                    continue;
                }

                ProcessPasswordValue(streamKey, ref convertedPlainTextPasswords, ref hasUndecodablePasswords);
            }
        }

        return new PasswordMaintenanceResult
        {
            ConvertedPlainTextPasswords = convertedPlainTextPasswords,
            HasUndecodablePasswords = hasUndecodablePasswords
        };
    }

    public static bool ContainsForbiddenPasswordBraceSequence(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        return password.Contains("{{", StringComparison.Ordinal) || password.Contains("}}", StringComparison.Ordinal);
    }

    public string NormalizeImportedPassword(string? importedPassword, out bool hasError)
    {
        hasError = false;
        var value = importedPassword ?? string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(PasswordNoKeyMarker, StringComparison.Ordinal))
        {
            hasError = true;
            return string.Empty;
        }

        if (!IsEncryptedPassword(value))
        {
            return value;
        }

        if (TryDecryptWrappedPassword(value, out var plainText))
        {
            return plainText;
        }

        hasError = true;
        return string.Empty;
    }

    public static int GetStreamNumberFromGlobalSettings(GlobalSettings settings)
    {
        return settings.UseSecondStream == 1 ? SecondaryStreamNumber : PrimaryStreamNumber;
    }

    public static int NormalizeStreamNumber(int streamNumber)
    {
        return streamNumber == SecondaryStreamNumber ? SecondaryStreamNumber : PrimaryStreamNumber;
    }

    private static bool HasUserSettings(IEnumerable<string> valueNames)
    {
        return valueNames.Any(name => !string.Equals(name, "ProcessId", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEncryptedPassword(string password)
    {
        return password.Length >= EncryptedPasswordPrefix.Length + EncryptedPasswordSuffix.Length &&
               password.StartsWith(EncryptedPasswordPrefix, StringComparison.Ordinal) &&
               password.EndsWith(EncryptedPasswordSuffix, StringComparison.Ordinal);
    }

    private static string WrapEncryptedPassword(string encryptedValue)
    {
        return $"{EncryptedPasswordPrefix}{encryptedValue}{EncryptedPasswordSuffix}";
    }

    private static string UnwrapEncryptedPassword(string encryptedValue)
    {
        return encryptedValue[EncryptedPasswordPrefix.Length..^EncryptedPasswordSuffix.Length];
    }

    private string ConvertPasswordForExport(string storedPassword)
    {
        if (string.IsNullOrEmpty(storedPassword))
        {
            return string.Empty;
        }

        if (storedPassword.Contains(PasswordNoKeyMarker, StringComparison.Ordinal))
        {
            return PasswordNoKeyMarker;
        }

        if (!IsEncryptedPassword(storedPassword))
        {
            return storedPassword;
        }

        return TryDecryptWrappedPassword(storedPassword, out var plainText)
            ? plainText
            : PasswordNoKeyMarker;
    }

    private string DecodePasswordForRuntime(string storedPassword)
    {
        if (string.IsNullOrEmpty(storedPassword) || storedPassword.Contains(PasswordNoKeyMarker, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (!IsEncryptedPassword(storedPassword))
        {
            return storedPassword;
        }

        return TryDecryptWrappedPassword(storedPassword, out var plainText)
            ? plainText
            : string.Empty;
    }

    private string EncryptPasswordForStorage(string? plainPassword)
    {
        var value = plainPassword ?? string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = NormalizeImportedPassword(value, out _);
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        var keyBytes = GetOrCreatePasswordKey();
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.KeySize = 128;
        aes.Key = keyBytes;
        aes.IV = PasswordIv;
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(normalized);
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        return WrapEncryptedPassword(Convert.ToBase64String(encryptedBytes));
    }

    private bool TryDecryptWrappedPassword(string wrappedPassword, out string plainText)
    {
        plainText = string.Empty;
        if (!IsEncryptedPassword(wrappedPassword) || !TryGetPasswordKey(out var keyBytes))
        {
            return false;
        }

        var base64 = UnwrapEncryptedPassword(wrappedPassword);
        byte[] encryptedBytes;
        try
        {
            encryptedBytes = Convert.FromBase64String(base64);
        }
        catch
        {
            return false;
        }

        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.KeySize = 128;
            aes.Key = keyBytes;
            aes.IV = PasswordIv;
            using var decryptor = aes.CreateDecryptor();
            var plainBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
            plainText = Encoding.UTF8.GetString(plainBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ProcessPasswordValue(RegistryKey key, ref bool convertedPlainTextPasswords, ref bool hasUndecodablePasswords)
    {
        var password = key.GetValue("Password") as string;
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        if (password.Contains(PasswordNoKeyMarker, StringComparison.Ordinal))
        {
            hasUndecodablePasswords = true;
            return;
        }

        if (!IsEncryptedPassword(password))
        {
            key.SetValue("Password", EncryptPasswordForStorage(password), RegistryValueKind.String);
            convertedPlainTextPasswords = true;
            return;
        }

        if (!TryDecryptWrappedPassword(password, out _))
        {
            hasUndecodablePasswords = true;
        }
    }

    private byte[] GetOrCreatePasswordKey()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RootPath);
        if (key is null)
        {
            return Guid.NewGuid().ToByteArray();
        }

        if (TryGetPasswordKey(key, out var existingKey))
        {
            return existingKey;
        }

        var generated = Guid.NewGuid().ToByteArray();
        key.SetValue(PasswordEncryptionGuidValueName, Convert.ToHexString(generated), RegistryValueKind.String);
        return generated;
    }

    private bool TryGetPasswordKey(out byte[] keyBytes)
    {
        keyBytes = [];
        using var key = Registry.CurrentUser.OpenSubKey(RootPath, writable: false);
        if (key is null)
        {
            return false;
        }

        return TryGetPasswordKey(key, out keyBytes);
    }

    private static bool TryGetPasswordKey(RegistryKey key, out byte[] keyBytes)
    {
        keyBytes = [];
        var keyHex = key.GetValue(PasswordEncryptionGuidValueName) as string;
        if (string.IsNullOrWhiteSpace(keyHex))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromHexString(keyHex.Trim());
            if (bytes.Length != 16)
            {
                return false;
            }

            keyBytes = bytes;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseStreamNumber(string subKeyName, out int streamNumber)
    {
        streamNumber = 0;
        if (!subKeyName.StartsWith("Stream_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(subKeyName["Stream_".Length..], out streamNumber) && streamNumber > 0;
    }

    private static string GetStreamKeyPath(int slot, int streamNumber)
    {
        return $@"{RootPath}\Instance_{slot}\Stream_{streamNumber}";
    }

    private static RegistryKey? OpenSettingsKey(int slot, int streamNumber, bool writable)
    {
        streamNumber = NormalizeStreamNumber(streamNumber);
        if (writable)
        {
            return Registry.CurrentUser.CreateSubKey(GetStreamKeyPath(slot, streamNumber));
        }

        var streamPath = GetStreamKeyPath(slot, streamNumber);
        var streamKey = Registry.CurrentUser.OpenSubKey(streamPath, writable: false);
        if (streamKey is not null)
        {
            return streamKey;
        }

        if (streamNumber != PrimaryStreamNumber)
        {
            return null;
        }

        return Registry.CurrentUser.OpenSubKey($@"{RootPath}\Instance_{slot}", writable: false);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            _ = Process.GetProcessById(processId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ToInt(object? value, int defaultValue)
    {
        return value is int intValue ? intValue : defaultValue;
    }

    private static bool TryParseInstanceSlot(string subKeyName, out int slot)
    {
        slot = 0;
        if (!subKeyName.StartsWith("Instance_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(subKeyName["Instance_".Length..], out slot) && slot > 0;
    }
}
