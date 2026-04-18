using hg5c_cam.Services;
using System.Reflection;
using System.Windows;

namespace hg5c_cam.Dialogs;

public partial class AboutDialog : Window
{
    public AboutDialog(string language)
    {
        InitializeComponent();
        Title = LocalizationService.Translate(language, "About").Replace("_", string.Empty);
        this.AboutBodyText.Text = LocalizationService.Translate(language, "AboutBody");
        this.FrameworkText.Text = LocalizationService.Translate(language, "Framework");
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var cleanInformationalVersion = informationalVersion?.Split('+')[0];
        var version = !string.IsNullOrWhiteSpace(cleanInformationalVersion)
            ? cleanInformationalVersion
            : Assembly.GetExecutingAssembly().GetName().Version?.ToString()
              ?? "?.?.?";
        this.VersionText.Text = $"{LocalizationService.Translate(language, "Version")}: {version}";
    }
}
