using hg5c_cam.Models;
using hg5c_cam.Services;
using System.Windows;

namespace hg5c_cam.Dialogs;

public partial class SetupDialog : Window
{
    private readonly OnvifService _onvifService = new();
    private static readonly string[] AspectRatioValues = [
        AppSettings.AutoAspectRatio,
        "1:1",
        "5:4",
        "4:3",
        "3:2",
        "16:10",
        "16:9",
        "2:1"
    ];

    private static readonly string[] FpsOverlayPositionValues = [
        AppSettings.FpsOverlayPositionTopLeft,
        AppSettings.FpsOverlayPositionTopRight,
        AppSettings.FpsOverlayPositionBottomLeft,
        AppSettings.FpsOverlayPositionBottomRight
    ];

    private readonly AppSettings _original;
    private readonly int _slot;
    private readonly string _language;
    private bool _isUpdatingOnvifProfileToken;
    private bool _hasOnvifProfileTokenChanged;
    private bool _isLoadingOnvifProfiles;
    private bool _isResolvingOnvif;
    private bool _isOnvifProfileTokenReady;
    private bool _isUpdatingPasswordText;
    private int? _onvifXSize;
    private int? _onvifYSize;
    private string _pictureSizeLabelText = string.Empty;
    private string _initialOnvifProfileTokenText = string.Empty;

    private sealed class OptionItem
    {
        public required string Value { get; init; }
        public required string Label { get; init; }
    }

    public AppSettings ResultSettings { get; private set; }

    public SetupDialog(AppSettings settings, IReadOnlyList<string> history, string language, int slot)
    {
        InitializeComponent();
        this._slot = Math.Max(1, slot);
        this._original = Clone(settings);
        this._language = LocalizationService.NormalizeLanguage(language);
        this._original.CameraName = GetEffectiveCameraName(this._original.CameraName);
        ResultSettings = Clone(this._original);
        this.CameraNameTextBox.Text = this._original.CameraName;
        this.UrlComboBox.ItemsSource = history;
        this.UrlComboBox.Text = string.IsNullOrWhiteSpace(settings.Url) ? "rtsp://" : settings.Url;
        this.UsernameTextBox.Text = settings.Username;
        this.PasswordBox.Password = settings.Password;
        this.UseOnvifCheckBox.IsChecked = settings.UseOnvif;
        this.AutoResolveOnvifCheckBox.IsChecked = settings.AutoResolveRtspFromOnvif;
        this.OnvifDeviceServiceUrlTextBox.Text = settings.OnvifDeviceServiceUrl;
        SetOnvifProfileTokenText(settings.OnvifProfileToken, setAsInitial: true);
        this.MaxFpsTextBox.Text = settings.MaxFps.ToString();
        this.ReconnectDelayTextBox.Text = settings.ReconnectDelaySec.ToString();
        this.RetriesTextBox.Text = settings.ConnectionRetries.ToString();
        this.NetworkTimeoutTextBox.Text = settings.NetworkTimeoutSec.ToString();
        this.SoundCheckBox.IsChecked = settings.SoundEnabled;
        this.FpsOverlayCheckBox.IsChecked = settings.ShowFpsOverlay;
        this.WindowMaximizedCheckBox.IsChecked = settings.WindowMaximized;
        this._onvifXSize = settings.OnvifXSize;
        this._onvifYSize = settings.OnvifYSize;
        ApplyLocalization(settings);
        UpdatePictureSizeText();
        this.CameraNameTextBox.TextChanged += (_, _) => RefreshState();
        this.UrlComboBox.SelectionChanged += (_, _) => FillFieldsFromSelectedUrl();
        this.UrlComboBox.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent, new System.Windows.Controls.TextChangedEventHandler((_, _) => RefreshState()));
        this.UsernameTextBox.TextChanged += (_, _) =>
        {
            ClearPictureSize();
            RefreshState();
        };
        this.PasswordBox.PasswordChanged += PasswordBox_OnPasswordChanged;
        this.UseOnvifCheckBox.Checked += (_, _) => RefreshState();
        this.UseOnvifCheckBox.Unchecked += (_, _) => RefreshState();
        this.AutoResolveOnvifCheckBox.Checked += (_, _) => RefreshState();
        this.AutoResolveOnvifCheckBox.Unchecked += (_, _) => RefreshState();
        this.OnvifDeviceServiceUrlTextBox.TextChanged += (_, _) =>
        {
            ClearPictureSize();
            RefreshState();
        };
        this.OnvifDeviceServiceUrlTextBox.LostKeyboardFocus += (_, _) => NormalizeOnvifDeviceServiceUrlText();
        this.OnvifProfileTokenComboBox.DropDownOpened += OnvifProfileTokenComboBox_OnDropDownOpened;
        this.OnvifProfileTokenComboBox.SelectionChanged += OnvifProfileTokenComboBox_OnSelectionChanged;
        this.OnvifProfileTokenComboBox.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent, new System.Windows.Controls.TextChangedEventHandler(OnOnvifProfileTokenTextChanged));
        this.OnvifProfileTokenComboBox.LostKeyboardFocus += OnvifProfileTokenComboBox_OnLostKeyboardFocus;
        this.AspectRatioComboBox.SelectionChanged += (_, _) => RefreshState();
        this.MaxFpsTextBox.TextChanged += (_, _) => RefreshState();
        this.ReconnectDelayTextBox.TextChanged += (_, _) => RefreshState();
        this.RetriesTextBox.TextChanged += (_, _) => RefreshState();
        this.NetworkTimeoutTextBox.TextChanged += (_, _) => RefreshState();
        this.SoundCheckBox.Checked += (_, _) => RefreshState();
        this.SoundCheckBox.Unchecked += (_, _) => RefreshState();
        this.FpsOverlayCheckBox.Checked += (_, _) => RefreshState();
        this.FpsOverlayCheckBox.Unchecked += (_, _) => RefreshState();
        this.FpsOverlayPositionComboBox.SelectionChanged += (_, _) => RefreshState();
        this.WindowMaximizedCheckBox.Checked += (_, _) => RefreshState();
        this.WindowMaximizedCheckBox.Unchecked += (_, _) => RefreshState();
        this.OkButton.Click += (_, _) => SaveAndClose();
        this.ResolveOnvifButton.Click += ResolveOnvifButton_OnClick;
        Loaded += SetupDialog_OnLoaded;
        RefreshState();
    }

    private void SetupDialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        this._initialOnvifProfileTokenText = this.OnvifProfileTokenComboBox.Text.Trim();
        this._hasOnvifProfileTokenChanged = false;
        this._isOnvifProfileTokenReady = true;
    }

    private void ApplyLocalization(AppSettings settings)
    {
        var language = this._language;
        Title = LocalizationService.Translate(language, "CameraSettings");
        this.CameraNameLabel.Text = LocalizationService.Translate(language, "CameraName");
        this.UseOnvifCheckBox.Content = LocalizationService.Translate(language, "UseOnvif");
        this.AutoResolveOnvifCheckBox.Content = LocalizationService.Translate(language, "AutoResolveOnvif");
        this.OnvifDeviceServiceUrlLabel.Text = LocalizationService.Translate(language, "OnvifDeviceServiceUrl");
        this.OnvifProfileTokenLabel.Text = LocalizationService.Translate(language, "OnvifProfileToken");
        this.UsernameLabel.Text = LocalizationService.Translate(language, "Username");
        this.PasswordLabel.Text = LocalizationService.Translate(language, "Password");
        this.ResolveOnvifButton.Content = LocalizationService.Translate(language, "ResolveOnvif");
        this.RtspUrlLabel.Text = LocalizationService.Translate(language, "RtspUrl");
        this._pictureSizeLabelText = $"{LocalizationService.Translate(language, "PictureSize")}:";
        UpdatePictureSizeText();
        this.AspectRatioLabel.Text = LocalizationService.Translate(language, "AspectRatio");
        this.SoundCheckBox.Content = LocalizationService.Translate(language, "StreamSoundSimple");
        this.MaxFpsLabel.Text = LocalizationService.Translate(language, "MaxFps");
        this.ConnectionDelayLabel.Text = LocalizationService.Translate(language, "ConnectionDelay");
        this.ConnectionRetriesLabel.Text = LocalizationService.Translate(language, "ConnectionRetries");
        this.NetworkTimeoutLabel.Text = LocalizationService.Translate(language, "NetworkTimeout");
        this.FpsOverlayCheckBox.Content = LocalizationService.Translate(language, "FpsOverlaySimple");
        this.FpsOverlayPositionLabel.Text = LocalizationService.Translate(language, "FpsOverlayPosition");
        this.WindowMaximizedCheckBox.Content = LocalizationService.Translate(language, "Maximized");
        this.OkButton.Content = LocalizationService.Translate(language, "Ok");
        this.CancelButton.Content = LocalizationService.Translate(language, "Cancel");

        var selectedAspect = settings.AspectRatioMode;
        this.AspectRatioComboBox.ItemsSource = AspectRatioValues
            .Select(value => new OptionItem
            {
                Value = value,
                Label = LocalizationService.LocalizeAspectRatioMode(language, value)
            })
            .ToList();
        this.AspectRatioComboBox.DisplayMemberPath = nameof(OptionItem.Label);
        this.AspectRatioComboBox.SelectedValuePath = nameof(OptionItem.Value);
        this.AspectRatioComboBox.SelectedValue = AspectRatioValues.Contains(selectedAspect) ? selectedAspect : AppSettings.AutoAspectRatio;

        var selectedOverlayPosition = settings.FpsOverlayPosition;
        this.FpsOverlayPositionComboBox.ItemsSource = FpsOverlayPositionValues
            .Select(value => new OptionItem
            {
                Value = value,
                Label = LocalizationService.LocalizeFpsOverlayPosition(language, value)
            })
            .ToList();
        this.FpsOverlayPositionComboBox.DisplayMemberPath = nameof(OptionItem.Label);
        this.FpsOverlayPositionComboBox.SelectedValuePath = nameof(OptionItem.Value);
        this.FpsOverlayPositionComboBox.SelectedValue = FpsOverlayPositionValues.Contains(selectedOverlayPosition) ? selectedOverlayPosition : AppSettings.FpsOverlayPositionBottomLeft;
    }

    private string GetEffectiveCameraName(string? configuredName)
    {
        var trimmed = configuredName?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(trimmed)
            ? LocalizationService.GetDefaultCameraName(this._language, this._slot)
            : trimmed;
    }

    private async void ResolveOnvifButton_OnClick(object sender, RoutedEventArgs e)
    {
        await ResolveOnvifAsync().ConfigureAwait(true);
    }

    private async Task ResolveOnvifAsync()
    {
        if (this._isResolvingOnvif)
        {
            return;
        }

        var currentSettings = BuildSettingsForOnvifResolution();
        if (!IsOnvifResolvable(currentSettings))
        {
            AppMessageDialog.Show(this,
                LocalizationService.Translate(this._language, "PleaseProvideOnvif"),
                LocalizationService.Translate(this._language, "CameraSettings"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                this._language);
            return;
        }

        this.UrlComboBox.Text = string.Empty;
        ClearPictureSize();
        this.ResolveOnvifStatusText.Text = string.Empty;
        this.ResolveOnvifStatusText.Visibility = Visibility.Collapsed;

        this.ResolveOnvifButton.IsEnabled = false;
        var originalButtonText = this.ResolveOnvifButton.Content;
        this.ResolveOnvifButton.Content = LocalizationService.Translate(this._language, "Resolving");
        this._isResolvingOnvif = true;

        try
        {
            var streamInfo = await this._onvifService.ResolveRtspStreamAsync(currentSettings);
            var rebasedRtspUrl = RebaseRtspHostToOnvifDeviceService(streamInfo.RtspUri, streamInfo.DeviceServiceUrl);
            this.UrlComboBox.Text = rebasedRtspUrl;
            this.OnvifDeviceServiceUrlTextBox.Text = streamInfo.DeviceServiceUrl;
            SetOnvifProfileTokenText(streamInfo.ProfileToken, setAsInitial: false);
            this._onvifXSize = streamInfo.StreamWidth;
            this._onvifYSize = streamInfo.StreamHeight;
            UpdatePictureSizeText();
            this.UseOnvifCheckBox.IsChecked = true;
            this.AutoResolveOnvifCheckBox.IsChecked = false;
            this.ResolveOnvifStatusText.Text = string.Format(LocalizationService.Translate(this._language, "ResolvedRtspPort"), streamInfo.RtspPort);
            this.ResolveOnvifStatusText.Visibility = Visibility.Visible;
            await LoadOnvifProfilesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            this.ResolveOnvifStatusText.Text = string.Empty;
            this.ResolveOnvifStatusText.Visibility = Visibility.Collapsed;
            AppMessageDialog.Show(this,
                string.Format(LocalizationService.Translate(this._language, "OnvifResolutionFailedWithError"), ex.Message),
                "ONVIF",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                this._language);
        }
        finally
        {
            this._isResolvingOnvif = false;
            this.ResolveOnvifButton.Content = originalButtonText;
            this.ResolveOnvifButton.IsEnabled = true;
            RefreshState();
        }
    }

    private AppSettings BuildSettingsForOnvifResolution()
    {
        var settings = Clone(this._original);
        settings.Url = this.UrlComboBox.Text?.Trim() ?? string.Empty;
        settings.Username = this.UsernameTextBox.Text.Trim();
        settings.Password = this.PasswordBox.Password;
        settings.OnvifDeviceServiceUrl = this.OnvifDeviceServiceUrlTextBox.Text.Trim();
        settings.OnvifProfileToken = this.OnvifProfileTokenComboBox.Text.Trim();
        settings.UseOnvif = true;
        settings.AutoResolveRtspFromOnvif = this.AutoResolveOnvifCheckBox.IsChecked == true;
        return settings;
    }

    private async void OnvifProfileTokenComboBox_OnDropDownOpened(object? sender, EventArgs e)
    {
        await LoadOnvifProfilesAsync().ConfigureAwait(true);
    }

    private void OnvifProfileTokenComboBox_OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!this._isOnvifProfileTokenReady)
        {
            RefreshState();
            return;
        }

        if (this.OnvifProfileTokenComboBox.SelectedItem is not string selected)
        {
            RefreshState();
            return;
        }

        SetOnvifProfileTokenText(selected, setAsInitial: false);
        this.UrlComboBox.Text = string.Empty;
        ClearPictureSize();
        this._hasOnvifProfileTokenChanged = true;
        RefreshState();
    }

    private void OnOnvifProfileTokenTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (this._isUpdatingOnvifProfileToken || !this._isOnvifProfileTokenReady)
        {
            return;
        }

        this.UrlComboBox.Text = string.Empty;
        ClearPictureSize();
        this._hasOnvifProfileTokenChanged = true;
        RefreshState();
    }

    private async void OnvifProfileTokenComboBox_OnLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (this._isUpdatingOnvifProfileToken || !this._isOnvifProfileTokenReady || e.NewFocus == this.ResolveOnvifButton)
        {
            return;
        }

        if (this._hasOnvifProfileTokenChanged)
        {
            await ResolveOnvifAsync().ConfigureAwait(true);
        }

        this._hasOnvifProfileTokenChanged = false;
        RefreshState();
    }

    private async Task LoadOnvifProfilesAsync()
    {
        if (this._isLoadingOnvifProfiles)
        {
            return;
        }

        var settings = BuildSettingsForOnvifResolution();
        if (!IsOnvifResolvable(settings))
        {
            return;
        }

        this._isLoadingOnvifProfiles = true;
        try
        {
            var profiles = await this._onvifService.GetProfileTokensAsync(settings).ConfigureAwait(true);
            var currentText = this.OnvifProfileTokenComboBox.Text.Trim();
            var profileList = profiles.ToList();

            if (!string.IsNullOrWhiteSpace(currentText) && !profileList.Contains(currentText, StringComparer.Ordinal))
            {
                profileList.Insert(0, currentText);
            }

            this._isUpdatingOnvifProfileToken = true;
            this.OnvifProfileTokenComboBox.ItemsSource = profileList;
        }
        catch
        {
        }
        finally
        {
            this._isUpdatingOnvifProfileToken = false;
            this._isLoadingOnvifProfiles = false;
        }
    }

    private void SetOnvifProfileTokenText(string value, bool setAsInitial)
    {
        var text = value?.Trim() ?? string.Empty;
        this._isUpdatingOnvifProfileToken = true;
        this.OnvifProfileTokenComboBox.Text = text;
        this._isUpdatingOnvifProfileToken = false;

        if (setAsInitial)
        {
            this._initialOnvifProfileTokenText = text;
            this._hasOnvifProfileTokenChanged = false;
        }
    }

    private static bool IsOnvifResolvable(AppSettings settings)
    {
        return Uri.TryCreate(settings.OnvifDeviceServiceUrl, UriKind.Absolute, out var endpoint) &&
               (string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase));
    }

    private static string RebaseRtspHostToOnvifDeviceService(string rtspUrl, string onvifDeviceServiceUrl)
    {
        if (!Uri.TryCreate(rtspUrl, UriKind.Absolute, out var rtspUri))
        {
            return rtspUrl;
        }

        if (!Uri.TryCreate(onvifDeviceServiceUrl, UriKind.Absolute, out var onvifUri) || string.IsNullOrWhiteSpace(onvifUri.Host))
        {
            return rtspUrl;
        }

        var rebased = new UriBuilder(rtspUri)
        {
            Host = onvifUri.Host
        };

        return rebased.Uri.ToString();
    }

    private void FillFieldsFromSelectedUrl()
    {
        if (!Uri.TryCreate(this.UrlComboBox.Text, UriKind.Absolute, out var uri)) return;
        if (!string.IsNullOrWhiteSpace(uri.UserInfo) && uri.UserInfo.Contains(':', StringComparison.Ordinal))
        {
            var parts = uri.UserInfo.Split(':', 2);
            this.UsernameTextBox.Text = Uri.UnescapeDataString(parts[0]);
            this.PasswordBox.Password = Uri.UnescapeDataString(parts[1]);
        }
    }

    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (this._isUpdatingPasswordText)
        {
            return;
        }

        var password = this.PasswordBox.Password;
        if (RegistryService.ContainsForbiddenPasswordBraceSequence(password))
        {
            this._isUpdatingPasswordText = true;
            var sanitized = password
                .Replace("{{", "{", StringComparison.Ordinal)
                .Replace("}}", "}", StringComparison.Ordinal);
            this.PasswordBox.Password = sanitized;
            this._isUpdatingPasswordText = false;
        }

        ClearPictureSize();
        RefreshState();
    }

    private void SaveAndClose()
    {
        NormalizeOnvifDeviceServiceUrlText();
        if (RegistryService.ContainsForbiddenPasswordBraceSequence(this.PasswordBox.Password))
        {
            AppMessageDialog.Show(
                this,
                LocalizationService.Translate(this._language, "PasswordBraceSequenceInvalid"),
                LocalizationService.Translate(this._language, "CameraSettings"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                this._language);
            return;
        }

        if (!TryBuildSettings(out var updated))
        {
            return;
        }

        ResultSettings = updated;
        DialogResult = true;
    }

    private void NormalizeOnvifDeviceServiceUrlText()
    {
        var normalized = NormalizeOnvifDeviceServiceUrl(this.OnvifDeviceServiceUrlTextBox.Text);
        if (!string.Equals(normalized, this.OnvifDeviceServiceUrlTextBox.Text, StringComparison.Ordinal))
        {
            this.OnvifDeviceServiceUrlTextBox.Text = normalized;
        }
    }

    private static string NormalizeOnvifDeviceServiceUrl(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return text;
        }

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            return text;
        }

        return new UriBuilder(uri)
        {
            Path = "/onvif/device_service"
        }.Uri.AbsoluteUri;
    }

    private void ClearPictureSize()
    {
        this._onvifXSize = null;
        this._onvifYSize = null;
        UpdatePictureSizeText();
    }

    private void UpdatePictureSizeText()
    {
        if (FindName("PictureSizeText") is not System.Windows.Controls.TextBlock pictureSizeText)
        {
            return;
        }

        if (this._onvifXSize is > 0 && this._onvifYSize is > 0)
        {
            pictureSizeText.Text = $"{this._pictureSizeLabelText} {this._onvifXSize.Value} x {this._onvifYSize.Value}";
        }
        else
        {
            pictureSizeText.Text = this._pictureSizeLabelText;
        }
    }

    private void RefreshState()
    {
        this.OkButton.IsEnabled = TryBuildSettings(out var current) && !IsSame(current, this._original);
    }

    private bool TryBuildSettings(out AppSettings settings)
    {
        settings = Clone(this._original);
        settings.CameraName = this.CameraNameTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(settings.CameraName))
        {
            return false;
        }
        settings.Url = this.UrlComboBox.Text?.Trim() ?? string.Empty;
        settings.Username = this.UsernameTextBox.Text.Trim();
        settings.Password = this.PasswordBox.Password;
        settings.UseOnvif = this.UseOnvifCheckBox.IsChecked == true;
        settings.AutoResolveRtspFromOnvif = this.AutoResolveOnvifCheckBox.IsChecked == true;
        settings.OnvifDeviceServiceUrl = this.OnvifDeviceServiceUrlTextBox.Text.Trim();
        settings.OnvifProfileToken = this.OnvifProfileTokenComboBox.Text.Trim();
        if (settings.UseOnvif)
        {
            settings.Url = RebaseRtspHostToOnvifDeviceService(settings.Url, settings.OnvifDeviceServiceUrl);
        }
        settings.SoundEnabled = this.SoundCheckBox.IsChecked == true;
        settings.ShowFpsOverlay = this.FpsOverlayCheckBox.IsChecked == true;
        settings.FpsOverlayPosition = this.FpsOverlayPositionComboBox.SelectedValue as string ?? AppSettings.FpsOverlayPositionBottomLeft;
        settings.AspectRatioMode = this.AspectRatioComboBox.SelectedValue as string ?? AppSettings.AutoAspectRatio;
        settings.WindowMaximized = this.WindowMaximizedCheckBox.IsChecked == true;
        settings.OnvifXSize = this._onvifXSize;
        settings.OnvifYSize = this._onvifYSize;
        if (!int.TryParse(this.MaxFpsTextBox.Text, out var maxFps)) return false;
        if (!int.TryParse(this.ReconnectDelayTextBox.Text, out var delay) || delay < 0) return false;
        if (!int.TryParse(this.RetriesTextBox.Text, out var retries) || retries < 0) return false;
        if (!int.TryParse(this.NetworkTimeoutTextBox.Text, out var networkTimeoutSec) || networkTimeoutSec < 1) return false;
        settings.MaxFps = maxFps;
        settings.ReconnectDelaySec = delay;
        settings.ConnectionRetries = retries;
        settings.NetworkTimeoutSec = networkTimeoutSec;
        var hasValidRtsp = IsUrlValid(settings.Url, settings.Username, settings.Password);
        var hasValidOnvif = !settings.UseOnvif || IsOnvifConfigValid(settings);
        return hasValidOnvif && (hasValidRtsp || settings.UseOnvif);
    }

    private static bool IsOnvifConfigValid(AppSettings settings)
    {
        if (!settings.UseOnvif)
        {
            return true;
        }

        return Uri.TryCreate(settings.OnvifDeviceServiceUrl, UriKind.Absolute, out var endpoint) &&
               (string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase) || string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUrlValid(string url, string username, string password)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(uri.Host)) return false;
        if (!string.IsNullOrWhiteSpace(uri.UserInfo) && uri.UserInfo.Contains(':', StringComparison.Ordinal)) return true;
        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
    }

    private static AppSettings Clone(AppSettings settings)
    {
        return new AppSettings
        {
            CameraName = settings.CameraName,
            Url = settings.Url,
            Username = settings.Username,
            Password = settings.Password,
            UseOnvif = settings.UseOnvif,
            OnvifDeviceServiceUrl = settings.OnvifDeviceServiceUrl,
            OnvifProfileToken = settings.OnvifProfileToken,
            AutoResolveRtspFromOnvif = settings.AutoResolveRtspFromOnvif,
            ReconnectDelaySec = settings.ReconnectDelaySec,
            ConnectionRetries = settings.ConnectionRetries,
            NetworkTimeoutSec = settings.NetworkTimeoutSec,
            MaxFps = settings.MaxFps,
            ShowFpsOverlay = settings.ShowFpsOverlay,
            FpsOverlayPosition = settings.FpsOverlayPosition,
            SoundEnabled = settings.SoundEnabled,
            AspectRatioMode = settings.AspectRatioMode,
            OnvifXSize = settings.OnvifXSize,
            OnvifYSize = settings.OnvifYSize,
            WindowLeft = settings.WindowLeft,
            WindowTop = settings.WindowTop,
            WindowWidth = settings.WindowWidth,
            WindowHeight = settings.WindowHeight,
            WindowMaximized = settings.WindowMaximized
        };
    }

    private static bool IsSame(AppSettings left, AppSettings right)
    {
        return left.CameraName == right.CameraName && left.Url == right.Url && left.Username == right.Username && left.Password == right.Password && left.UseOnvif == right.UseOnvif && left.OnvifDeviceServiceUrl == right.OnvifDeviceServiceUrl && left.OnvifProfileToken == right.OnvifProfileToken && left.AutoResolveRtspFromOnvif == right.AutoResolveRtspFromOnvif && left.ReconnectDelaySec == right.ReconnectDelaySec && left.ConnectionRetries == right.ConnectionRetries && left.NetworkTimeoutSec == right.NetworkTimeoutSec && left.MaxFps == right.MaxFps && left.ShowFpsOverlay == right.ShowFpsOverlay && left.FpsOverlayPosition == right.FpsOverlayPosition && left.SoundEnabled == right.SoundEnabled && left.AspectRatioMode == right.AspectRatioMode && left.OnvifXSize == right.OnvifXSize && left.OnvifYSize == right.OnvifYSize && left.WindowMaximized == right.WindowMaximized;
    }
}
