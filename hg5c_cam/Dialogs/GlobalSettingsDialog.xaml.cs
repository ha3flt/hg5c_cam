using hg5c_cam.Models;
using hg5c_cam.Services;
using System.Windows;

namespace hg5c_cam.Dialogs;

public partial class GlobalSettingsDialog : Window
{
    private readonly string _language;
    private readonly Func<IReadOnlyList<string>> _audioDeviceProvider;
    private readonly GlobalSettings _original;

    public GlobalSettings ResultSettings { get; private set; }
    public string SelectedLanguage { get; private set; }

    public GlobalSettingsDialog(GlobalSettings settings, string language, Func<IReadOnlyList<string>> audioDeviceProvider)
    {
        InitializeComponent();
        this._language = LocalizationService.NormalizeLanguage(language);
        this._audioDeviceProvider = audioDeviceProvider;
        this._original = Clone(settings);
        ResultSettings = Clone(settings);
        SelectedLanguage = this._language;

        this.EnableSoundCheckBox.IsChecked = settings.EnableSound;
        this.SoundLevelSlider.Value = Math.Clamp(settings.SoundLevel, 0, 100);
        this.SplitPlaybackCameraCountTextBox.Text = Math.Clamp(settings.SplitPlaybackCameraCount, 1, RegistryService.MaxInstanceSlots).ToString();
        this.AlwaysMaximizedPlaybackCheckBox.IsChecked = settings.AlwaysMaximizedPlayback;
        this.TopmostMainWindowCheckBox.IsChecked = settings.TopmostMainWindow;
        this.ForceSoftwareDecodingCheckBox.IsChecked = settings.ForceSoftwareDecoding;

        this.RefreshButton.Click += (_, _) => LoadAudioDevices();
        this.OkButton.Click += (_, _) => SaveAndClose();

        ApplyLocalization();
        LoadAudioDevices();
    }

    private void ApplyLocalization()
    {
        Title = LocalizationService.Translate(this._language, "GlobalSettings");
        this.LanguageLabel.Text = LocalizationService.Translate(this._language, "Language");
        this.LanguageComboBox.ItemsSource = LocalizationService.GetLanguageOptions();
        this.LanguageComboBox.DisplayMemberPath = nameof(LocalizationService.LanguageOption.Label);
        this.LanguageComboBox.SelectedValuePath = nameof(LocalizationService.LanguageOption.Value);
        this.LanguageComboBox.SelectedValue = this._language;
        this.EnableSoundCheckBox.Content = LocalizationService.Translate(this._language, "EnableSound");
        this.AudioDeviceLabel.Text = LocalizationService.Translate(this._language, "StreamSound").Replace("_", string.Empty);
        this.RefreshButton.Content = LocalizationService.Translate(this._language, "Refresh");
        this.SoundLevelLabel.Text = LocalizationService.Translate(this._language, "SoundLevel");
        this.SplitPlaybackCameraCountLabel.Text = LocalizationService.Translate(this._language, "SplitPlaybackCameraCount");
        this.AlwaysMaximizedPlaybackCheckBox.Content = LocalizationService.Translate(this._language, "AlwaysMaximizedPlayback");
        this.TopmostMainWindowCheckBox.Content = LocalizationService.Translate(this._language, "TopmostMainWindow");
        this.ForceSoftwareDecodingCheckBox.Content = LocalizationService.Translate(this._language, "ForceSoftwareDecoding");
        this.OkButton.Content = LocalizationService.Translate(this._language, "Ok");
        this.CancelButton.Content = LocalizationService.Translate(this._language, "Cancel");
    }

    private void LoadAudioDevices()
    {
        var defaultDeviceLabel = LocalizationService.Translate(this._language, "DefaultAudioDevice");
        var selectedBefore = this.AudioDevicesListBox.SelectedItem as string ?? this._original.AudioOutputDeviceName;
        var devices = this._audioDeviceProvider()
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        devices.Insert(0, defaultDeviceLabel);

        this.AudioDevicesListBox.ItemsSource = devices;

        if (!string.IsNullOrWhiteSpace(selectedBefore) && devices.Any(item => string.Equals(item, selectedBefore, StringComparison.OrdinalIgnoreCase)))
        {
            this.AudioDevicesListBox.SelectedItem = devices.First(item => string.Equals(item, selectedBefore, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            this.AudioDevicesListBox.SelectedIndex = 0;
        }
    }

    private void SaveAndClose()
    {
        var defaultDeviceLabel = LocalizationService.Translate(this._language, "DefaultAudioDevice");
        var selectedDevice = this.AudioDevicesListBox.SelectedItem as string ?? string.Empty;
        ResultSettings = new GlobalSettings
        {
            EnableSound = this.EnableSoundCheckBox.IsChecked == true,
            AudioOutputDeviceName = string.Equals(selectedDevice, defaultDeviceLabel, StringComparison.Ordinal) ? string.Empty : selectedDevice,
            SoundLevel = (int)Math.Round(this.SoundLevelSlider.Value),
            SplitPlaybackCameraCount = ParseSplitPlaybackCameraCount(),
            AlwaysMaximizedPlayback = this.AlwaysMaximizedPlaybackCheckBox.IsChecked == true,
            TopmostMainWindow = this.TopmostMainWindowCheckBox.IsChecked == true,
            ForceSoftwareDecoding = this.ForceSoftwareDecodingCheckBox.IsChecked == true,
            LastUsedCameraSlot = this._original.LastUsedCameraSlot,
            UseSecondStream = this._original.UseSecondStream == 1 ? 1 : 0
        };

        SelectedLanguage = LocalizationService.NormalizeLanguage(this.LanguageComboBox.SelectedValue as string ?? this._language);

        DialogResult = true;
    }

    private static GlobalSettings Clone(GlobalSettings settings)
    {
        return new GlobalSettings
        {
            EnableSound = settings.EnableSound,
            AudioOutputDeviceName = settings.AudioOutputDeviceName,
            SoundLevel = settings.SoundLevel,
            SplitPlaybackCameraCount = Math.Clamp(settings.SplitPlaybackCameraCount, 1, RegistryService.MaxInstanceSlots),
            AlwaysMaximizedPlayback = settings.AlwaysMaximizedPlayback,
            TopmostMainWindow = settings.TopmostMainWindow,
            ForceSoftwareDecoding = settings.ForceSoftwareDecoding,
            LastUsedCameraSlot = settings.LastUsedCameraSlot,
            UseSecondStream = settings.UseSecondStream == 1 ? 1 : 0
        };
    }

    private int ParseSplitPlaybackCameraCount()
    {
        if (!int.TryParse(this.SplitPlaybackCameraCountTextBox.Text, out var value))
        {
            return Math.Clamp(this._original.SplitPlaybackCameraCount, 1, RegistryService.MaxInstanceSlots);
        }

        return Math.Clamp(value, 1, RegistryService.MaxInstanceSlots);
    }

    private void SplitPlaybackQuickButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tagText } || !int.TryParse(tagText, out var value))
        {
            return;
        }

        this.SplitPlaybackCameraCountTextBox.Text = value.ToString();
    }
}
