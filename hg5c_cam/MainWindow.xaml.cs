using hg5c_cam.Dialogs;
using hg5c_cam.Models;
using hg5c_cam.Services;
using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace hg5c_cam;

public partial class MainWindow : Window
{
    private const int CameraMenuMaxProfiles = 8;
    private const double DesktopUsageLimit = 1.0;
    private const double FpsOverlayBackgroundOpacity = 0.47;
    private const double PanelsOverlayBackgroundOpacity = 0.60;
    private const double PresetsOverlayMaxVideoFraction = 0.8;
    private const int NavigationRepeatIntervalMs = 80;
    private const int NavigationDirectionSettleDelayMs = 70;
    private const double DirectionChangeBoostPanTilt = 1.8;
    private const double DirectionChangeBoostZoom = 1.6;
    private const double FixedNavigationStepSize = 0.001;
    private const double FixedNavigationStepSizeFast = FixedNavigationStepSize * 50;
    private const double FixedZoomStepSize = 0.01;
    private const double FixedZoomStepSizeFast = FixedZoomStepSize * 10;
    private const double AdaptiveWideScale = 1.0;
    private const double AdaptiveTeleScale = 0.35;
    private const double AdaptiveFastMultiplierPanTilt = 12.0;
    private const double AdaptiveFastMultiplierZoom = 8.0;
    private const double DisabledControlOpacity = 0.45;
    private const int PresetsDisplayLimit = 32;
    private const string SettingsExportFileName = "hg5c_cam_settings.cnf";
    private const int WmSizing = 0x0214;
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;

    private readonly RegistryService _registryService;
    private PlayerService _playerService;
    private readonly Dictionary<int, PlayerService> _backgroundPlayers = [];
    private readonly Dictionary<int, Action<PlayerState>> _backgroundPlayerStateHandlers = [];
    private readonly Dictionary<int, FpsCounterService> _fpsCounters = [];
    private readonly OnvifService _onvifService;
    private readonly Dictionary<int, CameraViewport> _viewports = [];
    private readonly int _ownedSlot;
    private int _slot;
    private AppSettings _settings;
    private GlobalSettings _globalSettings;
    private readonly DispatcherTimer _windowSaveDebounce;
    private readonly DispatcherTimer _navigationVolumeSaveDebounce;
    private WindowState _previousState;
    private WindowStyle _previousStyle;
    private bool _isFullscreen;
    private double _targetAspectRatio;
    private HwndSource? _hwndSource;
    private readonly Dictionary<int, List<OnvifPreset>> _presetCache = [];
    private readonly HashSet<int> _presetLoadsInProgress = [];
    private readonly Dictionary<int, int> _presetLoadVersions = [];
    private readonly Dictionary<int, StreamRuntimeCapabilities> _runtimeCapabilityCache = [];
    private readonly HashSet<int> _runtimeCapabilityLoadsInProgress = [];
    private readonly Dictionary<int, int> _runtimeCapabilityLoadVersions = [];
    private readonly Dictionary<int, Action<bool>> _backgroundPlayerAudioHandlers = [];
    private Action<bool>? _primaryPlayerAudioHandler;
    private CancellationTokenSource? _navigationMoveCancellationTokenSource;
    private bool _isVideoHostHovered;
    private int _hoveredSlot;
    private int _presetsSlot;
    private int _loadedPresetsSlot;
    private bool _isPresetsLoading;
    private string _language;
    private bool _isAspectRatioApplyQueued;
    private int _isApplyingPlayerStateUi;
    private int _isUpdatingPresetsOverlayVisibility;
    private int _isOverlayPlacementRefreshQueued;
    private int _isApplyingOverlayPlacement;
    private int _isGotoPresetInProgress;
    private double _navigationStepSize = FixedNavigationStepSize;
    private double _zoomStepSize = FixedZoomStepSize;
    private bool _resumePlaybackAfterPowerResume;
    private bool _isShuttingDown;
    private bool _isUpdatingNavigationVolumeSlider;
    private int _navigationVolumeTargetSlot;
    private int _navigationVolumeTargetStreamNumber = RegistryService.PrimaryStreamNumber;

    private enum PresetOverlayState
    {
        Hidden,
        NavigationOnly,
        Ready
    }

    private sealed class CameraViewport
    {
        public required int Slot { get; init; }
        public required Image VideoImage { get; init; }
        public required Border FpsOverlayBorder { get; init; }
        public required TextBlock FpsOverlayText { get; init; }
        public required TextBlock FpsOverlayDetailsText { get; init; }
        public required Border SelectionBorder { get; init; }
        public required Grid StatusOverlay { get; init; }
        public required TextBlock StatusText { get; init; }
        public required AppSettings Settings { get; set; }
        public string StatusResourceKey { get; set; } = "Disconnected";
    }

    private sealed class StreamRuntimeCapabilities
    {
        public OnvifPtzCapabilities PtzCapabilities { get; set; } = new();
        public AdaptivePtzProfile AdaptivePtzProfile { get; set; } = new();
        public bool IsPtzInfoLoaded { get; set; }
        public bool? HasAudio { get; set; }
        public double? LastKnownZoomNormalized { get; set; }
        public bool MiniCalibrationCompleted { get; set; }
        public double PanTiltRuntimeGain { get; set; } = 1.0;
        public double ZoomRuntimeGain { get; set; } = 1.0;
        public int PanTiltConsecutiveFailures { get; set; }
        public int ZoomConsecutiveFailures { get; set; }
        public string LastMoveDirection { get; set; } = string.Empty;
        public DateTime LastMoveDirectionUtc { get; set; }
    }

    public MainWindow(int slot, RegistryService registryService, AppSettings settings, string language)
    {
        InitializeComponent();
        this._ownedSlot = slot;
        this._slot = slot;
        this._registryService = registryService;
        this._settings = settings;
        this._globalSettings = this._registryService.LoadGlobalSettings();
        this._language = LocalizationService.NormalizeLanguage(language);
        this._playerService = new PlayerService();
        this._onvifService = new OnvifService();
        ApplySavedWindowPlacement();
        EnsureWindowVisibleOnDesktop();
        BuildPlaybackGrid();
        UpdateSplitPlaybackToolbarSelection();
        this._playerService.Initialize(GetSelectedVideoImage());
        this._playerService.StateChanged += OnPrimaryPlayerStateChanged;
        this._playerService.StreamAspectRatioChanged += OnStreamAspectRatioChanged;
        AttachPrimaryAudioAvailabilityHandler();
        var isTopmostWindow = this._registryService.LoadTopmostWindow();
        Topmost = isTopmostWindow;
        this.TopmostMenuItem.IsChecked = isTopmostWindow;
        this.FpsOverlayMenuItem.IsChecked = this._settings.ShowFpsOverlay;
        this.StreamSoundMenuItem.IsChecked = this._settings.SoundEnabled;
        ApplyStreamQualityMenuState();
        UpdateViewToolbarSelection();
        RefreshViewportSettings();
        UpdateCameraMenuSelection();
        ApplySharedOverlayStyle();
        ApplyFpsOverlaySettings();
        UpdatePresetsOverlayBounds();
        UpdateStreamMenuState(this._playerService.State);
        this._windowSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        this._windowSaveDebounce.Tick += (_, _) => SaveWindowMetrics();
        this._navigationVolumeSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        this._navigationVolumeSaveDebounce.Tick += NavigationVolumeSaveDebounce_OnTick;
        SizeChanged += (_, _) =>
        {
            RestartWindowDebounce();
        };
        LocationChanged += (_, _) => RestartWindowDebounce();
        StateChanged += (_, _) => OnWindowStateChanged();
        this.VideoHost.SizeChanged += (_, _) =>
        {
            UpdatePlaybackGridBoundsForAspectRatio();
            UpdatePresetsOverlayBounds();
        };
        this.PresetsOverlayBorder.SizeChanged += OverlayPanel_OnSizeChanged;
        this.NavigationOverlayBorder.SizeChanged += OverlayPanel_OnSizeChanged;
        this.PresetsOverlayList.SizeChanged += OverlayPanel_OnSizeChanged;
        Loaded += (_, _) => OnLoaded();
        Closing += (_, _) => ShutdownPlayer();
        SourceInitialized += OnSourceInitialized;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        this._targetAspectRatio = Width > 0 && Height > 0 ? Width / Height : 16d / 9d;
        this.StatusOverlay.Visibility = Visibility.Collapsed;
        this.StatusOverlay.IsHitTestVisible = false;
        ApplyLocalization();
        ApplyMenuShortcutTexts();
        UpdateWindowTitle();
    }

    private int GetSplitPlaybackCameraCount()
    {
        return Math.Clamp(this._globalSettings.SplitPlaybackCameraCount, 1, RegistryService.MaxInstanceSlots);
    }

    private void UpdateSplitPlaybackToolbarSelection()
    {
        var selectedCount = GetSplitPlaybackCameraCount();
        SetToolbarToggleState("SplitCount:1", selectedCount == 1);
        SetToolbarToggleState("SplitCount:4", selectedCount == 4);

        if (TryGetToolbarButtonBase("SplitCount:Down", out var downButton))
        {
            downButton.IsEnabled = selectedCount > 1;
        }

        if (TryGetToolbarButtonBase("SplitCount:Up", out var upButton))
        {
            upButton.IsEnabled = selectedCount < RegistryService.MaxInstanceSlots;
        }
    }

    private void UpdateActiveVisibleCameraToolbarSelection()
    {
        for (var slot = 1; slot <= 4; slot++)
        {
            var tag = $"ActiveSlot:{slot}";
            if (!TryGetToolbarToggleButton(tag, out var button))
            {
                continue;
            }

            button.IsEnabled = slot <= RegistryService.MaxInstanceSlots;
            button.IsChecked = this._slot == slot;
        }

        if (TryGetToolbarButtonBase("ActiveSlot:Down", out var downButton))
        {
            downButton.IsEnabled = this._slot > 1;
        }

        if (TryGetToolbarButtonBase("ActiveSlot:Up", out var upButton))
        {
            upButton.IsEnabled = this._slot < RegistryService.MaxInstanceSlots;
        }
    }

    private void UpdateViewToolbarSelection()
    {
        SetToolbarToggleState("StartStop", this.StartStreamMenuItem.IsChecked);
        SetToolbarToggleState("Quality", this.HighQualityMenuItem.IsChecked);
        SetToolbarToggleState("Sound", this.StreamSoundMenuItem.IsChecked);
        SetToolbarToggleState("GlobalSound", this._globalSettings.EnableSound);
        SetToolbarToggleState("Topmost", this.TopmostMenuItem.IsChecked);
        SetToolbarToggleState("Fps", this.FpsOverlayMenuItem.IsChecked);
        UpdateStartStopToolbarContent();
    }

    private void UpdateStartStopToolbarContent()
    {
        if (!TryGetToolbarToggleButton("StartStop", out var button))
        {
            return;
        }

        var key = button.IsChecked == true ? "ToolbarOn" : "ToolbarOff";
        var text = LocalizationService.Translate(this._language, key);
        if (button.Content is TextBlock textBlock)
        {
            textBlock.Text = text;
            return;
        }

        button.Content = text;
    }

    private void SetToolbarToggleState(string tag, bool isChecked)
    {
        if (TryGetToolbarToggleButton(tag, out var button))
        {
            button.IsChecked = isChecked;
        }
    }

    private bool TryGetToolbarButtonBase(string tag, out ButtonBase button)
    {
        button = null!;
        if (this.FindName("SplitPlaybackToolBar") is not ToolBar toolbar)
        {
            return false;
        }

        var match = toolbar.Items
            .OfType<ButtonBase>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
        if (match is null)
        {
            return false;
        }

        button = match;
        return true;
    }

    private bool TryGetToolbarToggleButton(string tag, out ToggleButton button)
    {
        button = null!;
        if (!TryGetToolbarButtonBase(tag, out var buttonBase) || buttonBase is not ToggleButton toggleButton)
        {
            return false;
        }

        button = toggleButton;
        return true;
    }

    private void ApplySplitPlaybackCameraCount(int count, bool restartPlayback)
    {
        var clampedCount = Math.Clamp(count, 1, RegistryService.MaxInstanceSlots);
        var previousCount = GetSplitPlaybackCameraCount();
        this._globalSettings.SplitPlaybackCameraCount = clampedCount;
        this._registryService.SaveGlobalSettings(this._globalSettings);
        UpdateSplitPlaybackToolbarSelection();
        UpdateActiveVisibleCameraToolbarSelection();

        if (clampedCount == previousCount)
        {
            return;
        }

        BuildPlaybackGrid();
        RefreshViewportSettings();

        if (restartPlayback && this._playerService.State is PlayerState.Playing or PlayerState.Connecting)
        {
            StartPlayback();
        }
    }

    private void SplitPlaybackToolbarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tagText } ||
            !tagText.StartsWith("SplitCount:", StringComparison.Ordinal) ||
            !int.TryParse(tagText["SplitCount:".Length..], out var count))
        {
            return;
        }

        ApplySplitPlaybackCameraCount(count, restartPlayback: true);
    }

    private void SplitPlaybackToolbarStepDown_OnClick(object sender, RoutedEventArgs e)
    {
        ApplySplitPlaybackCameraCount(GetSplitPlaybackCameraCount() - 1, restartPlayback: true);
    }

    private void SplitPlaybackToolbarStepUp_OnClick(object sender, RoutedEventArgs e)
    {
        ApplySplitPlaybackCameraCount(GetSplitPlaybackCameraCount() + 1, restartPlayback: true);
    }

    private void ActiveVisibleCameraToolbarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tagText } ||
            !tagText.StartsWith("ActiveSlot:", StringComparison.Ordinal) ||
            !int.TryParse(tagText["ActiveSlot:".Length..], out var slot))
        {
            return;
        }

        if (slot < 1 || slot > RegistryService.MaxInstanceSlots)
        {
            return;
        }

        SelectCameraSlotAndPromptForSettings(slot, restartPlayback: false);
    }

    private void ActiveVisibleCameraToolbarStepDown_OnClick(object sender, RoutedEventArgs e)
    {
        var targetSlot = Math.Clamp(this._slot - 1, 1, RegistryService.MaxInstanceSlots);
        SelectCameraSlotAndPromptForSettings(targetSlot, restartPlayback: false);
    }

    private void ActiveVisibleCameraToolbarStepUp_OnClick(object sender, RoutedEventArgs e)
    {
        var targetSlot = Math.Clamp(this._slot + 1, 1, RegistryService.MaxInstanceSlots);
        SelectCameraSlotAndPromptForSettings(targetSlot, restartPlayback: false);
    }

    private void SelectCameraSlotAndPromptForSettings(int slot, bool restartPlayback)
    {
        var previousSlot = this._slot;
        var shouldResumePreviousPlayback = this._playerService.State is PlayerState.Playing or PlayerState.Connecting;
        var streamNumber = RegistryService.GetStreamNumberFromGlobalSettings(this._globalSettings);
        var targetSettings = this._registryService.LoadSettings(slot, streamNumber);
        var shouldPromptForSettings = !IsSettingsValid(targetSettings);

        if (shouldPromptForSettings)
        {
            StopPlayback();
            foreach (var viewport in this._viewports.Values)
            {
                viewport.VideoImage.Source = null;
            }

            SetPresetsMenuUnavailable();
            SetSlotStatus(this._slot, "Disconnected");
            UpdateStreamMenuState(this._playerService.State);
        }

        SelectCameraSlot(
            slot,
            restartPlayback: shouldPromptForSettings ? false : restartPlayback,
            allowAutoPlaybackFromCurrentState: !shouldPromptForSettings);
        if (shouldPromptForSettings)
        {
            var saved = OpenSettingsDialog();
            if (!saved)
            {
                RestorePreviousCameraSelection(previousSlot, shouldResumePreviousPlayback);
            }
        }
    }

    private void RestorePreviousCameraSelection(int previousSlot, bool resumePlayback)
    {
        var streamNumber = RegistryService.GetStreamNumberFromGlobalSettings(this._globalSettings);
        this._settings = this._registryService.LoadSettings(previousSlot, streamNumber);
        this._slot = previousSlot;
        BuildPlaybackGrid();
        SelectCameraSlot(previousSlot, restartPlayback: false, allowAutoPlaybackFromCurrentState: false);

        if (resumePlayback)
        {
            StartPlayback();
        }
    }

    private void OnPrimaryPlayerStateChanged(PlayerState state)
    {
        OnPlayerStateChangedForSlot(this._slot, state, isPrimarySlot: true);
    }

    private void OnBackgroundPlayerStateChanged(int slot, PlayerState state)
    {
        OnPlayerStateChangedForSlot(slot, state, isPrimarySlot: false);
    }

    private void OnPlayerStateChangedForSlot(int slot, PlayerState state, bool isPrimarySlot)
    {
        RunOnUiThread(() =>
        {
            if (Interlocked.Exchange(ref this._isApplyingPlayerStateUi, 1) == 1)
            {
                return;
            }

            try
            {
                if (isPrimarySlot)
                {
                    UpdateStreamMenuState(state);
                }

                ApplyFpsOverlaySettings();
                ApplyStateToSlotStatus(slot, state, isPrimarySlot, clearSelectedSourceOnConnecting: isPrimarySlot);

                if (state == PlayerState.Disconnected)
                {
                    ClearPresetCacheForSlot(slot);
                    ClearRuntimeCapabilityCacheForSlot(slot);
                    if (isPrimarySlot && this._isFullscreen)
                    {
                        ExitFullscreen();
                    }
                }

                RefreshNavigationOverlayControlStates();
            }
            finally
            {
                Interlocked.Exchange(ref this._isApplyingPlayerStateUi, 0);
            }
        }, DispatcherPriority.Normal);
    }

    private void ApplyStateToSlotStatus(int slot, PlayerState state, bool isPrimarySlot, bool clearSelectedSourceOnConnecting = false)
    {
        if (isPrimarySlot && state is PlayerState.Connecting or PlayerState.Disconnecting or PlayerState.Disconnected)
        {
            SetPresetsMenuUnavailable();
        }

        switch (state)
        {
            case PlayerState.Connecting:
                if (clearSelectedSourceOnConnecting && this._slot == slot)
                {
                    GetSelectedVideoImage().Source = null;
                }

                var attempt = ResolveConnectionAttemptForSlot(slot);
                SetSlotStatusWithText(slot, "ConnectingAttempt", BuildConnectingAttemptText(slot, attempt));
                break;
            case PlayerState.Playing:
                SetSlotStatusVisibility(slot, false);
                break;
            case PlayerState.Disconnecting:
                SetSlotStatus(slot, "Disconnecting");
                break;
            case PlayerState.Disconnected:
                SetSlotStatus(slot, "Disconnected");
                break;
            default:
                SetSlotStatus(slot, "Disconnected");
                break;
        }

        UpdatePresetsOverlayVisibility();
    }

    private void SetSlotStatus(int slot, string statusResourceKey)
    {
        if (!this._viewports.TryGetValue(slot, out var viewport))
        {
            return;
        }

        viewport.StatusResourceKey = statusResourceKey;
        viewport.StatusText.Text = LocalizationService.Translate(this._language, statusResourceKey);
        viewport.StatusOverlay.Visibility = Visibility.Visible;
    }

    private void SetSlotStatusWithText(int slot, string statusResourceKey, string text)
    {
        if (!this._viewports.TryGetValue(slot, out var viewport))
        {
            return;
        }

        viewport.StatusResourceKey = statusResourceKey;
        viewport.StatusText.Text = text;
        viewport.StatusOverlay.Visibility = Visibility.Visible;
    }

    private int ResolveConnectionAttemptForSlot(int slot)
    {
        var attempt = slot == this._slot
            ? this._playerService.GetCurrentConnectionAttempt()
            : (this._backgroundPlayers.TryGetValue(slot, out var player) ? player.GetCurrentConnectionAttempt() : 0);
        return Math.Max(1, attempt);
    }

    private string BuildConnectingAttemptText(int slot, int attempt)
    {
        var isHungarian = LocalizationService.NormalizeLanguage(this._language) == LocalizationService.Hungarian;
        var attemptLabel = isHungarian
            ? $"{attempt}.\u00A0próbálkozás"
            : $"attempt\u00A0{attempt}";
        return string.Format(
            GetLanguageCulture(),
            LocalizationService.Translate(this._language, "ConnectingToCamAttempt"),
            slot,
            attemptLabel);
    }

    private string BuildSlotStatusText(int slot, string statusResourceKey)
    {
        return statusResourceKey switch
        {
            "Disconnected" => string.Format(GetLanguageCulture(), LocalizationService.Translate(this._language, "DisconnectedFromCam"), slot),
            "Disconnecting" => string.Format(GetLanguageCulture(), LocalizationService.Translate(this._language, "DisconnectingFromCam"), slot),
            "ResolvingOnvifStream" => string.Format(GetLanguageCulture(), LocalizationService.Translate(this._language, "ResolvingOnvifStreamForCam"), slot),
            "OnvifResolutionFailed" => string.Format(GetLanguageCulture(), LocalizationService.Translate(this._language, "OnvifResolutionFailedForCam"), slot),
            "FailedToStartPlayback" => string.Format(GetLanguageCulture(), LocalizationService.Translate(this._language, "FailedToStartPlaybackForCam"), slot),
            _ => LocalizationService.Translate(this._language, statusResourceKey)
        };
    }

    private CultureInfo GetLanguageCulture()
    {
        return LocalizationService.NormalizeLanguage(this._language) == LocalizationService.Hungarian
            ? CultureInfo.GetCultureInfo("hu-HU")
            : CultureInfo.GetCultureInfo("en-US");
    }

    private void SetSlotStatusVisibility(int slot, bool isVisible)
    {
        if (!this._viewports.TryGetValue(slot, out var viewport))
        {
            return;
        }

        viewport.StatusOverlay.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAllViewportStatuses()
    {
        foreach (var viewport in this._viewports.Values)
        {
            var state = GetPlayerStateForSlot(viewport.Slot);
            if (state == PlayerState.Playing)
            {
                SetSlotStatusVisibility(viewport.Slot, false);
                continue;
            }

            var key = state switch
            {
                PlayerState.Connecting => "ConnectingAttempt",
                PlayerState.Disconnecting => "Disconnecting",
                _ => viewport.StatusResourceKey
            };
            if (string.IsNullOrWhiteSpace(key))
            {
                key = "Disconnected";
            }

            if (state == PlayerState.Connecting)
            {
                var attempt = ResolveConnectionAttemptForSlot(viewport.Slot);
                SetSlotStatusWithText(viewport.Slot, "ConnectingAttempt", BuildConnectingAttemptText(viewport.Slot, attempt));
            }
            else
            {
                SetSlotStatus(viewport.Slot, key);
            }
        }
    }

    private void StartStopToolbarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button)
        {
            return;
        }

        if (button.IsChecked == true)
        {
            StartStreamMenuItem_OnClick(this.StartStreamMenuItem, e);
        }
        else
        {
            StopStreamMenuItem_OnClick(this.StopStreamMenuItem, e);
        }

        UpdateViewToolbarSelection();
    }

    private void QualityToolbarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button)
        {
            return;
        }

        if (button.IsChecked == true)
        {
            HighQualityMenuItem_OnClick(this.HighQualityMenuItem, e);
        }
        else
        {
            LowBandwidthMenuItem_OnClick(this.LowBandwidthMenuItem, e);
        }

        UpdateViewToolbarSelection();
    }

    private void SoundToolbarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button)
        {
            return;
        }

        this.StreamSoundMenuItem.IsChecked = button.IsChecked == true;
        StreamSoundMenuItem_OnClick(this.StreamSoundMenuItem, e);
        UpdateViewToolbarSelection();
    }

    private void TopmostToolbarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button)
        {
            return;
        }

        this.TopmostMenuItem.IsChecked = button.IsChecked == true;
        TopmostMenuItem_OnClick(this.TopmostMenuItem, e);
        UpdateViewToolbarSelection();
    }

    private void GlobalSoundToolbarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button)
        {
            return;
        }

        this._globalSettings.EnableSound = button.IsChecked == true;
        this._registryService.SaveGlobalSettings(this._globalSettings);
        UpdateViewToolbarSelection();

        if (this._playerService.State is PlayerState.Playing or PlayerState.Connecting)
        {
            StartPlayback();
        }
    }

    private void FpsToolbarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button)
        {
            return;
        }

        this.FpsOverlayMenuItem.IsChecked = button.IsChecked == true;
        FpsOverlayMenuItem_OnClick(this.FpsOverlayMenuItem, e);
        UpdateViewToolbarSelection();
    }

    private Image GetSelectedVideoImage()
    {
        if (this._viewports.TryGetValue(this._slot, out var viewport))
        {
            return viewport.VideoImage;
        }

        return this._viewports.Values.First().VideoImage;
    }

    private void BuildPlaybackGrid()
    {
        this.PlaybackGrid.Children.Clear();
        this.PlaybackGrid.RowDefinitions.Clear();
        this.PlaybackGrid.ColumnDefinitions.Clear();
        this._viewports.Clear();

        var visibleCount = GetSplitPlaybackCameraCount();
        this._slot = Math.Clamp(this._slot, 1, RegistryService.MaxInstanceSlots);
        var rowCount = visibleCount == 1 ? 1 : 2;
        var columns = visibleCount == 1 ? 1 : Math.Max(1, (int)Math.Ceiling(visibleCount / 2d));
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            this.PlaybackGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (var i = 0; i < columns; i++)
        {
            this.PlaybackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var streamNumber = RegistryService.GetStreamNumberFromGlobalSettings(this._globalSettings);
        var maxStartSlot = Math.Max(1, RegistryService.MaxInstanceSlots - visibleCount + 1);
        var startSlot = Math.Clamp(this._slot - visibleCount + 1, 1, maxStartSlot);
        for (var index = 0; index < visibleCount; index++)
        {
            var slot = startSlot + index;
            var row = rowCount == 1 ? 0 : index % 2;
            var column = rowCount == 1 ? 0 : index / 2;

            var cell = new Border
            {
                BorderThickness = new Thickness(0),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Black,
                Tag = slot
            };

            var cellGrid = new Grid();

            var video = new Image
            {
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(video, BitmapScalingMode.NearestNeighbor);

            var fpsOverlay = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8),
                Padding = new Thickness(8, 3, 8, 3),
                Background = Brushes.Black,
                CornerRadius = new CornerRadius(4),
                Visibility = Visibility.Collapsed
            };

            var fpsText = new TextBlock
            {
                Foreground = Brushes.White,
                Text = string.Empty
            };
            var fpsDetailsText = new TextBlock
            {
                Foreground = Brushes.White,
                Text = string.Empty,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420
            };
            var fpsStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            fpsStack.Children.Add(fpsText);
            fpsStack.Children.Add(fpsDetailsText);
            fpsOverlay.Child = fpsStack;

            var statusOverlay = new Grid
            {
                Visibility = Visibility.Visible,
                IsHitTestVisible = false
            };
            statusOverlay.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = Brushes.Black,
                Opacity = 0.55
            });
            var statusText = new TextBlock
            {
                Text = LocalizationService.Translate(this._language, "Disconnected"),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 23,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 8, 12, 8)
            };
            statusOverlay.Children.Add(statusText);

            cellGrid.Children.Add(video);
            cellGrid.Children.Add(fpsOverlay);
            cellGrid.Children.Add(statusOverlay);

            cell.MouseEnter += Viewport_OnMouseEnter;
            cell.MouseLeftButtonDown += Viewport_OnMouseLeftButtonDown;
            cell.Child = cellGrid;

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            this.PlaybackGrid.Children.Add(cell);

            var viewportSettings = slot == this._slot
                ? this._settings
                : this._registryService.LoadSettings(slot, streamNumber);
            this._viewports[slot] = new CameraViewport
            {
                Slot = slot,
                VideoImage = video,
                FpsOverlayBorder = fpsOverlay,
                FpsOverlayText = fpsText,
                FpsOverlayDetailsText = fpsDetailsText,
                SelectionBorder = cell,
                StatusOverlay = statusOverlay,
                StatusText = statusText,
                Settings = viewportSettings
            };
            ApplyVideoStretchModeForViewport(this._viewports[slot]);
        }

        if (!this._viewports.ContainsKey(this._slot))
        {
            this._slot = this._viewports.Keys.Min();
            this._settings = this._viewports[this._slot].Settings;
        }

        this._hoveredSlot = this._slot;
        UpdateSelectionBorder();
        UpdateActiveVisibleCameraToolbarSelection();
        ApplySharedOverlayStyle();
        ApplyFpsOverlaySettings();
        UpdateOverlayText();
        UpdateAllViewportStatuses();

        if (IsLoaded)
        {
            QueueApplyStreamAspectRatioToWindow();
        }
    }

    private void RefreshViewportSettings(bool reloadSelectedFromRegistry = false)
    {
        var streamNumber = RegistryService.GetStreamNumberFromGlobalSettings(this._globalSettings);
        foreach (var viewport in this._viewports.Values)
        {
            if (viewport.Slot == this._slot && !reloadSelectedFromRegistry)
            {
                viewport.Settings = this._settings;
            }
            else
            {
                var loadedSettings = this._registryService.LoadSettings(viewport.Slot, streamNumber);
                viewport.Settings = loadedSettings;
                if (viewport.Slot == this._slot)
                {
                    this._settings = loadedSettings;
                }
            }

            ApplyVideoStretchModeForViewport(viewport);
        }

        ApplyFpsOverlaySettings();
        RefreshNavigationOverlayControlStates();
    }

    private void UpdateSelectionBorder()
    {
        var multi = GetSplitPlaybackCameraCount() > 1;
        foreach (var viewport in this._viewports.Values)
        {
            var selected = multi && viewport.Slot == this._slot;
            viewport.SelectionBorder.BorderThickness = selected ? new Thickness(2) : new Thickness(0);
            viewport.SelectionBorder.BorderBrush = selected ? Brushes.LightSkyBlue : Brushes.Transparent;
        }
    }

    private void Viewport_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int slot } || !this._viewports.ContainsKey(slot))
        {
            return;
        }

        this._hoveredSlot = slot;
        UpdatePresetsOverlayVisibility();
    }

    private void Viewport_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int slot } || !this._viewports.ContainsKey(slot))
        {
            return;
        }

        this._hoveredSlot = slot;
        SelectCameraSlot(slot, restartPlayback: false);
    }

    private void SelectCameraSlot(int slot, bool restartPlayback, bool allowAutoPlaybackFromCurrentState = true)
    {
        if (slot < 1 || slot > RegistryService.MaxInstanceSlots)
        {
            return;
        }

        if (!this._viewports.ContainsKey(slot))
        {
            var streamNumber = RegistryService.GetStreamNumberFromGlobalSettings(this._globalSettings);
            this._slot = slot;
            this._settings = this._registryService.LoadSettings(this._slot, streamNumber);
            this._registryService.SaveLastUsedCameraSlot(this._slot);
            BuildPlaybackGrid();
            this._playerService.Initialize(GetSelectedVideoImage());
            ApplySettingsToUiState();
            UpdateWindowTitle();
            UpdateCameraMenuSelection();
            UpdateSelectionBorder();
            UpdateActiveVisibleCameraToolbarSelection();
            UpdatePresetsOverlayVisibility();
            RefreshNavigationOverlayControlStates();

            if (restartPlayback || (allowAutoPlaybackFromCurrentState && this._playerService.State is PlayerState.Playing or PlayerState.Connecting))
            {
                StartPlayback();
            }

            return;
        }

        var previousSlot = this._slot;
        var switchedPrimaryPlayer = false;
        if (!restartPlayback && previousSlot != slot)
        {
            switchedPrimaryPlayer = TrySwitchPrimaryPlayerToSlot(previousSlot, slot);
        }

        this._slot = slot;
        this._settings = this._viewports[slot].Settings;
        this._registryService.SaveLastUsedCameraSlot(this._slot);
        if (!switchedPrimaryPlayer)
        {
            this._playerService.Initialize(GetSelectedVideoImage());
        }

        ApplySettingsToUiState();
        UpdateWindowTitle();
        UpdateCameraMenuSelection();
        UpdateSelectionBorder();
        UpdateActiveVisibleCameraToolbarSelection();
        UpdatePresetsOverlayVisibility();
        RefreshNavigationOverlayControlStates();

        if (restartPlayback || (!switchedPrimaryPlayer && previousSlot != slot && allowAutoPlaybackFromCurrentState && this._playerService.State is PlayerState.Playing or PlayerState.Connecting))
        {
            StartPlayback();
        }

        if (switchedPrimaryPlayer)
        {
            UpdateStreamMenuState(this._playerService.State);
        }
    }

    private bool TrySwitchPrimaryPlayerToSlot(int previousSlot, int targetSlot)
    {
        if (!this._backgroundPlayers.TryGetValue(targetSlot, out var targetPlayer) ||
            !this._viewports.TryGetValue(previousSlot, out var previousViewport) ||
            !this._viewports.TryGetValue(targetSlot, out var targetViewport))
        {
            return false;
        }

        var previousPrimary = this._playerService;
        previousPrimary.StreamAudioAvailabilityChanged -= this._primaryPlayerAudioHandler;
        previousPrimary.StateChanged -= OnPrimaryPlayerStateChanged;
        previousPrimary.StreamAspectRatioChanged -= OnStreamAspectRatioChanged;
        previousPrimary.StreamAspectRatioChanged += OnBackgroundStreamAspectRatioChanged;

        targetPlayer.StreamAspectRatioChanged -= OnBackgroundStreamAspectRatioChanged;
        if (this._backgroundPlayerAudioHandlers.TryGetValue(targetSlot, out var targetAudioHandler))
        {
            targetPlayer.StreamAudioAvailabilityChanged -= targetAudioHandler;
            this._backgroundPlayerAudioHandlers.Remove(targetSlot);
        }
        if (this._backgroundPlayerStateHandlers.TryGetValue(targetSlot, out var targetStateHandler))
        {
            targetPlayer.StateChanged -= targetStateHandler;
            this._backgroundPlayerStateHandlers.Remove(targetSlot);
        }

        targetPlayer.StateChanged -= OnPrimaryPlayerStateChanged;
        targetPlayer.StateChanged += OnPrimaryPlayerStateChanged;
        targetPlayer.StreamAspectRatioChanged += OnStreamAspectRatioChanged;
        AttachPrimaryAudioAvailabilityHandler(targetPlayer);

        this._backgroundPlayers.Remove(targetSlot);
        if (this._backgroundPlayerStateHandlers.TryGetValue(previousSlot, out var previousStateHandler))
        {
            previousPrimary.StateChanged -= previousStateHandler;
            this._backgroundPlayerStateHandlers.Remove(previousSlot);
        }

        Action<PlayerState> primaryAsBackgroundStateHandler = state => OnBackgroundPlayerStateChanged(previousSlot, state);
        previousPrimary.StateChanged += primaryAsBackgroundStateHandler;
        this._backgroundPlayerStateHandlers[previousSlot] = primaryAsBackgroundStateHandler;
        Action<bool> primaryAsBackgroundAudioHandler = hasAudio => OnPlayerAudioAvailabilityChanged(previousSlot, hasAudio);
        previousPrimary.StreamAudioAvailabilityChanged += primaryAsBackgroundAudioHandler;
        this._backgroundPlayerAudioHandlers[previousSlot] = primaryAsBackgroundAudioHandler;
        this._backgroundPlayers[previousSlot] = previousPrimary;
        this._playerService = targetPlayer;

        previousPrimary.Initialize(previousViewport.VideoImage);
        this._playerService.Initialize(targetViewport.VideoImage);

        StartFpsCounterForSlot(previousSlot, previousPrimary);
        StartFpsCounterForSlot(targetSlot, this._playerService);
        return true;
    }

    private int IncrementRuntimeCapabilityLoadVersion(int slot)
    {
        var next = this._runtimeCapabilityLoadVersions.TryGetValue(slot, out var current) ? current + 1 : 1;
        this._runtimeCapabilityLoadVersions[slot] = next;
        return next;
    }

    private int GetRuntimeCapabilityLoadVersion(int slot)
    {
        return this._runtimeCapabilityLoadVersions.TryGetValue(slot, out var version) ? version : 0;
    }

    private StreamRuntimeCapabilities GetOrCreateRuntimeCapabilities(int slot)
    {
        if (!this._runtimeCapabilityCache.TryGetValue(slot, out var capabilities))
        {
            capabilities = new StreamRuntimeCapabilities
            {
                PtzCapabilities = new OnvifPtzCapabilities(),
                AdaptivePtzProfile = CreateAdaptivePtzProfile(new OnvifPtzCapabilities())
            };
            this._runtimeCapabilityCache[slot] = capabilities;
        }

        return capabilities;
    }

    private void ClearRuntimeCapabilityCacheForSlot(int slot)
    {
        if (slot <= 0)
        {
            return;
        }

        IncrementRuntimeCapabilityLoadVersion(slot);
        this._runtimeCapabilityLoadsInProgress.Remove(slot);
        this._runtimeCapabilityCache.Remove(slot);
    }

    private void AttachPrimaryAudioAvailabilityHandler(PlayerService? player = null)
    {
        var targetPlayer = player ?? this._playerService;

        if (this._primaryPlayerAudioHandler is not null)
        {
            targetPlayer.StreamAudioAvailabilityChanged -= this._primaryPlayerAudioHandler;
        }

        this._primaryPlayerAudioHandler = hasAudio => OnPlayerAudioAvailabilityChanged(this._slot, hasAudio);
        targetPlayer.StreamAudioAvailabilityChanged += this._primaryPlayerAudioHandler;
    }

    private void OnPlayerAudioAvailabilityChanged(int slot, bool hasAudio)
    {
        RunOnUiThread(() =>
        {
            var capabilities = GetOrCreateRuntimeCapabilities(slot);
            capabilities.HasAudio = hasAudio;
            RefreshNavigationOverlayControlStates();
        }, DispatcherPriority.Background);
    }

    private async Task EnsureRuntimeCapabilitiesLoadedAsync(int slot, AppSettings settings)
    {
        if (slot <= 0 || !this._viewports.ContainsKey(slot))
        {
            return;
        }

        var capabilities = GetOrCreateRuntimeCapabilities(slot);
        if (settings.UseOnvif)
        {
            if (capabilities.IsPtzInfoLoaded)
            {
                return;
            }

            if (!this._runtimeCapabilityLoadsInProgress.Add(slot))
            {
                return;
            }

            var requestVersion = IncrementRuntimeCapabilityLoadVersion(slot);
            try
            {
                var ptzCapabilities = await this._onvifService.GetPtzCapabilitiesAsync(settings, CancellationToken.None);
                if (GetRuntimeCapabilityLoadVersion(slot) != requestVersion)
                {
                    return;
                }

                capabilities.PtzCapabilities = ptzCapabilities;
                capabilities.AdaptivePtzProfile = CreateAdaptivePtzProfile(ptzCapabilities);
                capabilities.LastKnownZoomNormalized = ptzCapabilities.CurrentZoomNormalized;
                capabilities.PanTiltRuntimeGain = 1.0;
                capabilities.ZoomRuntimeGain = 1.0;
                capabilities.PanTiltConsecutiveFailures = 0;
                capabilities.ZoomConsecutiveFailures = 0;
                capabilities.LastMoveDirection = string.Empty;
                capabilities.MiniCalibrationCompleted = false;
                capabilities.IsPtzInfoLoaded = true;

                capabilities.MiniCalibrationCompleted = true;
            }
            catch
            {
                capabilities.IsPtzInfoLoaded = true;
            }
            finally
            {
                this._runtimeCapabilityLoadsInProgress.Remove(slot);
                RefreshNavigationOverlayControlStates();
                UpdateOverlayTextForSlot(slot);
            }
        }
        else
        {
            capabilities.PtzCapabilities = new OnvifPtzCapabilities();
            capabilities.AdaptivePtzProfile = CreateAdaptivePtzProfile(capabilities.PtzCapabilities);
            capabilities.LastKnownZoomNormalized = null;
            capabilities.PanTiltRuntimeGain = 1.0;
            capabilities.ZoomRuntimeGain = 1.0;
            capabilities.PanTiltConsecutiveFailures = 0;
            capabilities.ZoomConsecutiveFailures = 0;
            capabilities.LastMoveDirection = string.Empty;
            capabilities.MiniCalibrationCompleted = true;
            capabilities.IsPtzInfoLoaded = true;
            RefreshNavigationOverlayControlStates();
            UpdateOverlayTextForSlot(slot);
        }
    }

    private static AdaptivePtzProfile CreateAdaptivePtzProfile(OnvifPtzCapabilities capabilities)
    {
        var normalPanStep = ResolveNormalPanTiltStep(capabilities);
        var normalZoomStep = ResolveNormalZoomStep(capabilities);

        return new AdaptivePtzProfile
        {
            NormalPanTiltMinStep = normalPanStep,
            FastPanTiltMinStep = normalPanStep * AdaptiveFastMultiplierPanTilt,
            NormalZoomMinStep = normalZoomStep,
            FastZoomMinStep = normalZoomStep * AdaptiveFastMultiplierZoom,
            MaxScaleAtWide = AdaptiveWideScale,
            MaxScaleAtTele = AdaptiveTeleScale
        };
    }

    private static double ResolveNormalPanTiltStep(OnvifPtzCapabilities capabilities)
    {
        var candidates = new[]
        {
            capabilities.PanMinStep,
            capabilities.TiltMinStep
        };

        var min = candidates.Where(x => x.HasValue && x.Value > 0)
            .Select(x => x!.Value)
            .DefaultIfEmpty(FixedNavigationStepSize)
            .Min();

        return Math.Clamp(min, FixedNavigationStepSize / 10d, 0.25d);
    }

    private static double ResolveNormalZoomStep(OnvifPtzCapabilities capabilities)
    {
        var baseValue = capabilities.ZoomMinStep is > 0 ? capabilities.ZoomMinStep.Value : FixedZoomStepSize;
        return Math.Clamp(baseValue, FixedZoomStepSize / 10d, 0.5d);
    }

    private void ApplyVideoStretchModeForViewport(CameraViewport viewport)
    {
        var isAuto = string.Equals(viewport.Settings.AspectRatioMode, AppSettings.AutoAspectRatio, StringComparison.OrdinalIgnoreCase);
        viewport.VideoImage.Stretch = isAuto ? System.Windows.Media.Stretch.Uniform : System.Windows.Media.Stretch.Fill;
    }

    private int GetPresetTargetSlot()
    {
        return this._viewports.ContainsKey(this._hoveredSlot) ? this._hoveredSlot : 0;
    }

    private int IncrementPresetLoadVersion(int slot)
    {
        var next = this._presetLoadVersions.TryGetValue(slot, out var current) ? current + 1 : 1;
        this._presetLoadVersions[slot] = next;
        return next;
    }

    private int GetPresetLoadVersion(int slot)
    {
        return this._presetLoadVersions.TryGetValue(slot, out var version) ? version : 0;
    }

    private PlayerState GetPlayerStateForSlot(int slot)
    {
        if (slot == this._slot)
        {
            return this._playerService.State;
        }

        return this._backgroundPlayers.TryGetValue(slot, out var backgroundPlayer)
            ? backgroundPlayer.State
            : PlayerState.Stopped;
    }

    private void ApplyFpsOverlaySettings()
    {
        foreach (var viewport in this._viewports.Values)
        {
            var isConnected = GetPlayerStateForSlot(viewport.Slot) == PlayerState.Playing;
            viewport.FpsOverlayBorder.Visibility = viewport.Settings.ShowFpsOverlay && isConnected
                ? Visibility.Visible
                : Visibility.Collapsed;

            switch (viewport.Settings.FpsOverlayPosition)
            {
                case AppSettings.FpsOverlayPositionTopLeft:
                    viewport.FpsOverlayBorder.HorizontalAlignment = HorizontalAlignment.Left;
                    viewport.FpsOverlayBorder.VerticalAlignment = VerticalAlignment.Top;
                    break;
                case AppSettings.FpsOverlayPositionTopRight:
                    viewport.FpsOverlayBorder.HorizontalAlignment = HorizontalAlignment.Right;
                    viewport.FpsOverlayBorder.VerticalAlignment = VerticalAlignment.Top;
                    break;
                case AppSettings.FpsOverlayPositionBottomRight:
                    viewport.FpsOverlayBorder.HorizontalAlignment = HorizontalAlignment.Right;
                    viewport.FpsOverlayBorder.VerticalAlignment = VerticalAlignment.Bottom;
                    break;
                default:
                    viewport.FpsOverlayBorder.HorizontalAlignment = HorizontalAlignment.Left;
                    viewport.FpsOverlayBorder.VerticalAlignment = VerticalAlignment.Bottom;
                    break;
            }
        }

        UpdatePresetsOverlayPlacement();
    }

    private void UpdatePresetsOverlayPlacement()
    {
        var targetSlot = GetPresetTargetSlot();
        if (!this._viewports.TryGetValue(targetSlot, out var viewport) ||
            viewport.SelectionBorder.ActualWidth <= 0 ||
            viewport.SelectionBorder.ActualHeight <= 0)
        {
            return;
        }

        var fpsOnLeft = viewport.Settings.FpsOverlayPosition != AppSettings.FpsOverlayPositionTopRight
                        && viewport.Settings.FpsOverlayPosition != AppSettings.FpsOverlayPositionBottomRight;
        var viewportBounds = GetViewportPlaybackBounds(viewport);
        var overlayMargin = 10d;

        var maxWidth = viewportBounds.Width * PresetsOverlayMaxVideoFraction;
        var maxHeight = viewportBounds.Height * PresetsOverlayMaxVideoFraction;
        this.PresetsOverlayBorder.MaxWidth = maxWidth > 0 ? maxWidth : double.PositiveInfinity;
        this.PresetsOverlayBorder.MaxHeight = maxHeight > 0 ? maxHeight : double.PositiveInfinity;
        if (this.PresetsOverlayBorder.ActualWidth <= 0 || this.PresetsOverlayBorder.ActualHeight <= 0)
        {
            this.PresetsOverlayBorder.Measure(new Size(this.PresetsOverlayBorder.MaxWidth, this.PresetsOverlayBorder.MaxHeight));
        }

        if (this.NavigationOverlayBorder.ActualWidth <= 0 || this.NavigationOverlayBorder.ActualHeight <= 0)
        {
            this.NavigationOverlayBorder.Measure(new Size(viewportBounds.Width, viewportBounds.Height));
        }

        var presetsWidth = this.PresetsOverlayBorder.ActualWidth > 0 ? this.PresetsOverlayBorder.ActualWidth : this.PresetsOverlayBorder.DesiredSize.Width;
        var presetsHeight = this.PresetsOverlayBorder.ActualHeight > 0 ? this.PresetsOverlayBorder.ActualHeight : this.PresetsOverlayBorder.DesiredSize.Height;
        var navigationWidth = this.NavigationOverlayBorder.ActualWidth > 0 ? this.NavigationOverlayBorder.ActualWidth : this.NavigationOverlayBorder.DesiredSize.Width;
        var navigationHeight = this.NavigationOverlayBorder.ActualHeight > 0 ? this.NavigationOverlayBorder.ActualHeight : this.NavigationOverlayBorder.DesiredSize.Height;

        var presetsLeft = fpsOnLeft
            ? viewportBounds.Right - overlayMargin - presetsWidth
            : viewportBounds.Left + overlayMargin;
        var presetsTop = viewportBounds.Top + (viewportBounds.Height - presetsHeight) / 2d;

        var navigationLeft = viewportBounds.Left + overlayMargin;
        var navigationTop = viewportBounds.Top + (viewportBounds.Height - navigationHeight) / 2d;

        var minTop = viewportBounds.Top + overlayMargin;
        var maxPresetsTop = Math.Max(minTop, viewportBounds.Bottom - overlayMargin - presetsHeight);
        var maxNavigationTop = Math.Max(minTop, viewportBounds.Bottom - overlayMargin - navigationHeight);
        presetsTop = Math.Clamp(presetsTop, minTop, maxPresetsTop);
        navigationTop = Math.Clamp(navigationTop, minTop, maxNavigationTop);

        var minLeft = viewportBounds.Left + overlayMargin;
        var maxPresetsLeft = Math.Max(minLeft, viewportBounds.Right - overlayMargin - presetsWidth);
        var maxNavigationLeft = Math.Max(minLeft, viewportBounds.Right - overlayMargin - navigationWidth);
        presetsLeft = Math.Clamp(presetsLeft, minLeft, maxPresetsLeft);
        navigationLeft = Math.Clamp(navigationLeft, minLeft, maxNavigationLeft);

        this.PresetsOverlayBorder.HorizontalAlignment = HorizontalAlignment.Left;
        this.PresetsOverlayBorder.VerticalAlignment = VerticalAlignment.Top;
        this.PresetsOverlayBorder.Margin = new Thickness(Math.Max(0, presetsLeft), Math.Max(0, presetsTop), 0, 0);

        this.NavigationOverlayBorder.HorizontalAlignment = HorizontalAlignment.Left;
        this.NavigationOverlayBorder.VerticalAlignment = VerticalAlignment.Top;
        this.NavigationOverlayBorder.Margin = new Thickness(Math.Max(0, navigationLeft), Math.Max(0, navigationTop), 0, 0);
    }

    private Rect GetViewportPlaybackBounds(CameraViewport viewport)
    {
        var transform = viewport.SelectionBorder.TransformToAncestor(this.VideoHost);
        var origin = transform.Transform(new Point(0, 0));
        var bounds = new Rect(origin.X, origin.Y, viewport.SelectionBorder.ActualWidth, viewport.SelectionBorder.ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return bounds;
        }

        if (viewport.VideoImage.Stretch != System.Windows.Media.Stretch.Uniform)
        {
            return bounds;
        }

        if (!TryGetViewportNativeDimensions(viewport, out var nativeWidth, out var nativeHeight) || nativeWidth <= 0 || nativeHeight <= 0)
        {
            return bounds;
        }

        var videoAspect = nativeWidth / nativeHeight;
        if (videoAspect <= 0)
        {
            return bounds;
        }

        var boundsAspect = bounds.Width / bounds.Height;
        if (Math.Abs(boundsAspect - videoAspect) < 0.0001d)
        {
            return bounds;
        }

        if (videoAspect > boundsAspect)
        {
            var displayedHeight = bounds.Width / videoAspect;
            var top = bounds.Y + (bounds.Height - displayedHeight) / 2d;
            return new Rect(bounds.X, top, bounds.Width, displayedHeight);
        }

        var displayedWidth = bounds.Height * videoAspect;
        var left = bounds.X + (bounds.Width - displayedWidth) / 2d;
        return new Rect(left, bounds.Y, displayedWidth, bounds.Height);
    }

    private void ApplySharedOverlayStyle()
    {
        var fpsOverlayBrush = new SolidColorBrush(Colors.Black)
        {
            Opacity = FpsOverlayBackgroundOpacity
        };

        if (fpsOverlayBrush.CanFreeze)
        {
            fpsOverlayBrush.Freeze();
        }

        var panelsOverlayBrush = new SolidColorBrush(Colors.Black)
        {
            Opacity = PanelsOverlayBackgroundOpacity
        };

        if (panelsOverlayBrush.CanFreeze)
        {
            panelsOverlayBrush.Freeze();
        }

        foreach (var viewport in this._viewports.Values)
        {
            viewport.FpsOverlayBorder.Background = fpsOverlayBrush;
        }

        this.PresetsOverlayBorder.Background = panelsOverlayBrush;
        this.NavigationOverlayBorder.Background = panelsOverlayBrush;
    }

    private void UpdatePresetsOverlayBounds()
    {
        UpdatePresetsOverlayPlacement();
    }

    private void ApplyLocalization()
    {
        this.FileMenuItem.Header = LocalizationService.Translate(this._language, "File");
        this.GlobalSettingsMenuItem.Header = LocalizationService.Translate(this._language, "GlobalSettingsMenu");
        this.ExportMenuItem.Header = LocalizationService.Translate(this._language, "Export");
        this.ImportMenuItem.Header = LocalizationService.Translate(this._language, "Import");
        this.DefaultsMenuItem.Header = LocalizationService.Translate(this._language, "Defaults");
        this.ExitMenuItem.Header = LocalizationService.Translate(this._language, "Exit");
        this.CameraMenuItem.Header = LocalizationService.Translate(this._language, "Camera");
        this.ViewMenuItem.Header = LocalizationService.Translate(this._language, "View");
        this.CameraSettingsMenuItem.Header = LocalizationService.Translate(this._language, "CameraSettingsMenu");
        this.StartStreamMenuItem.Header = LocalizationService.Translate(this._language, "StartStream");
        this.StopStreamMenuItem.Header = LocalizationService.Translate(this._language, "StopStream");
        this.HighQualityMenuItem.Header = LocalizationService.Translate(this._language, "HighQuality");
        this.LowBandwidthMenuItem.Header = LocalizationService.Translate(this._language, "LowBandwidth");
        this.StreamSoundMenuItem.Header = LocalizationService.Translate(this._language, "StreamSound");
        this.TopmostMenuItem.Header = LocalizationService.Translate(this._language, "TopmostWindow");
        this.FpsOverlayMenuItem.Header = LocalizationService.Translate(this._language, "FpsOverlay");
        this.HelpRootMenuItem.Header = LocalizationService.Translate(this._language, "Help");
        this.HelpMenuItem.Header = LocalizationService.Translate(this._language, "Help");
        this.AboutMenuItem.Header = LocalizationService.Translate(this._language, "About");
        SetToolbarButtonToolTip("StartStop", "ToolbarStartStop");
        SetToolbarButtonToolTip("Quality", "ToolbarQuality");
        SetToolbarButtonToolTip("Sound", "ToolbarSound");
        SetToolbarButtonToolTip("GlobalSound", "ToolbarGlobalSound");
        SetToolbarButtonToolTip("Topmost", "ToolbarTopmost");
        SetToolbarButtonToolTip("Fps", "ToolbarFps");
        SetToolbarButtonToolTip("SplitCount:1", "ToolbarSplitCameraCount");
        SetToolbarButtonToolTip("SplitCount:4", "ToolbarSplitCameraCount");
        SetToolbarButtonToolTip("SplitCount:Down", "ToolbarSplitCameraCount");
        SetToolbarButtonToolTip("SplitCount:Up", "ToolbarSplitCameraCount");
        SetToolbarButtonToolTip("ActiveSlot:1", "ToolbarActiveCamera");
        SetToolbarButtonToolTip("ActiveSlot:2", "ToolbarActiveCamera");
        SetToolbarButtonToolTip("ActiveSlot:3", "ToolbarActiveCamera");
        SetToolbarButtonToolTip("ActiveSlot:4", "ToolbarActiveCamera");
        SetToolbarButtonToolTip("ActiveSlot:Down", "ToolbarActiveCamera");
        SetToolbarButtonToolTip("ActiveSlot:Up", "ToolbarActiveCamera");

        this.PresetsOverlayTitleText.Text = LocalizationService.Translate(this._language, "Presets").Replace("_", string.Empty);
        this.NavigationOverlayTitleText.Text = LocalizationService.Translate(this._language, "Navigation");
        this.NavigationVolumeLabelText.Text = LocalizationService.Translate(this._language, "NavigationVolume");
        UpdateCameraMenuLabels();
        UpdateOverlayText();
        foreach (var viewport in this._viewports.Values)
        {
            viewport.StatusText.Text = viewport.StatusResourceKey == "ConnectingAttempt"
                ? BuildConnectingAttemptText(viewport.Slot, ResolveConnectionAttemptForSlot(viewport.Slot))
                : BuildSlotStatusText(viewport.Slot, viewport.StatusResourceKey);
        }
    }

    private void SetToolbarButtonToolTip(string tag, string resourceKey)
    {
        if (TryGetToolbarButtonBase(tag, out var button))
        {
            button.ToolTip = LocalizationService.Translate(this._language, resourceKey);
        }
    }

    private void UpdateCameraMenuLabels()
    {
        var streamNumber = RegistryService.GetStreamNumberFromGlobalSettings(this._globalSettings);
        foreach (var item in this.CameraMenuItem.Items.OfType<MenuItem>())
        {
            if (int.TryParse(item.Tag?.ToString(), out var slotNumber) && slotNumber is >= 1 and <= CameraMenuMaxProfiles)
            {
                var slotSettings = this._registryService.LoadSettings(slotNumber, streamNumber);
                item.Header = GetEffectiveCameraName(slotSettings.CameraName, slotNumber);
            }
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        this._hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        this._hwndSource?.AddHook(WndProc);
    }

    private void UpdateWindowTitle()
    {
        var title = "hg5c_cam";

        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var cleanInformationalVersion = informationalVersion?.Split('+')[0];
        var version = !string.IsNullOrWhiteSpace(cleanInformationalVersion)
            ? cleanInformationalVersion
            : Assembly.GetExecutingAssembly().GetName().Version?.ToString()
              ?? "";
        if (!string.IsNullOrWhiteSpace(version))
        {
            title += $" v{version}";
        }

        if (this._slot > 0)
        {
            title += $" - {GetEffectiveCameraName(this._settings.CameraName, this._slot)}";
        }

        var qualityKey = this._globalSettings.UseSecondStream == 1 ? "LowBandwidth" : "HighQuality";
        var qualityLabel = RemoveAccessKeyMarker(LocalizationService.Translate(this._language, qualityKey));
        if (!string.IsNullOrWhiteSpace(qualityLabel))
        {
            title += $" - {qualityLabel}";
        }

        if (!string.IsNullOrWhiteSpace(this._settings.Url))
        {
            var urlPrefix = ExtractUrlPrefix(this._settings.Url);
            if (!string.IsNullOrWhiteSpace(urlPrefix))
            {
                title += $" - {urlPrefix}";
            }
        }

        Title = title;
    }

    private static string ExtractUrlPrefix(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    private string GetEffectiveCameraName(string? configuredName, int slot)
    {
        var trimmed = configuredName?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(trimmed)
            ? LocalizationService.GetDefaultCameraName(this._language, slot)
            : trimmed;
    }

    private void OnLoaded()
    {
        EnsureWindowVisibleOnDesktop();
        ApplyConfiguredAspectRatio();
        ApplyVideoStretchMode();
        if (IsAutoAspectRatioMode())
        {
            ApplyStreamAspectRatioToWindow();
        }

        if (!IsSettingsValid(this._settings))
        {
            OpenSettingsDialog();
        }
        else
        {
            StartPlayback();
        }
    }

    private static bool IsSettingsValid(AppSettings settings)
    {
        var hasRtsp = Uri.TryCreate(settings.Url, UriKind.Absolute, out var uri) &&
                      string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase) &&
                      !string.IsNullOrWhiteSpace(uri.Host) &&
                      ((!string.IsNullOrWhiteSpace(settings.Username) && !string.IsNullOrWhiteSpace(settings.Password)) || uri.UserInfo.Contains(':', StringComparison.Ordinal));

        var hasOnvifEndpoint = Uri.TryCreate(settings.OnvifDeviceServiceUrl, UriKind.Absolute, out var onvifUri) &&
                               (string.Equals(onvifUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) || string.Equals(onvifUri.Scheme, "https", StringComparison.OrdinalIgnoreCase));

        var hasOnvif = settings.UseOnvif && hasOnvifEndpoint;
        return hasRtsp || hasOnvif;
    }

    private async void StartPlayback()
    {
        try
        {
            SetPresetsMenuUnavailable();
            StopBackgroundPlayers();
            StopAllFpsCounters();
            this._playerService.Initialize(GetSelectedVideoImage());
            GetSelectedVideoImage().Source = null;

            var rtspUrl = await ResolveRtspUrlAsync(this._settings);
            if (string.IsNullOrWhiteSpace(rtspUrl))
            {
                SetSlotStatus(this._slot, "OnvifResolutionFailed");
                return;
            }

            this._registryService.SaveSettings(this._slot, this._settings);
            this._registryService.AddUrlToHistory(rtspUrl);
            ClearRuntimeCapabilityCacheForSlot(this._slot);
            _ = EnsureRuntimeCapabilitiesLoadedAsync(this._slot, this._settings);
            ApplyConfiguredAspectRatio();
            ApplyVideoStretchMode();
            this._playerService.Play(
                rtspUrl,
                this._settings.MaxFps,
                this._settings.ReconnectDelaySec,
                this._settings.ConnectionRetries,
                this._settings.NetworkTimeoutSec,
                this._settings.SoundEnabled && this._globalSettings.EnableSound,
                this._globalSettings.AudioOutputDeviceName,
                this._settings.SoundLevel);
            this._navigationStepSize = FixedNavigationStepSize;
            this._zoomStepSize = FixedZoomStepSize;
            StartFpsCounterForSlot(this._slot, this._playerService);
            _ = EnsurePresetCacheLoadedAsync(this._slot, this._settings);
            RefreshNavigationOverlayControlStates();

            _ = StartBackgroundPlayersAsync();
            _ = RefreshPresetsMenuAsync();
        }
        catch
        {
            SetPresetsMenuUnavailable();
            SetSlotStatus(this._slot, "FailedToStartPlayback");
        }
    }

    private async Task<string> ResolveRtspUrlAsync(AppSettings settings)
    {
        var previousRtspUrl = BuildRtspUrl(settings);
        var rtspUrl = previousRtspUrl;

        if (!settings.UseOnvif || !settings.AutoResolveRtspFromOnvif)
        {
            return rtspUrl;
        }

        try
        {
            SetSlotStatus(this._slot, "ResolvingOnvifStream");
            var streamInfo = await this._onvifService.ResolveRtspStreamAsync(settings);
            settings.OnvifDeviceServiceUrl = streamInfo.DeviceServiceUrl;
            settings.OnvifProfileToken = streamInfo.ProfileToken;
            settings.OnvifXSize = streamInfo.StreamWidth;
            settings.OnvifYSize = streamInfo.StreamHeight;
            var resolvedRtspUrl = BuildRtspUrl(settings, streamInfo.RtspUri);
            var rebasedRtspUrl = RebaseRtspHostToOnvifDeviceService(resolvedRtspUrl, streamInfo.DeviceServiceUrl);
            settings.Url = rebasedRtspUrl;
            settings.AutoResolveRtspFromOnvif = false;
            return rebasedRtspUrl;
        }
        catch
        {
            return rtspUrl;
        }
    }

    private async Task StartBackgroundPlayersAsync()
    {
        foreach (var viewport in this._viewports.Values.Where(v => v.Slot != this._slot))
        {
            viewport.VideoImage.Source = null;

            if (!IsSettingsValid(viewport.Settings))
            {
                viewport.VideoImage.Source = null;
                continue;
            }

            var rtspUrl = await ResolveRtspUrlAsync(viewport.Settings);
            if (string.IsNullOrWhiteSpace(rtspUrl))
            {
                viewport.VideoImage.Source = null;
                continue;
            }

            this._registryService.SaveSettings(viewport.Slot, viewport.Settings);
            ClearRuntimeCapabilityCacheForSlot(viewport.Slot);
            _ = EnsureRuntimeCapabilitiesLoadedAsync(viewport.Slot, viewport.Settings);
            var player = new PlayerService();
            player.Initialize(viewport.VideoImage);
            player.StreamAspectRatioChanged += OnBackgroundStreamAspectRatioChanged;
            var slot = viewport.Slot;
            Action<PlayerState> stateHandler = state => OnBackgroundPlayerStateChanged(slot, state);
            player.StateChanged += stateHandler;
            this._backgroundPlayerStateHandlers[slot] = stateHandler;
            Action<bool> audioHandler = hasAudio => OnPlayerAudioAvailabilityChanged(slot, hasAudio);
            player.StreamAudioAvailabilityChanged += audioHandler;
            this._backgroundPlayerAudioHandlers[slot] = audioHandler;
            player.Play(
                rtspUrl,
                viewport.Settings.MaxFps,
                viewport.Settings.ReconnectDelaySec,
                viewport.Settings.ConnectionRetries,
                viewport.Settings.NetworkTimeoutSec,
                viewport.Settings.SoundEnabled && this._globalSettings.EnableSound,
                this._globalSettings.AudioOutputDeviceName,
                viewport.Settings.SoundLevel);
            this._navigationStepSize = FixedNavigationStepSize;
            this._zoomStepSize = FixedZoomStepSize;
            this._backgroundPlayers[viewport.Slot] = player;
            StartFpsCounterForSlot(viewport.Slot, player);
            _ = EnsurePresetCacheLoadedAsync(viewport.Slot, viewport.Settings);
        }
    }

    private void StopBackgroundPlayers()
    {
        foreach (var pair in this._backgroundPlayers)
        {
            pair.Value.StreamAspectRatioChanged -= OnBackgroundStreamAspectRatioChanged;
            if (this._backgroundPlayerAudioHandlers.TryGetValue(pair.Key, out var audioHandler))
            {
                pair.Value.StreamAudioAvailabilityChanged -= audioHandler;
            }
            if (this._backgroundPlayerStateHandlers.TryGetValue(pair.Key, out var stateHandler))
            {
                pair.Value.StateChanged -= stateHandler;
            }

            pair.Value.Stop();
            StopFpsCounterForSlot(pair.Key);
            ClearPresetCacheForSlot(pair.Key);
            ClearRuntimeCapabilityCacheForSlot(pair.Key);
        }

        this._backgroundPlayers.Clear();
        this._backgroundPlayerStateHandlers.Clear();
        this._backgroundPlayerAudioHandlers.Clear();
    }

    private void StartFpsCounterForSlot(int slot, PlayerService player)
    {
        StopFpsCounterForSlot(slot);

        var counter = new FpsCounterService(
            () => player.ConsumeFrameCount(),
            () => player.ConsumePacketBytes());
        counter.FpsChanged += _ => UpdateOverlayTextForSlot(slot);
        counter.MemoryChanged += _ => UpdateOverlayTextForSlot(slot);
        this._fpsCounters[slot] = counter;
        counter.Start();
    }

    private void StopFpsCounterForSlot(int slot)
    {
        if (!this._fpsCounters.TryGetValue(slot, out var counter))
        {
            return;
        }

        counter.Stop();
        this._fpsCounters.Remove(slot);
    }

    private void StopAllFpsCounters()
    {
        foreach (var counter in this._fpsCounters.Values)
        {
            counter.Stop();
        }

        this._fpsCounters.Clear();
    }

    private Task RefreshPresetsMenuAsync()
    {
        var targetSlot = GetPresetTargetSlot();
        if (targetSlot <= 0)
        {
            this._presetsSlot = 0;
            this._isPresetsLoading = false;
            return Task.CompletedTask;
        }

        this._presetsSlot = targetSlot;
        var targetSettings = this._viewports.TryGetValue(targetSlot, out var viewport) ? viewport.Settings : this._settings;
        if (!targetSettings.UseOnvif)
        {
            this._isPresetsLoading = false;
            SetPresetsMenuUnavailable();
            return Task.CompletedTask;
        }

        if (!this._presetCache.TryGetValue(targetSlot, out var cachedPresets) || cachedPresets.Count == 0)
        {
            if (!this._presetLoadsInProgress.Contains(targetSlot))
            {
                _ = EnsurePresetCacheLoadedAsync(targetSlot, targetSettings);
            }

            this._isPresetsLoading = this._presetLoadsInProgress.Contains(targetSlot);
            UpdatePresetsOverlayVisibility();
            return Task.CompletedTask;
        }

        this.PresetsOverlayList.ItemsSource = cachedPresets;
        this._loadedPresetsSlot = targetSlot;
        this._isPresetsLoading = false;
        UpdatePresetsMenuState(GetPlayerStateForSlot(targetSlot));
        QueueOverlayPlacementRefresh();
        return Task.CompletedTask;
    }

    private async Task GotoPresetAsync(string presetToken)
    {
        if (string.IsNullOrWhiteSpace(presetToken))
        {
            return;
        }

        if (Interlocked.Exchange(ref this._isGotoPresetInProgress, 1) == 1)
        {
            return;
        }

        if (this._presetsSlot > 0 && this._presetsSlot != this._slot)
        {
            SelectCameraSlot(this._presetsSlot, restartPlayback: false);
        }

        try
        {
            await this._onvifService.GotoPresetAsync(this._settings, presetToken);
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref this._isGotoPresetInProgress, 0);
            UpdatePresetsMenuState(GetPlayerStateForSlot(this._slot));
        }
    }

    private void SetPresetsMenuUnavailable()
    {
        RunOnUiThread(() =>
        {
            this._loadedPresetsSlot = 0;
            this._isPresetsLoading = false;
            this.PresetsOverlayList.ItemsSource = null;
            UpdatePresetsOverlayVisibility();
        }, DispatcherPriority.Normal);
    }

    private async Task EnsurePresetCacheLoadedAsync(int slot, AppSettings settings)
    {
        if (slot <= 0 || !settings.UseOnvif)
        {
            return;
        }

        if (this._presetCache.ContainsKey(slot) || !this._presetLoadsInProgress.Add(slot))
        {
            return;
        }

        var requestVersion = IncrementPresetLoadVersion(slot);

        try
        {
            var presets = await this._onvifService.GetPresetsAsync(settings, CancellationToken.None);
            if (GetPresetLoadVersion(slot) != requestVersion)
            {
                return;
            }

            this._presetCache[slot] = presets.Take(PresetsDisplayLimit).ToList();
            if (GetPresetTargetSlot() == slot)
            {
                await RefreshPresetsMenuAsync();
            }
        }
        catch
        {
            this._presetCache.Remove(slot);
        }
        finally
        {
            this._presetLoadsInProgress.Remove(slot);
        }
    }

    private void ClearPresetCacheForSlot(int slot)
    {
        if (slot <= 0)
        {
            return;
        }

        IncrementPresetLoadVersion(slot);
        this._presetLoadsInProgress.Remove(slot);
        if (this._presetCache.Remove(slot) && this._loadedPresetsSlot == slot)
        {
            SetPresetsMenuUnavailable();
        }
    }

    private void OverlayPanel_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!this._isVideoHostHovered || GetPresetTargetSlot() <= 0)
        {
            return;
        }

        QueueOverlayPlacementRefresh();
    }

    private void QueueOverlayPlacementRefresh()
    {
        if (Interlocked.Exchange(ref this._isOverlayPlacementRefreshQueued, 1) == 1)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref this._isOverlayPlacementRefreshQueued, 0);
            if (!this._isVideoHostHovered || GetPresetTargetSlot() <= 0)
            {
                return;
            }

            if (Interlocked.Exchange(ref this._isApplyingOverlayPlacement, 1) == 1)
            {
                return;
            }

            try
            {
                UpdatePresetsOverlayPlacement();
            }
            finally
            {
                Interlocked.Exchange(ref this._isApplyingOverlayPlacement, 0);
            }
        }, DispatcherPriority.Background);
    }

    private string BuildRtspUrl(AppSettings settings)
    {
        return BuildRtspUrl(settings, settings.Url);
    }

    private static string BuildRtspUrl(AppSettings settings, string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)) return sourceUrl;
        var builder = new UriBuilder(uri);
        builder.Scheme = "rtsp";
        if (!string.IsNullOrWhiteSpace(settings.Username)) builder.UserName = Uri.EscapeDataString(settings.Username);
        if (!string.IsNullOrWhiteSpace(settings.Password)) builder.Password = Uri.EscapeDataString(settings.Password);
        return builder.Uri.ToString();
    }

    private static string RebaseRtspHostToOnvifDeviceService(string resolvedRtspUrl, string onvifDeviceServiceUrl)
    {
        if (!Uri.TryCreate(resolvedRtspUrl, UriKind.Absolute, out var resolvedUri))
        {
            return resolvedRtspUrl;
        }

        if (!Uri.TryCreate(onvifDeviceServiceUrl, UriKind.Absolute, out var onvifUri) ||
            string.IsNullOrWhiteSpace(onvifUri.Host))
        {
            return resolvedRtspUrl;
        }

        var rebased = new UriBuilder(resolvedUri)
        {
            Host = onvifUri.Host
        };

        return rebased.Uri.ToString();
    }

    private static bool HasValidRtspUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(uri.Host);
    }

    private void UpdateStreamMenuState(PlayerState state)
    {
        var streamRunning = state is PlayerState.Connecting or PlayerState.Playing;
        this.StartStreamMenuItem.IsChecked = streamRunning;
        this.StopStreamMenuItem.IsChecked = !streamRunning;
        UpdateViewToolbarSelection();
        UpdatePresetsMenuState(state);
    }

    private void UpdatePresetsMenuState(PlayerState state)
    {
        _ = state;
        UpdatePresetsOverlayVisibility();
    }

    private PresetOverlayState ResolvePresetOverlayState(int targetSlot)
    {
        if (targetSlot <= 0 || !this._viewports.TryGetValue(targetSlot, out var viewport))
        {
            return PresetOverlayState.Hidden;
        }

        var canShowNavigation = this._isVideoHostHovered &&
                                GetPlayerStateForSlot(targetSlot) == PlayerState.Playing &&
                                viewport.Settings.UseOnvif;
        if (!canShowNavigation)
        {
            return PresetOverlayState.Hidden;
        }

        var presetsReady = this._loadedPresetsSlot == targetSlot &&
                           !this._isPresetsLoading &&
                           !this._presetLoadsInProgress.Contains(targetSlot) &&
                           this.PresetsOverlayList.Items.Count > 0;
        return presetsReady ? PresetOverlayState.Ready : PresetOverlayState.NavigationOnly;
    }

    private void UpdatePresetsOverlayVisibility()
    {
        if (Interlocked.Exchange(ref this._isUpdatingPresetsOverlayVisibility, 1) == 1)
        {
            return;
        }

        try
        {
            var targetSlot = GetPresetTargetSlot();
            if (targetSlot > 0)
            {
                _ = RefreshPresetsMenuAsync();
            }

            UpdateNavigationVolumeUi(targetSlot);

            var overlayState = ResolvePresetOverlayState(targetSlot);
            switch (overlayState)
            {
                case PresetOverlayState.Ready:
                    this.PresetsOverlayBorder.Visibility = Visibility.Visible;
                    this.NavigationOverlayBorder.Visibility = Visibility.Visible;
                    this.PresetsOverlayList.IsEnabled = true;
                    UpdatePresetsOverlayPlacement();
                    QueueOverlayPlacementRefresh();
                    RefreshNavigationOverlayControlStates();
                    break;
                case PresetOverlayState.NavigationOnly:
                    this.PresetsOverlayBorder.Visibility = Visibility.Collapsed;
                    this.NavigationOverlayBorder.Visibility = Visibility.Visible;
                    this.PresetsOverlayList.IsEnabled = false;
                    UpdatePresetsOverlayPlacement();
                    QueueOverlayPlacementRefresh();
                    RefreshNavigationOverlayControlStates();
                    break;
                default:
                    this.PresetsOverlayBorder.Visibility = Visibility.Collapsed;
                    this.NavigationOverlayBorder.Visibility = Visibility.Collapsed;
                    this.PresetsOverlayList.IsEnabled = false;
                    StopNavigationMove();
                    RefreshNavigationOverlayControlStates();
                    break;
            }
        }
        finally
        {
            Interlocked.Exchange(ref this._isUpdatingPresetsOverlayVisibility, 0);
        }
    }

    private void UpdateOverlayText()
    {
        foreach (var slot in this._viewports.Keys)
        {
            UpdateOverlayTextForSlot(slot);
        }
    }

    private void UpdateOverlayTextForSlot(int slot)
    {
        RunOnUiThread(() =>
        {
            if (!this._viewports.TryGetValue(slot, out var viewport))
            {
                return;
            }

            var isConnected = GetPlayerStateForSlot(slot) == PlayerState.Playing;
            viewport.FpsOverlayBorder.Visibility = viewport.Settings.ShowFpsOverlay && isConnected
                ? Visibility.Visible
                : Visibility.Collapsed;

            var sizeText = string.Empty;
            if (viewport.VideoImage.Source is System.Windows.Media.Imaging.BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            {
                sizeText = $"{LocalizationService.Translate(this._language, "Size")}: {bitmap.PixelWidth}x{bitmap.PixelHeight}  ";
            }

            if (!this._fpsCounters.TryGetValue(slot, out var counter))
            {
                viewport.FpsOverlayText.Text =
                    $"{sizeText}{LocalizationService.Translate(this._language, "Memory")} 000.0 MB  FPS: 00  0000 kbps";
                ApplyPtzDiagnosticsOverlay(slot, viewport, null);
                return;
            }

            viewport.FpsOverlayText.Text =
                $"{sizeText}{LocalizationService.Translate(this._language, "Memory")}"
                + $" {counter.CurrentMemoryMb:000.0} MB"
                + $"  FPS: {counter.CurrentFps:D2}"
                + $"  {counter.CurrentBitrateKbps:0000} kbps";
            ApplyPtzDiagnosticsOverlay(slot, viewport, counter);
        }, DispatcherPriority.Background);
    }

    private void ApplyPtzDiagnosticsOverlay(int slot, CameraViewport viewport, FpsCounterService? counter)
    {
        var hasRuntime = this._runtimeCapabilityCache.TryGetValue(slot, out var runtime);
        var caps = hasRuntime ? runtime!.PtzCapabilities : new OnvifPtzCapabilities();
        var stepLabel = LocalizationService.Translate(this._language, "StepLabel");
        var defaultLabel = LocalizationService.Translate(this._language, "DefaultLabel");
        var npt = ResolveAdaptiveStepSizes(slot, isFast: false).PanTiltStep;
        var fpt = ResolveAdaptiveStepSizes(slot, isFast: true).PanTiltStep;
        var nz = ResolveAdaptiveStepSizes(slot, isFast: false).ZoomStep;
        var fz = ResolveAdaptiveStepSizes(slot, isFast: true).ZoomStep;
        var znorm = hasRuntime ? ResolveCurrentZoomNormalized(runtime!) : null;

        //var detailsText =
        //    $"NPT/FPT {stepLabel}: {npt:0.#####}/{fpt:0.#####}  "
        //    + $"NZ/FZ {stepLabel}: {nz:0.#####}/{fz:0.#####}  "
        //    + $"Znorm: {(znorm.HasValue ? znorm.Value.ToString("0.###", CultureInfo.InvariantCulture) : "-")}  "
        //    + $"{defaultLabel}: {BuildDefaultUsageText(caps)}";
        var detailsText =
            $"PT: {npt:0.#####}/{fpt:0.#####}  "
            + $"Z: {nz:0.#####}/{fz:0.#####}  "
            + $"Zn: {(znorm.HasValue ? znorm.Value.ToString("0.###", CultureInfo.InvariantCulture) : "-")}  "
            + $"{defaultLabel}: {BuildDefaultUsageText(caps)}";

        viewport.FpsOverlayDetailsText.Text = string.Empty;
        viewport.FpsOverlayDetailsText.Inlines.Clear();
        //viewport.FpsOverlayDetailsText.Inlines.Add(new Run("PTZ: "));
        viewport.FpsOverlayDetailsText.Inlines.Add(BuildCapabilityRun('P', caps.HasPan));
        viewport.FpsOverlayDetailsText.Inlines.Add(BuildCapabilityRun('T', caps.HasTilt));
        viewport.FpsOverlayDetailsText.Inlines.Add(BuildCapabilityRun('Z', caps.HasZoom));
        viewport.FpsOverlayDetailsText.Inlines.Add(new Run("  "));
        viewport.FpsOverlayDetailsText.Inlines.Add(new Run(detailsText));

        _ = counter;
    }

    private static Run BuildCapabilityRun(char key, bool isActive)
    {
        return new Run(key.ToString())
        {
            Foreground = isActive ? Brushes.White : new SolidColorBrush(Color.FromRgb(120, 120, 120))
        };
    }

    private static string BuildDefaultUsageText(OnvifPtzCapabilities caps)
    {
        var tokens = new List<string>();
        if (caps.PanStepUsesDefault)
        {
            tokens.Add("P");
        }

        if (caps.TiltStepUsesDefault)
        {
            tokens.Add("T");
        }

        if (caps.ZoomStepUsesDefault)
        {
            tokens.Add("Z");
        }

        return tokens.Count == 0 ? "-" : string.Join('/', tokens);
    }

    private void RestartWindowDebounce()
    {
        this._windowSaveDebounce.Stop();
        this._windowSaveDebounce.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmSizing || WindowState != WindowState.Normal)
        {
            return IntPtr.Zero;
        }

        var aspectRatio = GetEffectiveAspectRatio();
        if (aspectRatio <= 0)
        {
            return IntPtr.Zero;
        }

        var (chromeWidth, chromeHeight) = GetWindowChromeMetricsInDevicePixels();
        var rect = Marshal.PtrToStructure<RectNative>(lParam);
        EnforceAspectRatio(ref rect, wParam.ToInt32(), aspectRatio, chromeWidth, chromeHeight);
        Marshal.StructureToPtr(rect, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private static void EnforceAspectRatio(ref RectNative rect, int edge, double aspectRatio, double chromeWidth, double chromeHeight)
    {
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var videoWidth = width - chromeWidth;
        var videoHeight = height - chromeHeight;
        if (videoWidth <= 0 || videoHeight <= 0)
        {
            return;
        }

        var targetHeight = (int)Math.Round(videoWidth / aspectRatio + chromeHeight);
        var targetWidth = (int)Math.Round(videoHeight * aspectRatio + chromeWidth);

        switch (edge)
        {
            case WmszLeft:
            case WmszRight:
                ApplyVerticalAdjustment(ref rect, edge, targetHeight);
                break;
            case WmszTop:
            case WmszBottom:
                ApplyHorizontalAdjustment(ref rect, edge, targetWidth);
                break;
            case WmszTopLeft:
            case WmszTopRight:
            case WmszBottomLeft:
            case WmszBottomRight:
                var useWidthDriver = Math.Abs(targetHeight - height) <= Math.Abs(targetWidth - width);
                if (useWidthDriver)
                {
                    ApplyVerticalAdjustment(ref rect, edge, targetHeight);
                }
                else
                {
                    ApplyHorizontalAdjustment(ref rect, edge, targetWidth);
                }
                break;
        }
    }

    private static void ApplyVerticalAdjustment(ref RectNative rect, int edge, int targetHeight)
    {
        switch (edge)
        {
            case WmszTop:
            case WmszTopLeft:
            case WmszTopRight:
                rect.Top = rect.Bottom - targetHeight;
                break;
            default:
                rect.Bottom = rect.Top + targetHeight;
                break;
        }
    }

    private static void ApplyHorizontalAdjustment(ref RectNative rect, int edge, int targetWidth)
    {
        switch (edge)
        {
            case WmszLeft:
            case WmszTopLeft:
            case WmszBottomLeft:
                rect.Left = rect.Right - targetWidth;
                break;
            default:
                rect.Right = rect.Left + targetWidth;
                break;
        }
    }

    private void OnStreamAspectRatioChanged(double aspectRatio)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (aspectRatio <= 0)
            {
                return;
            }

            if (IsAutoAspectRatioMode())
            {
                if (TryGetStoredOnvifAspectRatio(this._settings, out var storedAspectRatio) && storedAspectRatio > 0)
                {
                    this._targetAspectRatio = storedAspectRatio;
                }
                else
                {
                    this._targetAspectRatio = aspectRatio;
                }
            }

            QueueApplyStreamAspectRatioToWindow();
        }, DispatcherPriority.Normal);
    }

    private double GetEffectiveAspectRatio()
    {
        if (TryGetCompositeViewportAspectRatio(out var compositeAspectRatio))
        {
            return compositeAspectRatio;
        }

        if (!IsAutoAspectRatioMode())
        {
            return this._targetAspectRatio;
        }

        if (this._viewports.TryGetValue(this._slot, out var selectedViewport) &&
            TryGetViewportNativeDimensions(selectedViewport, out var selectedWidth, out var selectedHeight) &&
            selectedWidth > 0 &&
            selectedHeight > 0)
        {
            return selectedWidth / selectedHeight;
        }

        if (this._targetAspectRatio > 0)
        {
            return this._targetAspectRatio;
        }

        if (GetSelectedVideoImage().Source is System.Windows.Media.Imaging.BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
        {
            return bitmap.PixelWidth / (double)bitmap.PixelHeight;
        }

        return this._targetAspectRatio;
    }

    private static bool TryGetStoredOnvifAspectRatio(AppSettings settings, out double aspectRatio)
    {
        aspectRatio = 0d;
        if (settings.OnvifXSize is not > 0 || settings.OnvifYSize is not > 0)
        {
            return false;
        }

        aspectRatio = settings.OnvifXSize.Value / (double)settings.OnvifYSize.Value;
        return aspectRatio > 0;
    }

    private bool TryGetCompositeViewportAspectRatio(out double aspectRatio)
    {
        aspectRatio = 0d;
        if (this._viewports.Count == 0)
        {
            return false;
        }

        var rowWidths = new Dictionary<int, double>();
        var columnHeights = new Dictionary<int, double>();
        var fallbackAspectRatio = ResolveFallbackViewportAspectRatio();

        foreach (var viewport in this._viewports.Values)
        {
            if (!TryGetViewportNativeDimensions(viewport, out var width, out var height))
            {
                if (fallbackAspectRatio <= 0)
                {
                    continue;
                }

                width = fallbackAspectRatio;
                height = 1d;
            }

            var row = Grid.GetRow(viewport.SelectionBorder);
            var column = Grid.GetColumn(viewport.SelectionBorder);

            if (rowWidths.TryGetValue(row, out var currentRowWidth))
            {
                rowWidths[row] = currentRowWidth + width;
            }
            else
            {
                rowWidths[row] = width;
            }

            if (columnHeights.TryGetValue(column, out var currentColumnHeight))
            {
                columnHeights[column] = currentColumnHeight + height;
            }
            else
            {
                columnHeights[column] = height;
            }
        }

        if (rowWidths.Count == 0 || columnHeights.Count == 0)
        {
            return false;
        }

        var maxRowWidth = rowWidths.Values.Max();
        var maxColumnHeight = columnHeights.Values.Max();
        if (maxRowWidth <= 0 || maxColumnHeight <= 0)
        {
            return false;
        }

        aspectRatio = maxRowWidth / maxColumnHeight;
        return aspectRatio > 0;
    }

    private double ResolveFallbackViewportAspectRatio()
    {
        foreach (var viewport in this._viewports.Values)
        {
            if (TryGetViewportNativeDimensions(viewport, out var width, out var height) && width > 0 && height > 0)
            {
                return width / height;
            }
        }

        return this._targetAspectRatio > 0 ? this._targetAspectRatio : 0d;
    }

    private bool TryGetViewportNativeDimensions(CameraViewport viewport, out double width, out double height)
    {
        width = 0d;
        height = 0d;

        if (TryParseAspectRatioMode(viewport.Settings.AspectRatioMode, out var configuredAspectRatio) && configuredAspectRatio > 0)
        {
            width = configuredAspectRatio;
            height = 1d;
            return true;
        }

        if (viewport.Settings.OnvifXSize is > 0 && viewport.Settings.OnvifYSize is > 0)
        {
            width = viewport.Settings.OnvifXSize.Value / (double)viewport.Settings.OnvifYSize.Value;
            height = 1d;
            return true;
        }

        if (viewport.VideoImage.Source is System.Windows.Media.Imaging.BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
        {
            width = bitmap.PixelWidth / (double)bitmap.PixelHeight;
            height = 1d;
            return true;
        }

        var streamAspectRatio = viewport.Slot == this._slot
            ? this._playerService.GetStreamAspectRatio()
            : (this._backgroundPlayers.TryGetValue(viewport.Slot, out var backgroundPlayer)
                ? backgroundPlayer.GetStreamAspectRatio()
                : null);
        if (streamAspectRatio.HasValue && streamAspectRatio.Value > 0)
        {
            width = streamAspectRatio.Value;
            height = 1d;
            return true;
        }

        return false;
    }

    private void OnBackgroundStreamAspectRatioChanged(double aspectRatio)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (aspectRatio <= 0)
            {
                return;
            }

            QueueApplyStreamAspectRatioToWindow();
        }, DispatcherPriority.Background);
    }

    private void ApplyConfiguredAspectRatio()
    {
        if (TryParseAspectRatioMode(this._settings.AspectRatioMode, out var configuredAspectRatio))
        {
            this._targetAspectRatio = configuredAspectRatio;
            ApplyStreamAspectRatioToWindow();
        }
    }

    private bool IsAutoAspectRatioMode()
    {
        return string.Equals(this._settings.AspectRatioMode, AppSettings.AutoAspectRatio, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyVideoStretchMode()
    {
        if (this._viewports.TryGetValue(this._slot, out var viewport))
        {
            ApplyVideoStretchModeForViewport(viewport);
        }
    }

    private static bool TryParseAspectRatioMode(string? mode, out double aspectRatio)
    {
        aspectRatio = 0;
        if (string.IsNullOrWhiteSpace(mode) || string.Equals(mode, AppSettings.AutoAspectRatio, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = mode.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !double.TryParse(parts[0], out var width) || !double.TryParse(parts[1], out var height) || width <= 0 || height <= 0)
        {
            return false;
        }

        aspectRatio = width / height;
        return aspectRatio > 0;
    }

    private void ApplyStreamAspectRatioToWindow()
    {
        var aspectRatio = GetEffectiveAspectRatio();
        if (aspectRatio <= 0)
        {
            UpdatePlaybackGridBoundsForAspectRatio();
            return;
        }

        if (!IsLoaded || this.VideoHost.ActualWidth <= 0 || this.VideoHost.ActualHeight <= 0)
        {
            return;
        }

        if (WindowState != WindowState.Normal)
        {
            UpdatePlaybackGridBoundsForAspectRatio();
            return;
        }

        var (chromeWidth, chromeHeight) = GetWindowChromeMetrics();
        var workArea = SystemParameters.WorkArea;
        var maxWidth = workArea.Width * DesktopUsageLimit;
        var maxHeight = workArea.Height * DesktopUsageLimit;
        var maxVideoWidth = Math.Max(1d, maxWidth - chromeWidth);
        var maxVideoHeight = Math.Max(1d, maxHeight - chromeHeight);

        var targetVideoHeight = this.VideoHost.ActualHeight > 0 ? this.VideoHost.ActualHeight : Math.Max(1d, ActualHeight - chromeHeight);
        var targetVideoWidth = targetVideoHeight * aspectRatio;

        var scale = Math.Min(1d, Math.Min(maxVideoWidth / targetVideoWidth, maxVideoHeight / targetVideoHeight));
        targetVideoWidth *= scale;
        targetVideoHeight *= scale;

        var targetWidth = targetVideoWidth + chromeWidth;
        var targetHeight = targetVideoHeight + chromeHeight;

        Width = targetWidth;
        Height = targetHeight;

        var maxLeft = Math.Max(workArea.Left, workArea.Right - Width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - Height);
        Left = Math.Min(Math.Max(Left, workArea.Left), maxLeft);
        Top = Math.Min(Math.Max(Top, workArea.Top), maxTop);

        UpdatePlaybackGridBoundsForAspectRatio();
    }

    private void UpdatePlaybackGridBoundsForAspectRatio()
    {
        if (!IsLoaded || this.VideoHost.ActualWidth <= 0 || this.VideoHost.ActualHeight <= 0)
        {
            return;
        }

        var aspectRatio = GetEffectiveAspectRatio();
        if (aspectRatio <= 0)
        {
            ResetPlaybackGridBounds();
            return;
        }

        var hostWidth = this.VideoHost.ActualWidth;
        var hostHeight = this.VideoHost.ActualHeight;
        var targetWidth = hostWidth;
        var targetHeight = targetWidth / aspectRatio;
        if (targetHeight > hostHeight)
        {
            targetHeight = hostHeight;
            targetWidth = targetHeight * aspectRatio;
        }

        this.PlaybackGrid.HorizontalAlignment = HorizontalAlignment.Center;
        this.PlaybackGrid.VerticalAlignment = VerticalAlignment.Center;
        this.PlaybackGrid.Width = targetWidth;
        this.PlaybackGrid.Height = targetHeight;
    }

    private void ResetPlaybackGridBounds()
    {
        this.PlaybackGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        this.PlaybackGrid.VerticalAlignment = VerticalAlignment.Stretch;
        this.PlaybackGrid.Width = double.NaN;
        this.PlaybackGrid.Height = double.NaN;
    }

    private void QueueApplyStreamAspectRatioToWindow()
    {
        if (this._isAspectRatioApplyQueued)
        {
            return;
        }

        this._isAspectRatioApplyQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            this._isAspectRatioApplyQueued = false;
            if (!IsLoaded || this.VideoHost.ActualWidth <= 0 || this.VideoHost.ActualHeight <= 0)
            {
                return;
            }

            ApplyStreamAspectRatioToWindow();
        }, DispatcherPriority.ContextIdle);
    }

    private (double ChromeWidth, double ChromeHeight) GetWindowChromeMetrics()
    {
        var chromeWidth = ActualWidth - this.VideoHost.ActualWidth;
        var chromeHeight = ActualHeight - this.VideoHost.ActualHeight;
        if (double.IsNaN(chromeWidth) || chromeWidth < 0) chromeWidth = 0;
        if (double.IsNaN(chromeHeight) || chromeHeight < 0) chromeHeight = 0;
        return (chromeWidth, chromeHeight);
    }

    private (double ChromeWidth, double ChromeHeight) GetWindowChromeMetricsInDevicePixels()
    {
        var (chromeWidthDip, chromeHeightDip) = GetWindowChromeMetrics();
        var matrix = this._hwndSource?.CompositionTarget?.TransformToDevice;
        var scaleX = matrix?.M11 ?? 1d;
        var scaleY = matrix?.M22 ?? 1d;
        return (chromeWidthDip * scaleX, chromeHeightDip * scaleY);
    }

    private void SaveWindowMetrics()
    {
        this._windowSaveDebounce.Stop();
        if (WindowState == WindowState.Normal)
        {
            this._settings.WindowLeft = (int)Left;
            this._settings.WindowTop = (int)Top;
            this._settings.WindowWidth = (int)Width;
            this._settings.WindowHeight = (int)Height;
            this._registryService.SaveSettings(this._slot, this._settings);
        }
    }

    private void OnWindowStateChanged()
    {
        if (this._isFullscreen || WindowStyle == WindowStyle.None)
        {
            return;
        }

        if (WindowState is not (WindowState.Normal or WindowState.Maximized))
        {
            return;
        }

        var isMaximized = WindowState == WindowState.Maximized;
        if (this._settings.WindowMaximized == isMaximized)
        {
            return;
        }

        this._settings.WindowMaximized = isMaximized;
        this._registryService.SaveSettings(this._slot, this._settings);
    }

    private void ApplySavedWindowPlacement()
    {
        if (!this._settings.WindowMaximized && WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }

        if (this._settings.WindowWidth.HasValue && this._settings.WindowHeight.HasValue)
        {
            Width = this._settings.WindowWidth.Value;
            Height = this._settings.WindowHeight.Value;
        }
        if (this._settings.WindowLeft.HasValue && this._settings.WindowTop.HasValue)
        {
            Left = this._settings.WindowLeft.Value;
            Top = this._settings.WindowTop.Value;
        }

        if (this._settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(() => EnsureWindowVisibleOnDesktop(), DispatcherPriority.Normal);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                RunOnUiThread(HandleSystemSuspend, DispatcherPriority.Send);
                break;
            case PowerModes.Resume:
                RunOnUiThread(HandleSystemResume, DispatcherPriority.Background);
                break;
        }
    }

    private void HandleSystemSuspend()
    {
        if (this._isShuttingDown)
        {
            return;
        }

        this._resumePlaybackAfterPowerResume = IsAnyPlaybackActive();
        if (!this._resumePlaybackAfterPowerResume)
        {
            return;
        }

        StopPlayback();
    }

    private void HandleSystemResume()
    {
        if (this._isShuttingDown || !this._resumePlaybackAfterPowerResume)
        {
            return;
        }

        this._resumePlaybackAfterPowerResume = false;
        if (!IsSettingsValid(this._settings))
        {
            SetPresetsMenuUnavailable();
            SetSlotStatus(this._slot, "Disconnected");
            UpdateStreamMenuState(this._playerService.State);
            return;
        }

        StartPlayback();
    }

    private bool IsAnyPlaybackActive()
    {
        if (this._playerService.State is PlayerState.Playing or PlayerState.Connecting)
        {
            return true;
        }

        return this._backgroundPlayers.Values.Any(player => player.State is PlayerState.Playing or PlayerState.Connecting);
    }

    private void RunOnUiThread(Action action, DispatcherPriority priority)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = Dispatcher.BeginInvoke(action, priority);
    }

    private void EnsureWindowVisibleOnDesktop()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var desktopLeft = SystemParameters.VirtualScreenLeft;
        var desktopTop = SystemParameters.VirtualScreenTop;
        var desktopWidth = SystemParameters.VirtualScreenWidth;
        var desktopHeight = SystemParameters.VirtualScreenHeight;
        if (desktopWidth <= 0 || desktopHeight <= 0)
        {
            return;
        }

        var width = Width;
        var height = Height;
        if (double.IsNaN(width) || width <= 0)
        {
            width = ActualWidth;
        }

        if (double.IsNaN(height) || height <= 0)
        {
            height = ActualHeight;
        }

        width = Math.Max(MinWidth > 0 ? MinWidth : 1d, width);
        height = Math.Max(MinHeight > 0 ? MinHeight : 1d, height);

        var resized = false;
        if (width > desktopWidth)
        {
            width = desktopWidth;
            resized = true;
        }

        if (height > desktopHeight)
        {
            height = desktopHeight;
            resized = true;
        }

        if (resized)
        {
            Width = width;
            Height = height;
        }

        var left = Left;
        var top = Top;
        if (double.IsNaN(left) || double.IsInfinity(left))
        {
            left = desktopLeft;
        }

        if (double.IsNaN(top) || double.IsInfinity(top))
        {
            top = desktopTop;
        }

        var maxLeft = desktopLeft + desktopWidth - width;
        var maxTop = desktopTop + desktopHeight - height;
        if (maxLeft < desktopLeft)
        {
            maxLeft = desktopLeft;
        }

        if (maxTop < desktopTop)
        {
            maxTop = desktopTop;
        }

        Left = Math.Clamp(left, desktopLeft, maxLeft);
        Top = Math.Clamp(top, desktopTop, maxTop);
    }

    private void CameraSettingsMenuItem_OnClick(object sender, RoutedEventArgs e) => OpenSettingsDialog();

    private void GlobalSettingsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        this._globalSettings = this._registryService.LoadGlobalSettings();
        var wasAlwaysMaximizedPlayback = this._globalSettings.AlwaysMaximizedPlayback;
        var dialog = new GlobalSettingsDialog(this._globalSettings, this._language, this._playerService.GetAudioOutputDevices) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        this._globalSettings = dialog.ResultSettings;
        this._registryService.SaveGlobalSettings(this._globalSettings);
        var isTopmostWindow = this._globalSettings.TopmostMainWindow;
        Topmost = isTopmostWindow;
        this.TopmostMenuItem.IsChecked = isTopmostWindow;
        ApplyStreamQualityMenuState();
        if (this._globalSettings.AlwaysMaximizedPlayback)
        {
            WindowState = WindowState.Maximized;
        }
        else if (wasAlwaysMaximizedPlayback && WindowState == WindowState.Maximized && !this._settings.WindowMaximized)
        {
            WindowState = WindowState.Normal;
        }

        ApplySplitPlaybackCameraCount(this._globalSettings.SplitPlaybackCameraCount, restartPlayback: false);

        var selectedLanguage = LocalizationService.NormalizeLanguage(dialog.SelectedLanguage);
        if (this._language != selectedLanguage)
        {
            this._language = selectedLanguage;
            this._registryService.SaveLanguage(this._language);
            ApplyLocalization();
            ApplyMenuShortcutTexts();
            UpdateWindowTitle();
        }

        if (this._playerService.State is PlayerState.Playing or PlayerState.Connecting)
        {
            StartPlayback();
        }
    }

    private bool OpenSettingsDialog(bool startPlaybackAfterSave = true)
    {
        var dialog = new SetupDialog(this._settings, this._registryService.LoadUrlHistory(), this._language, this._slot) { Owner = this };
        dialog.Title = BuildCameraSettingsDialogTitle();
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        this._onvifService.InvalidateCache();
        this._settings = dialog.ResultSettings;
        ApplySettingsToUiState();
        ApplySavedWindowPlacement();
        EnsureWindowVisibleOnDesktop();
        UpdateWindowTitle();
        this._registryService.SaveSettings(this._slot, this._settings);
        if (this._viewports.TryGetValue(this._slot, out var viewport))
        {
            viewport.Settings = this._settings;
            ApplyVideoStretchModeForViewport(viewport);
        }

        RefreshViewportSettings();
        UpdateCameraMenuLabels();
        UpdateCameraMenuSelection();

        if (startPlaybackAfterSave)
        {
            StartPlayback();
        }
        else
        {
            SetPresetsMenuUnavailable();
            SetSlotStatus(this._slot, "Disconnected");
            UpdateStreamMenuState(this._playerService.State);
        }

        return true;
    }

    private string BuildCameraSettingsDialogTitle()
    {
        var settingsTitle = RemoveAccessKeyMarker(LocalizationService.Translate(this._language, "CameraSettings"));
        var cameraName = RemoveAccessKeyMarker(GetEffectiveCameraName(this._settings.CameraName, this._slot));
        var qualityKey = this._globalSettings.UseSecondStream == 1 ? "LowBandwidth" : "HighQuality";
        var qualityLabel = RemoveAccessKeyMarker(LocalizationService.Translate(this._language, qualityKey));

        return this._slot > 0
            ? $"{settingsTitle} - {cameraName} - {qualityLabel}"
            : $"{settingsTitle} - {qualityLabel}";
    }

    private static string RemoveAccessKeyMarker(string text) => text.Replace("_", string.Empty);

    private bool IsHungarianShortcutsActive() => LocalizationService.NormalizeLanguage(this._language) == LocalizationService.Hungarian;

    private string GetShortcutDisplayText(string englishShortcut, string hungarianShortcut)
    {
        return IsHungarianShortcutsActive() ? hungarianShortcut : englishShortcut;
    }

    private void ApplyMenuShortcutTexts()
    {
        this.GlobalSettingsMenuItem.InputGestureText = GetShortcutDisplayText("Ctrl+P", "Ctrl+P");
        this.CameraSettingsMenuItem.InputGestureText = GetShortcutDisplayText("Ctrl+A", "Ctrl+A");
        this.StartStreamMenuItem.InputGestureText = GetShortcutDisplayText("Ctrl+R", "Ctrl+I");
        this.StopStreamMenuItem.InputGestureText = GetShortcutDisplayText("Ctrl+O", "Ctrl+L");
        this.HighQualityMenuItem.InputGestureText = GetShortcutDisplayText("Ctrl+Q", "Ctrl+M");
        this.LowBandwidthMenuItem.InputGestureText = GetShortcutDisplayText("Ctrl+L", "Ctrl+O");
        this.StreamSoundMenuItem.InputGestureText = GetShortcutDisplayText("Ctrl+S", "Ctrl+H");
        this.TopmostMenuItem.InputGestureText = GetShortcutDisplayText("Ctrl+T", "Ctrl+F");
        this.FpsOverlayMenuItem.InputGestureText = GetShortcutDisplayText("Ctrl+U", "Ctrl+U");
        this.HelpMenuItem.InputGestureText = GetShortcutDisplayText("Ctrl+H", "Ctrl+S");

        foreach (var item in this.CameraMenuItem.Items.OfType<MenuItem>())
        {
            if (int.TryParse(item.Tag?.ToString(), out var slotNumber) && slotNumber is >= 1 and <= CameraMenuMaxProfiles)
            {
                item.InputGestureText = $"Ctrl+{slotNumber}";
            }
        }
    }

    private void ApplySettingsToUiState()
    {
        var isTopmostWindow = this._registryService.LoadTopmostWindow();
        Topmost = isTopmostWindow;
        this.TopmostMenuItem.IsChecked = isTopmostWindow;
        this.FpsOverlayMenuItem.IsChecked = this._settings.ShowFpsOverlay;
        this.StreamSoundMenuItem.IsChecked = this._settings.SoundEnabled;
        UpdateViewToolbarSelection();
        ApplyFpsOverlaySettings();
        RefreshNavigationOverlayControlStates();
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    private void CameraProfileMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            !int.TryParse(menuItem.Tag?.ToString(), out var targetSlot) ||
            targetSlot is < 1 or > CameraMenuMaxProfiles)
        {
            return;
        }

        SwitchCameraProfile(targetSlot);
    }

    private void UpdateCameraMenuSelection()
    {
        foreach (var item in this.CameraMenuItem.Items.OfType<MenuItem>())
        {
            var itemSlot = int.TryParse(item.Tag?.ToString(), out var parsedSlot) ? parsedSlot : -1;
            item.IsChecked = this._slot >= 1 && this._slot <= CameraMenuMaxProfiles && itemSlot == this._slot;
        }
    }

    private void SwitchCameraProfile(int targetSlot, bool forceReload = false)
    {
        if (targetSlot < 1 || targetSlot > CameraMenuMaxProfiles)
        {
            return;
        }

        if (forceReload)
        {
            RefreshViewportSettings(reloadSelectedFromRegistry: true);
        }

        SelectCameraSlotAndPromptForSettings(targetSlot, restartPlayback: forceReload);
    }

    private void ExportMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = SettingsExportFileName,
            Filter = "Config files (*.cnf)|*.cnf|All files (*.*)|*.*",
            DefaultExt = ".cnf",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var yaml = SerializeCameraSettingsToYaml(this._registryService.LoadAllCameraSettings(forExport: true));
        File.WriteAllText(dialog.FileName, yaml, Encoding.UTF8);
    }

    private void ImportMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var initialDirectory = AppContext.BaseDirectory;
        var dialog = new OpenFileDialog
        {
            FileName = "",
            Filter = "Config files (*.cnf)|*.cnf|All files (*.*)|*.*",
            DefaultExt = ".cnf",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ImportCameraSettingsFromFile(this._registryService, dialog.FileName, clearExisting: false, this._language);
            SwitchCameraProfile(1, true);
            UpdateCameraMenuLabels();
        }
        catch (Exception ex)
        {
            AppMessageDialog.Show(
                this,
                ex.Message,
                "hg5c_cam",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                this._language);
        }
    }

    private void DefaultsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var result = AppMessageDialog.Show(
            this,
            LocalizationService.Translate(this._language, "ConfirmClearSettings"),
            LocalizationService.Translate(this._language, "ConfirmClearSettingsTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            this._language);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        this._registryService.ResetAllCameraSettings();
        SwitchCameraProfile(1, true);
        UpdateCameraMenuLabels();
    }

    private void StartStreamMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (!IsSettingsValid(this._settings))
        {
            OpenSettingsDialog();
            return;
        }

        StartPlayback();
    }

    private void StopStreamMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        StopPlayback();
    }

    private void StopPlayback()
    {
        StopBackgroundPlayers();
        StopAllFpsCounters();
        ClearPresetCacheForSlot(this._slot);
        ClearRuntimeCapabilityCacheForSlot(this._slot);
        this._playerService.Stop();
        RefreshNavigationOverlayControlStates();
    }

    private void HighQualityMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (this._globalSettings.UseSecondStream == 0)
        {
            return;
        }

        this._globalSettings.UseSecondStream = 0;
        ApplyStreamQualityMenuState();
        this._registryService.SaveGlobalSettings(this._globalSettings);
        ApplySelectedStreamSettings();
    }

    private void LowBandwidthMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (this._globalSettings.UseSecondStream == 1)
        {
            return;
        }

        this._globalSettings.UseSecondStream = 1;
        ApplyStreamQualityMenuState();
        this._registryService.SaveGlobalSettings(this._globalSettings);
        ApplySelectedStreamSettings();
    }

    private void StreamSoundMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        this._settings.SoundEnabled = this.StreamSoundMenuItem.IsChecked;
        StartPlayback();
    }

    private void TopmostMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var isTopmostWindow = this.TopmostMenuItem.IsChecked;
        Topmost = isTopmostWindow;
        this._globalSettings.TopmostMainWindow = isTopmostWindow;
        this._registryService.SaveTopmostWindow(isTopmostWindow);
    }

    private void FpsOverlayMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        this._settings.ShowFpsOverlay = this.FpsOverlayMenuItem.IsChecked;
        ApplyFpsOverlaySettings();
        this._registryService.SaveSettings(this._slot, this._settings);
    }

    private void ApplyStreamQualityMenuState()
    {
        var useSecondStream = this._globalSettings.UseSecondStream == 1;
        this.HighQualityMenuItem.IsChecked = !useSecondStream;
        this.LowBandwidthMenuItem.IsChecked = useSecondStream;
        UpdateViewToolbarSelection();
    }

    private void ApplySelectedStreamSettings()
    {
        var streamNumber = RegistryService.GetStreamNumberFromGlobalSettings(this._globalSettings);
        this._onvifService.InvalidateCache();
        foreach (var viewport in this._viewports.Values)
        {
            ClearRuntimeCapabilityCacheForSlot(viewport.Slot);
        }
        this._settings = this._registryService.LoadSettings(this._slot, streamNumber);
        if (this._viewports.TryGetValue(this._slot, out var selectedViewport))
        {
            selectedViewport.Settings = this._settings;
        }

        RefreshViewportSettings();

        ApplySettingsToUiState();
        ApplySavedWindowPlacement();
        EnsureWindowVisibleOnDesktop();
        UpdateCameraMenuLabels();
        UpdateWindowTitle();

        if (IsSettingsValid(this._settings))
        {
            StartPlayback();
            return;
        }

        this._playerService.Stop();
        SetPresetsMenuUnavailable();
        SetSlotStatus(this._slot, "Disconnected");
        UpdateStreamMenuState(this._playerService.State);
    }

    private void HelpMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        new HelpDialog(this._language) { Owner = this }.ShowDialog();
    }

    private void AboutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        new AboutDialog(this._language) { Owner = this }.ShowDialog();
    }

    private void VideoHost_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (this._isFullscreen) ExitFullscreen(); else EnterFullscreen();
    }

    private void VideoHost_OnMouseEnter(object sender, MouseEventArgs e)
    {
        this._isVideoHostHovered = true;
        UpdatePresetsOverlayVisibility();
    }

    private void VideoHost_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!this._isVideoHostHovered)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject originalSource &&
            (IsDescendantOf(originalSource, this.PresetsOverlayBorder) || IsDescendantOf(originalSource, this.NavigationOverlayBorder)))
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            TryGetViewportSlotFromSource(source, out var sourceSlot) &&
            sourceSlot > 0)
        {
            if (sourceSlot != this._hoveredSlot)
            {
                this._hoveredSlot = sourceSlot;
                UpdatePresetsOverlayVisibility();
            }

            return;
        }

        var hoveredSlot = GetViewportSlotFromPoint(e.GetPosition(this.VideoHost));
        if (hoveredSlot > 0)
        {
            if (hoveredSlot != this._hoveredSlot)
            {
                this._hoveredSlot = hoveredSlot;
                UpdatePresetsOverlayVisibility();
            }

            return;
        }

        var pointerInsidePlaybackGrid = this.PlaybackGrid.ActualWidth > 0 &&
                                        this.PlaybackGrid.ActualHeight > 0 &&
                                        new Rect(0, 0, this.PlaybackGrid.ActualWidth, this.PlaybackGrid.ActualHeight)
                                            .Contains(e.GetPosition(this.PlaybackGrid));

        if (!pointerInsidePlaybackGrid && this._hoveredSlot != 0)
        {
            this._hoveredSlot = 0;
            UpdatePresetsOverlayVisibility();
        }
    }

    private int GetViewportSlotFromPoint(Point point)
    {
        foreach (var viewport in this._viewports.Values)
        {
            if (viewport.SelectionBorder.ActualWidth <= 0 || viewport.SelectionBorder.ActualHeight <= 0)
            {
                continue;
            }

            var transform = viewport.SelectionBorder.TransformToAncestor(this.VideoHost);
            var origin = transform.Transform(new Point(0, 0));
            var bounds = new Rect(origin.X, origin.Y, viewport.SelectionBorder.ActualWidth, viewport.SelectionBorder.ActualHeight);
            if (bounds.Contains(point))
            {
                return viewport.Slot;
            }
        }

        return 0;
    }

    private void VideoHost_OnMouseLeave(object sender, MouseEventArgs e)
    {
        this._isVideoHostHovered = false;
        this._hoveredSlot = 0;
        StopNavigationMove();
        UpdatePresetsOverlayVisibility();
    }

    private void NavigationVolumeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (this._isUpdatingNavigationVolumeSlider)
        {
            return;
        }

        var targetSlot = ResolveNavigationVolumeTargetSlot();
        if (!this._viewports.TryGetValue(targetSlot, out var viewport))
        {
            return;
        }

        var soundLevel = Math.Clamp((int)Math.Round(this.NavigationVolumeSlider.Value), 0, 100);
        if (viewport.Settings.SoundLevel == soundLevel)
        {
            return;
        }

        viewport.Settings.SoundLevel = soundLevel;
        if (targetSlot == this._slot)
        {
            this._settings.SoundLevel = soundLevel;
            this._playerService.SetVolume(soundLevel);
        }
        else if (this._backgroundPlayers.TryGetValue(targetSlot, out var backgroundPlayer))
        {
            backgroundPlayer.SetVolume(soundLevel);
        }

        this._navigationVolumeTargetSlot = targetSlot;
        this._navigationVolumeTargetStreamNumber = RegistryService.GetStreamNumberFromGlobalSettings(this._globalSettings);
        this._navigationVolumeSaveDebounce.Stop();
        this._navigationVolumeSaveDebounce.Start();
    }

    private int ResolveNavigationVolumeTargetSlot()
    {
        var targetSlot = GetPresetTargetSlot();
        if (targetSlot > 0 && this._viewports.ContainsKey(targetSlot))
        {
            return targetSlot;
        }

        if (this._navigationVolumeTargetSlot > 0 && this._viewports.ContainsKey(this._navigationVolumeTargetSlot))
        {
            return this._navigationVolumeTargetSlot;
        }

        return this._slot;
    }

    private void UpdateNavigationVolumeUi(int overlayTargetSlot)
    {
        var targetSlot = overlayTargetSlot > 0 && this._viewports.ContainsKey(overlayTargetSlot)
            ? overlayTargetSlot
            : ResolveNavigationVolumeTargetSlot();
        if (!this._viewports.TryGetValue(targetSlot, out var viewport))
        {
            return;
        }

        var soundLevel = Math.Clamp(viewport.Settings.SoundLevel, 0, 100);
        this._navigationVolumeTargetSlot = targetSlot;
        this._isUpdatingNavigationVolumeSlider = true;
        this.NavigationVolumeSlider.Value = soundLevel;
        this._isUpdatingNavigationVolumeSlider = false;
        RefreshNavigationOverlayControlStates();
    }

    private void RefreshNavigationOverlayControlStates()
    {
        var targetSlot = ResolveNavigationOverlayTargetSlot();
        if (targetSlot <= 0 || !this._viewports.TryGetValue(targetSlot, out var viewport))
        {
            SetNavigationControlsEnabled(false, false, false);
            SetNavigationVolumeControlVisualState(false);
            return;
        }

        var playerState = GetPlayerStateForSlot(targetSlot);
        var isPlaying = playerState == PlayerState.Playing;
        var runtime = GetOrCreateRuntimeCapabilities(targetSlot);

        var hasPanTilt = viewport.Settings.UseOnvif && isPlaying && runtime.PtzCapabilities.HasPanTilt;
        var hasZoom = viewport.Settings.UseOnvif && isPlaying && runtime.PtzCapabilities.HasZoom;
        SetNavigationControlsEnabled(hasPanTilt, hasZoom, isPlaying);

        var hasAudio = runtime.HasAudio ?? false;
        SetNavigationVolumeControlVisualState(isPlaying && hasAudio);
    }

    private void SetNavigationVolumeControlVisualState(bool isEnabled)
    {
        this.NavigationVolumeSlider.IsEnabled = isEnabled;
        this.NavigationVolumeSlider.Opacity = isEnabled ? 1.0 : DisabledControlOpacity;
        this.NavigationVolumeLabelText.Opacity = isEnabled ? 1.0 : DisabledControlOpacity;
    }

    private int ResolveNavigationOverlayTargetSlot()
    {
        var targetSlot = GetPresetTargetSlot();
        if (targetSlot > 0 && this._viewports.ContainsKey(targetSlot))
        {
            return targetSlot;
        }

        return this._slot;
    }

    private void SetNavigationControlsEnabled(bool panTiltEnabled, bool zoomEnabled, bool overlayEnabled)
    {
        var panTilt = overlayEnabled && panTiltEnabled;
        var zoom = overlayEnabled && zoomEnabled;

        this.NavigationLeftButton.IsEnabled = panTilt;
        this.NavigationLeftFastButton.IsEnabled = panTilt;
        this.NavigationRightButton.IsEnabled = panTilt;
        this.NavigationRightFastButton.IsEnabled = panTilt;
        this.NavigationUpButton.IsEnabled = panTilt;
        this.NavigationUpFastButton.IsEnabled = panTilt;
        this.NavigationDownButton.IsEnabled = panTilt;
        this.NavigationDownFastButton.IsEnabled = panTilt;

        this.NavigationZoomInButton.IsEnabled = zoom;
        this.NavigationZoomInFastButton.IsEnabled = zoom;
        this.NavigationZoomOutButton.IsEnabled = zoom;
        this.NavigationZoomOutFastButton.IsEnabled = zoom;
    }

    private void NavigationVolumeSaveDebounce_OnTick(object? sender, EventArgs e)
    {
        this._navigationVolumeSaveDebounce.Stop();
        var targetSlot = this._navigationVolumeTargetSlot;
        if (targetSlot <= 0 || !this._viewports.TryGetValue(targetSlot, out var viewport))
        {
            return;
        }

        this._registryService.SaveSettings(targetSlot, viewport.Settings, this._navigationVolumeTargetStreamNumber);
    }

    private bool TryGetViewportSlotFromSource(DependencyObject? source, out int slot)
    {
        slot = 0;
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { Tag: int taggedSlot } && this._viewports.ContainsKey(taggedSlot))
            {
                slot = taggedSlot;
                return true;
            }

            current = GetParentObject(current);
        }

        return false;
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = GetParentObject(current);
        }

        return false;
    }

    private static DependencyObject? GetParentObject(DependencyObject child)
    {
        return child switch
        {
            Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(child),
            FrameworkContentElement frameworkContentElement => frameworkContentElement.Parent,
            _ => LogicalTreeHelper.GetParent(child)
        };
    }

    private void VideoHost_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopNavigationMove();
    }

    private void NavigationButton_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var targetSlot = GetPresetTargetSlot();
        if (targetSlot != this._slot)
        {
            SelectCameraSlot(targetSlot, restartPlayback: false);
        }

        if (sender is not FrameworkElement { Tag: string direction })
        {
            return;
        }

        var isFastDirection = direction.EndsWith("Fast", StringComparison.Ordinal);
        var normalizedDirection = isFastDirection
            ? direction[..^"Fast".Length]
            : direction;
        var (panStep, zoomStep) = ResolveAdaptiveStepSizes(targetSlot, isFastDirection);
        var commandDirection = ResolveCommandDirection(normalizedDirection);
        var (adjustedPanStep, adjustedZoomStep, settleDelayMs) = ResolveDirectionAdjustedSteps(targetSlot, commandDirection, panStep, zoomStep);
        panStep = adjustedPanStep;
        zoomStep = adjustedZoomStep;
        this._navigationStepSize = panStep;
        this._zoomStepSize = zoomStep;

        var (pan, tilt, zoom) = normalizedDirection switch
        {
            "Left" => (-this._navigationStepSize, 0.0, 0.0),
            "Right" => (this._navigationStepSize, 0.0, 0.0),
            "Up" => (0.0, this._navigationStepSize, 0.0),
            "Down" => (0.0, -this._navigationStepSize, 0.0),
            "ZoomOut" => (0.0, 0.0, -this._zoomStepSize),
            "ZoomIn" => (0.0, 0.0, this._zoomStepSize),
            _ => (0.0, 0.0, 0.0)
        };

        if (pan == 0 && tilt == 0 && zoom == 0)
        {
            return;
        }

        TrackMoveDirection(targetSlot, commandDirection);
        StartNavigationMove(targetSlot, pan, tilt, zoom, settleDelayMs);
        e.Handled = true;
    }

    private static string ResolveCommandDirection(string normalizedDirection)
    {
        return normalizedDirection switch
        {
            "Left" => "PanNegative",
            "Right" => "PanPositive",
            "Up" => "TiltPositive",
            "Down" => "TiltNegative",
            "ZoomIn" => "ZoomPositive",
            "ZoomOut" => "ZoomNegative",
            _ => string.Empty
        };
    }

    private (double PanStep, double ZoomStep, int SettleDelayMs) ResolveDirectionAdjustedSteps(int slot, string direction, double panStep, double zoomStep)
    {
        if (slot <= 0 || string.IsNullOrWhiteSpace(direction) || !this._runtimeCapabilityCache.TryGetValue(slot, out var runtime))
        {
            return (panStep, zoomStep, 0);
        }

        var previousDirection = runtime.LastMoveDirection;
        if (string.IsNullOrWhiteSpace(previousDirection) || previousDirection == direction)
        {
            return (panStep, zoomStep, 0);
        }

        var isSameAxis = (previousDirection.StartsWith("Pan", StringComparison.Ordinal) && direction.StartsWith("Pan", StringComparison.Ordinal))
                      || (previousDirection.StartsWith("Tilt", StringComparison.Ordinal) && direction.StartsWith("Tilt", StringComparison.Ordinal))
                      || (previousDirection.StartsWith("Zoom", StringComparison.Ordinal) && direction.StartsWith("Zoom", StringComparison.Ordinal));

        if (!isSameAxis)
        {
            return (panStep, zoomStep, 0);
        }

        if (direction.StartsWith("Zoom", StringComparison.Ordinal))
        {
            return (panStep, zoomStep * DirectionChangeBoostZoom, NavigationDirectionSettleDelayMs);
        }

        return (panStep * DirectionChangeBoostPanTilt, zoomStep, NavigationDirectionSettleDelayMs);
    }

    private void TrackMoveDirection(int slot, string direction)
    {
        if (slot <= 0 || string.IsNullOrWhiteSpace(direction) || !this._runtimeCapabilityCache.TryGetValue(slot, out var runtime))
        {
            return;
        }

        runtime.LastMoveDirection = direction;
        runtime.LastMoveDirectionUtc = DateTime.UtcNow;
    }

    private (double PanTiltStep, double ZoomStep) ResolveAdaptiveStepSizes(int targetSlot, bool isFast)
    {
        if (targetSlot <= 0 || !this._viewports.TryGetValue(targetSlot, out var viewport) || !viewport.Settings.UseOnvif)
        {
            return isFast
                ? (FixedNavigationStepSizeFast, FixedZoomStepSizeFast)
                : (FixedNavigationStepSize, FixedZoomStepSize);
        }

        var runtime = GetOrCreateRuntimeCapabilities(targetSlot);
        var profile = runtime.AdaptivePtzProfile;
        var zoomNormalized = ResolveCurrentZoomNormalized(runtime);
        var adaptiveScale = ResolveAdaptiveScale(profile, zoomNormalized);

        var basePanStep = isFast ? profile.FastPanTiltMinStep : profile.NormalPanTiltMinStep;
        var baseZoomStep = isFast ? profile.FastZoomMinStep : profile.NormalZoomMinStep;

        basePanStep *= Math.Clamp(runtime.PanTiltRuntimeGain, 0.5, 8.0);
        baseZoomStep *= Math.Clamp(runtime.ZoomRuntimeGain, 0.5, 8.0);

        var panStep = Math.Clamp(basePanStep * adaptiveScale, FixedNavigationStepSize / 10d, 1.0d);
        var zoomStep = Math.Clamp(baseZoomStep * adaptiveScale, FixedZoomStepSize / 10d, 1.0d);
        return (panStep, zoomStep);
    }

    private static double ResolveAdaptiveScale(AdaptivePtzProfile profile, double? zoomNormalized)
    {
        var z = Math.Clamp(zoomNormalized ?? 0d, 0d, 1d);
        var wide = profile.MaxScaleAtWide;
        var tele = profile.MaxScaleAtTele;
        return wide + ((tele - wide) * z);
    }

    private static double? ResolveCurrentZoomNormalized(StreamRuntimeCapabilities runtime)
    {
        return runtime.LastKnownZoomNormalized
               ?? runtime.PtzCapabilities.CurrentZoomNormalized;
    }

    private void NavigationButton_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopNavigationMove();
        e.Handled = true;
    }

    private void StartNavigationMove(int activeSlot, double panDelta, double tiltDelta, double zoomDelta, int settleDelayMs)
    {
        StopNavigationMove();

        this._navigationMoveCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = this._navigationMoveCancellationTokenSource.Token;

        _ = Task.Run(async () =>
        {
            if (settleDelayMs > 0)
            {
                try
                {
                    await this._onvifService.StopMoveAsync(this._settings, stopPanTilt: true, stopZoom: true, cancellationToken);
                    await Task.Delay(settleDelayMs, cancellationToken);
                }
                catch
                {
                }
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var loopStart = DateTime.UtcNow;
                try
                {
                    if (zoomDelta != 0)
                    {
                        if (zoomDelta > 0)
                        {
                            await this._onvifService.ZoomInAsync(this._settings, zoomDelta, cancellationToken);
                        }
                        else
                        {
                            await this._onvifService.ZoomOutAsync(this._settings, -zoomDelta, cancellationToken);
                        }

                        TrackZoomEstimate(activeSlot, zoomDelta);
                    }
                    else
                    {
                        await this._onvifService.MoveRelativeAsync(this._settings, panDelta, tiltDelta, cancellationToken);
                    }

                    if (this._runtimeCapabilityCache.TryGetValue(activeSlot, out var successRuntime))
                    {
                        if (zoomDelta != 0)
                        {
                            successRuntime.ZoomConsecutiveFailures = 0;
                            successRuntime.ZoomRuntimeGain = Math.Max(1.0, successRuntime.ZoomRuntimeGain * 0.98);
                        }
                        else
                        {
                            successRuntime.PanTiltConsecutiveFailures = 0;
                            successRuntime.PanTiltRuntimeGain = Math.Max(1.0, successRuntime.PanTiltRuntimeGain * 0.98);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    if (this._runtimeCapabilityCache.TryGetValue(activeSlot, out var runtime))
                    {
                        if (zoomDelta != 0)
                        {
                            runtime.ZoomConsecutiveFailures++;
                            runtime.ZoomRuntimeGain = Math.Clamp(runtime.ZoomRuntimeGain * (runtime.ZoomConsecutiveFailures >= 2 ? 1.35 : 1.15), 1.0, 8.0);
                        }
                        else
                        {
                            runtime.PanTiltConsecutiveFailures++;
                            runtime.PanTiltRuntimeGain = Math.Clamp(runtime.PanTiltRuntimeGain * (runtime.PanTiltConsecutiveFailures >= 2 ? 1.35 : 1.15), 1.0, 8.0);
                        }

                        UpdateOverlayTextForSlot(activeSlot);
                    }

                    break;
                }

                var elapsed = DateTime.UtcNow - loopStart;
                var delay = TimeSpan.FromMilliseconds(NavigationRepeatIntervalMs) - elapsed;
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }, cancellationToken);
    }

    private void TrackZoomEstimate(int slot, double zoomDelta)
    {
        if (!this._runtimeCapabilityCache.TryGetValue(slot, out var runtime) || !runtime.PtzCapabilities.HasZoom)
        {
            return;
        }

        var baseStep = runtime.PtzCapabilities.ZoomMinStep ?? FixedZoomStepSize;
        var deltaNormalized = zoomDelta / Math.Max(baseStep, FixedZoomStepSize / 10d);
        var current = runtime.LastKnownZoomNormalized ?? runtime.PtzCapabilities.CurrentZoomNormalized ?? 0d;
        var next = Math.Clamp(current + (deltaNormalized * 0.005d), 0d, 1d);
        runtime.LastKnownZoomNormalized = next;
    }

    private void StopNavigationMove()
    {
        var hadActiveMove = this._navigationMoveCancellationTokenSource is not null;
        this._navigationMoveCancellationTokenSource?.Cancel();
        this._navigationMoveCancellationTokenSource?.Dispose();
        this._navigationMoveCancellationTokenSource = null;

        if (hadActiveMove && this._settings.UseOnvif)
        {
            _ = SendPtzStopAsync();
        }
    }

    private async Task SendPtzStopAsync()
    {
        try
        {
            await this._onvifService.StopMoveAsync(this._settings, stopPanTilt: true, stopZoom: true, CancellationToken.None);
        }
        catch
        {
        }
    }

    private async void PresetsOverlayList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.PresetsOverlayList.SelectedItem is not OnvifPreset preset || string.IsNullOrWhiteSpace(preset.Token))
        {
            return;
        }

        this.PresetsOverlayList.SelectedItem = null;
        await GotoPresetAsync(preset.Token);
    }

    private void EnterFullscreen()
    {
        this._previousState = WindowState;
        this._previousStyle = WindowStyle;
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        this.MenuBar.Visibility = Visibility.Collapsed;
        this._isFullscreen = true;
    }

    private void ExitFullscreen()
    {
        WindowStyle = this._previousStyle;
        WindowState = this._previousState;
        this.MenuBar.Visibility = Visibility.Visible;
        this._isFullscreen = false;
    }

    private void MainWindow_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && this._isFullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        var isHu = IsHungarianShortcutsActive();
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (TryGetCameraSlotFromCtrlKey(key, out var targetSlot))
        {
            SelectCameraSlotAndPromptForSettings(targetSlot, restartPlayback: false);
            e.Handled = true;
            return;
        }

        switch (key)
        {
            case Key.P:
                GlobalSettingsMenuItem_OnClick(this.GlobalSettingsMenuItem, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.A when isHu:
            case Key.A when !isHu:
                CameraSettingsMenuItem_OnClick(this.CameraSettingsMenuItem, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.I when isHu:
            case Key.R when !isHu:
                StartStreamMenuItem_OnClick(this.StartStreamMenuItem, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.L when isHu:
            case Key.O when !isHu:
                StopStreamMenuItem_OnClick(this.StopStreamMenuItem, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.M when isHu:
            case Key.Q when !isHu:
                HighQualityMenuItem_OnClick(this.HighQualityMenuItem, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.O when isHu:
            case Key.L when !isHu:
                LowBandwidthMenuItem_OnClick(this.LowBandwidthMenuItem, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.H when isHu:
            case Key.S when !isHu:
                this.StreamSoundMenuItem.IsChecked = !this.StreamSoundMenuItem.IsChecked;
                StreamSoundMenuItem_OnClick(this.StreamSoundMenuItem, new RoutedEventArgs());
                UpdateViewToolbarSelection();
                e.Handled = true;
                return;
            case Key.F when isHu:
            case Key.T when !isHu:
                this.TopmostMenuItem.IsChecked = !this.TopmostMenuItem.IsChecked;
                TopmostMenuItem_OnClick(this.TopmostMenuItem, new RoutedEventArgs());
                UpdateViewToolbarSelection();
                e.Handled = true;
                return;
            case Key.U:
                this.FpsOverlayMenuItem.IsChecked = !this.FpsOverlayMenuItem.IsChecked;
                FpsOverlayMenuItem_OnClick(this.FpsOverlayMenuItem, new RoutedEventArgs());
                UpdateViewToolbarSelection();
                e.Handled = true;
                return;
            case Key.S when isHu:
            case Key.H when !isHu:
                HelpMenuItem_OnClick(this.HelpMenuItem, new RoutedEventArgs());
                e.Handled = true;
                return;
        }
    }

    private static bool TryGetCameraSlotFromCtrlKey(Key key, out int slot)
    {
        slot = key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            _ => 0
        };

        return slot != 0;
    }

    private void ShutdownPlayer()
    {
        this._isShuttingDown = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StopNavigationMove();

        if (this._hwndSource is not null)
        {
            this._hwndSource.RemoveHook(WndProc);
            this._hwndSource = null;
        }

        StopPlayback();
        SaveWindowMetrics();
        this._registryService.ReleaseInstanceSlot(this._ownedSlot);
    }

    internal static void ImportCameraSettingsFromFile(RegistryService registryService, string filePath, bool clearExisting, string language)
    {
        var yaml = File.ReadAllText(filePath);
        ImportCameraSettingsFromYaml(registryService, yaml, clearExisting, language);
    }

    internal static void ImportCameraSettingsFromYaml(RegistryService registryService, string yaml, bool clearExisting, string language)
    {
        var settingsBySlot = DeserializeCameraSettingsFromYaml(yaml);
        var hasImportPasswordIssue = false;
        if (clearExisting)
        {
            registryService.ResetAllCameraSettings();

            foreach (var slotPair in settingsBySlot)
            {
                foreach (var streamPair in slotPair.Value)
                {
                    if (streamPair.Value.ProvidedKeys.Contains("password"))
                    {
                        streamPair.Value.Settings.Password = registryService.NormalizeImportedPassword(streamPair.Value.Settings.Password, out var hasPasswordError);
                        hasImportPasswordIssue |= hasPasswordError;
                    }

                    registryService.SaveSettings(slotPair.Key, streamPair.Value.Settings, streamPair.Key);
                }
            }

            var clearExistingMaintenance = registryService.EnsurePasswordEncryptionState();
            if (hasImportPasswordIssue || clearExistingMaintenance.HasUndecodablePasswords)
            {
                throw new InvalidOperationException(BuildPasswordReentryMessage(language));
            }

            return;
        }

        foreach (var slotPair in settingsBySlot)
        {
            foreach (var streamPair in slotPair.Value)
            {
                var merged = registryService.LoadSettings(slotPair.Key, streamPair.Key);
                if (streamPair.Value.ProvidedKeys.Contains("password"))
                {
                    streamPair.Value.Settings.Password = registryService.NormalizeImportedPassword(streamPair.Value.Settings.Password, out var hasPasswordError);
                    hasImportPasswordIssue |= hasPasswordError;
                }

                ApplyProvidedSettings(merged, streamPair.Value.Settings, streamPair.Value.ProvidedKeys);
                registryService.SaveSettings(slotPair.Key, merged, streamPair.Key);
            }
        }

        var maintenance = registryService.EnsurePasswordEncryptionState();
        if (hasImportPasswordIssue || maintenance.HasUndecodablePasswords)
        {
            throw new InvalidOperationException(BuildPasswordReentryMessage(language));
        }
    }

    internal static string BuildPasswordReentryMessage(string language)
    {
        return string.Format(
            LocalizationService.Translate(language, "PasswordReentryRequired"),
            RegistryService.PasswordNoKeyMarker);
    }

    private static string SerializeCameraSettingsToYaml(IReadOnlyDictionary<int, Dictionary<int, AppSettings>> settingsBySlot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("cameras:");

        foreach (var pair in settingsBySlot.OrderBy(p => p.Key))
        {
            var slot = pair.Key;
            builder.AppendLine($"  - slot: {slot}");
            builder.AppendLine("    streams:");

            foreach (var streamPair in pair.Value.OrderBy(p => p.Key))
            {
                var stream = streamPair.Key;
                var settings = streamPair.Value;
                builder.AppendLine($"      - stream: {stream}");
                builder.AppendLine($"        cameraName: {QuoteYaml(settings.CameraName)}");
                builder.AppendLine($"        url: {QuoteYaml(settings.Url)}");
                builder.AppendLine($"        username: {QuoteYaml(settings.Username)}");
                builder.AppendLine($"        password: {QuoteYaml(settings.Password)}");
                builder.AppendLine($"        useOnvif: {ToYamlBool(settings.UseOnvif)}");
                builder.AppendLine($"        onvifDeviceServiceUrl: {QuoteYaml(settings.OnvifDeviceServiceUrl)}");
                builder.AppendLine($"        onvifProfileToken: {QuoteYaml(settings.OnvifProfileToken)}");
                if (settings.OnvifXSize.HasValue)
                {
                    builder.AppendLine($"        onvifXSize: {settings.OnvifXSize.Value}");
                }

                if (settings.OnvifYSize.HasValue)
                {
                    builder.AppendLine($"        onvifYSize: {settings.OnvifYSize.Value}");
                }
                builder.AppendLine($"        autoResolveRtspFromOnvif: {ToYamlBool(settings.AutoResolveRtspFromOnvif)}");
                builder.AppendLine($"        reconnectDelaySec: {settings.ReconnectDelaySec}");
                builder.AppendLine($"        connectionRetries: {settings.ConnectionRetries}");
                builder.AppendLine($"        networkTimeoutSec: {settings.NetworkTimeoutSec}");
                builder.AppendLine($"        maxFps: {settings.MaxFps}");
                builder.AppendLine($"        showFpsOverlay: {ToYamlBool(settings.ShowFpsOverlay)}");
                builder.AppendLine($"        fpsOverlayPosition: {QuoteYaml(settings.FpsOverlayPosition)}");
                builder.AppendLine($"        soundEnabled: {ToYamlBool(settings.SoundEnabled)}");
                builder.AppendLine($"        soundLevel: {settings.SoundLevel}");
                builder.AppendLine($"        aspectRatioMode: {QuoteYaml(settings.AspectRatioMode)}");
                builder.AppendLine($"        windowLeft: {ToYamlNullableInt(settings.WindowLeft)}");
                builder.AppendLine($"        windowTop: {ToYamlNullableInt(settings.WindowTop)}");
                builder.AppendLine($"        windowWidth: {ToYamlNullableInt(settings.WindowWidth)}");
                builder.AppendLine($"        windowHeight: {ToYamlNullableInt(settings.WindowHeight)}");
                builder.AppendLine($"        windowMaximized: {ToYamlBool(settings.WindowMaximized)}");
            }
        }

        return builder.ToString();
    }

    private sealed class ImportedSettings
    {
        public AppSettings Settings { get; } = new();
        public HashSet<string> ProvidedKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<int, IReadOnlyDictionary<int, ImportedSettings>> DeserializeCameraSettingsFromYaml(string yaml)
    {
        var settingsBySlot = new Dictionary<int, Dictionary<int, ImportedSettings>>();
        var lines = yaml.Split('\n');
        var currentSlot = 0;
        var currentStream = RegistryService.PrimaryStreamNumber;
        ImportedSettings? currentSettings = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (string.Equals(trimmed, "cameras:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.StartsWith("- slot:", StringComparison.OrdinalIgnoreCase))
            {
                var slotText = trimmed[(trimmed.IndexOf(':') + 1)..].Trim();
                if (!int.TryParse(slotText, out var slot) || slot <= 0)
                {
                    currentSlot = 0;
                    currentSettings = null;
                    continue;
                }

                currentSlot = slot;
                currentStream = RegistryService.PrimaryStreamNumber;
                currentSettings = null;
                if (!settingsBySlot.ContainsKey(currentSlot))
                {
                    settingsBySlot[currentSlot] = new Dictionary<int, ImportedSettings>();
                }
                continue;
            }

            if (currentSlot <= 0)
            {
                continue;
            }

            if (trimmed.StartsWith("- stream:", StringComparison.OrdinalIgnoreCase))
            {
                var streamText = trimmed[(trimmed.IndexOf(':') + 1)..].Trim();
                if (!int.TryParse(streamText, out var parsedStream) || parsedStream <= 0)
                {
                    currentSettings = null;
                    continue;
                }

                currentStream = RegistryService.NormalizeStreamNumber(parsedStream);
                currentSettings = new ImportedSettings();
                settingsBySlot[currentSlot][currentStream] = currentSettings;
                continue;
            }

            var separatorIndex = trimmed.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = trimmed[..separatorIndex].Trim();
            var value = trimmed[(separatorIndex + 1)..].Trim();

            if (string.Equals(key, "streams", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(key, "stream", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, out var parsedStream) || parsedStream <= 0)
                {
                    currentSettings = null;
                    continue;
                }

                currentStream = RegistryService.NormalizeStreamNumber(parsedStream);
                currentSettings = new ImportedSettings();
                settingsBySlot[currentSlot][currentStream] = currentSettings;
                continue;
            }

            if (currentSettings is null)
            {
                currentStream = RegistryService.PrimaryStreamNumber;
                currentSettings = new ImportedSettings();
                settingsBySlot[currentSlot][currentStream] = currentSettings;
            }

            if (ApplyYamlSetting(currentSettings.Settings, key, value))
            {
                currentSettings.ProvidedKeys.Add(key);
            }
        }

        return settingsBySlot.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<int, ImportedSettings>)pair.Value);
    }

    private static bool ApplyYamlSetting(AppSettings settings, string key, string rawValue)
    {
        switch (key)
        {
            case "cameraName":
                settings.CameraName = UnquoteYaml(rawValue);
                return true;
            case "url":
                settings.Url = UnquoteYaml(rawValue);
                return true;
            case "username":
                settings.Username = UnquoteYaml(rawValue);
                return true;
            case "password":
                settings.Password = UnquoteYaml(rawValue);
                return true;
            case "useOnvif":
                settings.UseOnvif = ParseYamlBool(rawValue, settings.UseOnvif);
                return true;
            case "onvifDeviceServiceUrl":
                settings.OnvifDeviceServiceUrl = UnquoteYaml(rawValue);
                return true;
            case "onvifProfileToken":
                settings.OnvifProfileToken = UnquoteYaml(rawValue);
                return true;
            case "onvifXSize":
                {
                    var parsed = ParseYamlNullableInt(rawValue);
                    if (!parsed.HasValue)
                    {
                        return false;
                    }

                    settings.OnvifXSize = parsed;
                    return true;
                }
            case "onvifYSize":
                {
                    var parsed = ParseYamlNullableInt(rawValue);
                    if (!parsed.HasValue)
                    {
                        return false;
                    }

                    settings.OnvifYSize = parsed;
                    return true;
                }
            case "autoResolveRtspFromOnvif":
                settings.AutoResolveRtspFromOnvif = ParseYamlBool(rawValue, settings.AutoResolveRtspFromOnvif);
                return true;
            case "reconnectDelaySec":
                settings.ReconnectDelaySec = ParseYamlInt(rawValue, settings.ReconnectDelaySec);
                return true;
            case "connectionRetries":
                settings.ConnectionRetries = ParseYamlInt(rawValue, settings.ConnectionRetries);
                return true;
            case "networkTimeoutSec":
                settings.NetworkTimeoutSec = ParseYamlInt(rawValue, settings.NetworkTimeoutSec);
                return true;
            case "maxFps":
                settings.MaxFps = ParseYamlInt(rawValue, settings.MaxFps);
                return true;
            case "showFpsOverlay":
                settings.ShowFpsOverlay = ParseYamlBool(rawValue, settings.ShowFpsOverlay);
                return true;
            case "fpsOverlayPosition":
                settings.FpsOverlayPosition = UnquoteYaml(rawValue);
                return true;
            case "soundEnabled":
                settings.SoundEnabled = ParseYamlBool(rawValue, settings.SoundEnabled);
                return true;
            case "soundLevel":
                settings.SoundLevel = Math.Clamp(ParseYamlInt(rawValue, settings.SoundLevel), 0, 100);
                return true;
            case "aspectRatioMode":
                settings.AspectRatioMode = UnquoteYaml(rawValue);
                return true;
            case "windowLeft":
                settings.WindowLeft = ParseYamlNullableInt(rawValue);
                return true;
            case "windowTop":
                settings.WindowTop = ParseYamlNullableInt(rawValue);
                return true;
            case "windowWidth":
                settings.WindowWidth = ParseYamlNullableInt(rawValue);
                return true;
            case "windowHeight":
                settings.WindowHeight = ParseYamlNullableInt(rawValue);
                return true;
            case "windowMaximized":
                settings.WindowMaximized = ParseYamlBool(rawValue, settings.WindowMaximized);
                return true;
            default:
                return false;
        }
    }

    private static void ApplyProvidedSettings(AppSettings target, AppSettings source, IReadOnlySet<string> providedKeys)
    {
        if (providedKeys.Contains("cameraName")) target.CameraName = source.CameraName;
        if (providedKeys.Contains("url")) target.Url = source.Url;
        if (providedKeys.Contains("username")) target.Username = source.Username;
        if (providedKeys.Contains("password")) target.Password = source.Password;
        if (providedKeys.Contains("useOnvif")) target.UseOnvif = source.UseOnvif;
        if (providedKeys.Contains("onvifDeviceServiceUrl")) target.OnvifDeviceServiceUrl = source.OnvifDeviceServiceUrl;
        if (providedKeys.Contains("onvifProfileToken")) target.OnvifProfileToken = source.OnvifProfileToken;
        if (providedKeys.Contains("onvifXSize")) target.OnvifXSize = source.OnvifXSize;
        if (providedKeys.Contains("onvifYSize")) target.OnvifYSize = source.OnvifYSize;
        if (providedKeys.Contains("autoResolveRtspFromOnvif")) target.AutoResolveRtspFromOnvif = source.AutoResolveRtspFromOnvif;
        if (providedKeys.Contains("reconnectDelaySec")) target.ReconnectDelaySec = source.ReconnectDelaySec;
        if (providedKeys.Contains("connectionRetries")) target.ConnectionRetries = source.ConnectionRetries;
        if (providedKeys.Contains("networkTimeoutSec")) target.NetworkTimeoutSec = source.NetworkTimeoutSec;
        if (providedKeys.Contains("maxFps")) target.MaxFps = source.MaxFps;
        if (providedKeys.Contains("showFpsOverlay")) target.ShowFpsOverlay = source.ShowFpsOverlay;
        if (providedKeys.Contains("fpsOverlayPosition")) target.FpsOverlayPosition = source.FpsOverlayPosition;
        if (providedKeys.Contains("soundEnabled")) target.SoundEnabled = source.SoundEnabled;
        if (providedKeys.Contains("soundLevel")) target.SoundLevel = source.SoundLevel;
        if (providedKeys.Contains("aspectRatioMode")) target.AspectRatioMode = source.AspectRatioMode;
        if (providedKeys.Contains("windowLeft")) target.WindowLeft = source.WindowLeft;
        if (providedKeys.Contains("windowTop")) target.WindowTop = source.WindowTop;
        if (providedKeys.Contains("windowWidth")) target.WindowWidth = source.WindowWidth;
        if (providedKeys.Contains("windowHeight")) target.WindowHeight = source.WindowHeight;
        if (providedKeys.Contains("windowMaximized")) target.WindowMaximized = source.WindowMaximized;
    }

    private static string QuoteYaml(string? value)
    {
        var text = value ?? string.Empty;
        return $"\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static string UnquoteYaml(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\");
    }

    private static string ToYamlBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static bool ParseYamlBool(string value, bool defaultValue)
    {
        return bool.TryParse(value.Trim(), out var parsed) ? parsed : defaultValue;
    }

    private static int ParseYamlInt(string value, int defaultValue)
    {
        return int.TryParse(value.Trim(), out var parsed) ? parsed : defaultValue;
    }

    private static string ToYamlNullableInt(int? value)
    {
        return value.HasValue ? value.Value.ToString() : "null";
    }

    private static int? ParseYamlNullableInt(string value)
    {
        var trimmed = value.Trim();
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(trimmed, out var parsed) ? parsed : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
