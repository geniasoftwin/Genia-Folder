using System.Windows;

namespace GeniaFolder.Views;

public partial class RecoveryKeyWindow : Window
{
    public RecoveryKeyWindow(
        string folderName,
        string recoveryKey,
        string fingerprint)
    {
        InitializeComponent();

        DescriptionText.Text =
            $"Аварийный ключ для «{folderName}». Он сможет восстановить доступ, " +
            "если пароль папки будет забыт.";

        RecoveryKeyText.Text = recoveryKey;
        FingerprintText.Text = $"Fingerprint профиля: {fingerprint}";

        SavedBox.Checked += (_, _) => ContinueButton.IsEnabled = true;
        SavedBox.Unchecked += (_, _) => ContinueButton.IsEnabled = false;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (SavedBox.IsChecked == true)
            DialogResult = true;
    }
}
