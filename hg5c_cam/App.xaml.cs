using FFmpeg.AutoGen;
using hg5c_cam.Dialogs;
using hg5c_cam.Models;
using hg5c_cam.Services;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using System.Linq;

namespace hg5c_cam;

public partial class App : Application
{
    private const int SwRestore = 9;
    private const int SwShow = 5;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNoTopmost = new(-2);

    private readonly RegistryService _registryService = new();
    private int _slot;
    private System.Threading.Mutex? _instanceMutex;
    private System.Threading.Mutex? _sharedInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (!Directory.Exists(ffmpegPath))
        {
            var legacyPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg_bins");
            ffmpegPath = Directory.Exists(legacyPath) ? legacyPath : AppContext.BaseDirectory;
        }

        ffmpeg.RootPath = ffmpegPath;
        base.OnStartup(e);

        var startupArguments = ParseStartupArguments(e.Args);
        var requestedInstanceNumber = startupArguments.InstanceNumber;
        var urls = startupArguments.Urls;
        this._sharedInstanceMutex = OpenSharedInstanceMutex(out var isFirstInstance);

        if (startupArguments.HasExplicitLanguageOverride && !string.IsNullOrWhiteSpace(startupArguments.Language))
        {
            this._registryService.SaveLanguage(startupArguments.Language);
        }

        if (urls.Count > 1)
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                foreach (var extraUrl in urls.Skip(1))
                {
                    Process.Start(new ProcessStartInfo(exePath, $"\"{extraUrl}\"") { UseShellExecute = false });
                }
            }
        }

        var language = startupArguments.Language ?? this._registryService.LoadLanguage();

        if (startupArguments.ClearSettings)
        {
            this._registryService.ResetAllCameraSettings();
        }

        if (startupArguments.ImportSettingsPath is not null)
        {
            var importPath = startupArguments.ImportSettingsPath;
            if (File.Exists(importPath))
            {
                try
                {
                    hg5c_cam.MainWindow.ImportCameraSettingsFromFile(this._registryService, importPath, clearExisting: false, language);
                }
                catch (Exception ex)
                {
                    ShowAppCenteredMessageBox(
                        language,
                        ex.Message,
                        "hg5c_cam",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            else
            {
                ShowAppCenteredMessageBox(
                    language,
                    $"Settings import file was not found: {importPath}",
                    "hg5c_cam",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        var passwordMaintenance = this._registryService.EnsurePasswordEncryptionState();
        if (passwordMaintenance.HasUndecodablePasswords)
        {
            ShowAppCenteredMessageBox(
                language,
                hg5c_cam.MainWindow.BuildPasswordReentryMessage(language),
                "hg5c_cam",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        if (requestedInstanceNumber.HasValue)
        {
            if (!TryAcquireInstanceMutex(requestedInstanceNumber.Value, out this._instanceMutex))
            {
                ShowAppCenteredMessageBox(
                    language,
                    string.Format(LocalizationService.Translate(language, "InstanceAlreadyRunning"), requestedInstanceNumber.Value),
                    "hg5c_cam",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            this._slot = this._registryService.AcquireSpecificInstanceSlot(requestedInstanceNumber.Value);
            this._registryService.SaveLastUsedCameraSlot(this._slot);
        }
        else
        {
            var assignedSlot = 0;
            if (isFirstInstance)
            {
                var preferredSlot = this._registryService.LoadLastUsedCameraSlot();
                if (TryAcquireInstanceMutex(preferredSlot, out var preferredMutex))
                {
                    this._instanceMutex = preferredMutex;
                    assignedSlot = preferredSlot;
                }
            }

            if (assignedSlot == 0)
            {
                for (var candidate = 1; candidate <= RegistryService.MaxInstanceSlots; candidate++)
                {
                    if (!TryAcquireInstanceMutex(candidate, out var candidateMutex))
                    {
                        continue;
                    }

                    this._instanceMutex = candidateMutex;
                    assignedSlot = candidate;
                    break;
                }
            }

            if (assignedSlot == 0)
            {
                ShowAppCenteredMessageBox(
                    language,
                    $"No free camera profile slot is available (1..{RegistryService.MaxInstanceSlots}).",
                    "hg5c_cam",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            this._slot = this._registryService.AcquireSpecificInstanceSlot(assignedSlot);
            this._registryService.SaveLastUsedCameraSlot(this._slot);
        }

        var settings = this._registryService.LoadSettings(this._slot);
        if (urls.Count > 0)
        {
            ApplyUrlArguments(settings, urls);
            this._registryService.SaveSettings(this._slot, settings);
        }
        var mainWindow = new MainWindow(this._slot, this._registryService, settings, language);
        MainWindow = mainWindow;
        mainWindow.Show();

        ForceWindowToForeground(mainWindow);

        mainWindow.Dispatcher.BeginInvoke(() =>
        {
            ForceWindowToForeground(mainWindow);
        }, DispatcherPriority.ApplicationIdle);

        _ = mainWindow.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(150);
            ForceWindowToForeground(mainWindow);
        }, DispatcherPriority.Background);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (this._slot > 0)
        {
            this._registryService.ReleaseInstanceSlot(this._slot);
        }

        if (this._instanceMutex is not null)
        {
            this._instanceMutex.ReleaseMutex();
            this._instanceMutex.Dispose();
        }

        this._sharedInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static bool TryAcquireInstanceMutex(int instanceNumber, out System.Threading.Mutex? mutex)
    {
        mutex = new System.Threading.Mutex(true, $"Global\\hg5c_cam_instance_{instanceNumber}", out var createdNew);
        if (createdNew)
        {
            return true;
        }

        mutex.Dispose();
        mutex = null;
        return false;
    }

    private static MessageBoxResult ShowAppCenteredMessageBox(
        string language,
        string message,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        var owner = Current?.MainWindow ?? Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsVisible);
        return AppMessageDialog.Show(owner, message, caption, button, icon, language);
    }

    private static System.Threading.Mutex OpenSharedInstanceMutex(out bool isFirstInstance)
    {
        return new System.Threading.Mutex(false, "Global\\hg5c_cam_instance_any", out isFirstInstance);
    }

    private static StartupArguments ParseStartupArguments(string[] args)
    {
        const string clearSettingsOption = "--clearsettings";
        const string importSettingsOption = "--importsettings";
        const string defaultSettingsFileName = "hg5c_cam_settings.cnf";

        var positionalArgs = new List<string>();
        var clearSettings = false;
        string? importSettingsPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, clearSettingsOption, StringComparison.OrdinalIgnoreCase))
            {
                clearSettings = true;
                continue;
            }

            if (string.Equals(arg, importSettingsOption, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    var next = args[i + 1];
                    if (!string.Equals(next, clearSettingsOption, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(next, importSettingsOption, StringComparison.OrdinalIgnoreCase))
                    {
                        importSettingsPath = next;
                        i++;
                        continue;
                    }
                }

                importSettingsPath = Path.Combine(AppContext.BaseDirectory, defaultSettingsFileName);
                continue;
            }

            if (!arg.StartsWith('-') && !arg.StartsWith('/'))
            {
                positionalArgs.Add(arg);
            }
        }

        var consumed = 0;
        int? instanceNumber = null;
        string? language = null;
        var hasExplicitLanguageOverride = false;

        if (positionalArgs.Count > 0 && int.TryParse(positionalArgs[0], out var number) && number > 0)
        {
            instanceNumber = number;
            consumed = 1;

            if (positionalArgs.Count > 1 && IsLanguageParameter(positionalArgs[1]))
            {
                language = LocalizationService.NormalizeLanguage(positionalArgs[1]);
                hasExplicitLanguageOverride = true;
                consumed = 2;
            }
        }

        var urls = positionalArgs.Skip(consumed).Where(a => !IsNumeric(a)).ToList();
        return new StartupArguments(instanceNumber, language, hasExplicitLanguageOverride, urls, clearSettings, importSettingsPath);
    }

    private static bool IsNumeric(string value)
    {
        return int.TryParse(value, out _);
    }

    private static bool IsLanguageParameter(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is LocalizationService.English or LocalizationService.Hungarian;
    }

    private sealed record StartupArguments(
        int? InstanceNumber,
        string? Language,
        bool HasExplicitLanguageOverride,
        List<string> Urls,
        bool ClearSettings,
        string? ImportSettingsPath);

    private static void ForceWindowToForeground(Window window)
    {
        var wasMinimized = window.WindowState == WindowState.Minimized;
        if (wasMinimized)
        {
            window.WindowState = WindowState.Normal;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        ShowWindow(hwnd, wasMinimized ? SwRestore : SwShow);
        if (window.Topmost)
        {
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        }
        else
        {
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        }
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        window.Activate();
        Keyboard.Focus(window);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private void ApplyUrlArguments(AppSettings settings, IReadOnlyList<string> urls)
    {
        foreach (var url in urls)
        {
            this._registryService.AddUrlToHistory(url);
        }

        var first = urls[0];
        settings.Url = first;
        if (!Uri.TryCreate(first, UriKind.Absolute, out var uri)) return;
        if (!string.IsNullOrWhiteSpace(uri.UserInfo) && uri.UserInfo.Contains(':', StringComparison.Ordinal))
        {
            var parts = uri.UserInfo.Split(':', 2);
            settings.Username = Uri.UnescapeDataString(parts[0]);
            settings.Password = Uri.UnescapeDataString(parts[1]);
        }
    }
}
