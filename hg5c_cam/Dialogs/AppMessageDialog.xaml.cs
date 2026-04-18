using hg5c_cam.Services;
using System.Windows;

namespace hg5c_cam.Dialogs;

public partial class AppMessageDialog : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;
    private readonly MessageBoxButton _buttons;

    private AppMessageDialog(
        Window? owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image,
        string language)
    {
        InitializeComponent();

        this._buttons = buttons;
        Owner = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;

        Title = caption;
        this.MessageTextBlock.Text = message;
        this.IconTextBlock.Text = GetIconGlyph(image);
        ConfigureButtons(language);
    }

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image,
        string language)
    {
        var dialog = new AppMessageDialog(owner, message, caption, buttons, image, language);
        dialog.ShowDialog();
        return dialog._result;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (this._result == MessageBoxResult.None)
        {
            this._result = this._buttons switch
            {
                MessageBoxButton.YesNo => MessageBoxResult.No,
                MessageBoxButton.YesNoCancel or MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
                _ => MessageBoxResult.OK
            };
        }

        base.OnClosing(e);
    }

    private void ConfigureButtons(string language)
    {
        this.PrimaryButton.Click += (_, _) => SetResultAndClose(GetPrimaryResult());
        this.SecondaryButton.Click += (_, _) => SetResultAndClose(GetSecondaryResult());
        this.TertiaryButton.Click += (_, _) => SetResultAndClose(GetTertiaryResult());

        switch (this._buttons)
        {
            case MessageBoxButton.OK:
                this.PrimaryButton.Content = LocalizationService.Translate(language, "Ok");
                this.PrimaryButton.IsDefault = true;
                this.PrimaryButton.IsCancel = true;
                break;
            case MessageBoxButton.OKCancel:
                this.PrimaryButton.Content = LocalizationService.Translate(language, "Ok");
                this.SecondaryButton.Content = LocalizationService.Translate(language, "Cancel");
                this.SecondaryButton.Visibility = Visibility.Visible;
                this.PrimaryButton.IsDefault = true;
                this.SecondaryButton.IsCancel = true;
                break;
            case MessageBoxButton.YesNo:
                this.PrimaryButton.Content = LocalizationService.Translate(language, "Yes");
                this.SecondaryButton.Content = LocalizationService.Translate(language, "No");
                this.SecondaryButton.Visibility = Visibility.Visible;
                this.PrimaryButton.IsDefault = true;
                this.SecondaryButton.IsCancel = true;
                break;
            case MessageBoxButton.YesNoCancel:
                this.PrimaryButton.Content = LocalizationService.Translate(language, "Yes");
                this.SecondaryButton.Content = LocalizationService.Translate(language, "No");
                this.TertiaryButton.Content = LocalizationService.Translate(language, "Cancel");
                this.SecondaryButton.Visibility = Visibility.Visible;
                this.TertiaryButton.Visibility = Visibility.Visible;
                this.PrimaryButton.IsDefault = true;
                this.TertiaryButton.IsCancel = true;
                break;
        }
    }

    private MessageBoxResult GetPrimaryResult()
    {
        return this._buttons switch
        {
            MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => MessageBoxResult.Yes,
            _ => MessageBoxResult.OK
        };
    }

    private MessageBoxResult GetSecondaryResult()
    {
        return this._buttons switch
        {
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => MessageBoxResult.No,
            _ => MessageBoxResult.None
        };
    }

    private MessageBoxResult GetTertiaryResult()
    {
        return this._buttons switch
        {
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.None
        };
    }

    private void SetResultAndClose(MessageBoxResult result)
    {
        this._result = result;
        Close();
    }

    private static string GetIconGlyph(MessageBoxImage image)
    {
        return image switch
        {
            MessageBoxImage.Error => "⛔",
            MessageBoxImage.Warning => "⚠",
            MessageBoxImage.Information => "ℹ",
            MessageBoxImage.Question => "?",
            _ => string.Empty
        };
    }
}
