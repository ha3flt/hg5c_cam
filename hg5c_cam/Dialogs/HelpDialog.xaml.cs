using hg5c_cam.Services;
using System.Windows;

namespace hg5c_cam.Dialogs;

public partial class HelpDialog : Window
{
    public HelpDialog(string language)
    {
        InitializeComponent();
        Title = LocalizationService.Translate(language, "Help").Replace("_", string.Empty);
        this.HelpBodyText.Text = LocalizationService.Translate(language, "HelpBody");
    }
}
