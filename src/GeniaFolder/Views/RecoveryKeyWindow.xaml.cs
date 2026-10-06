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
            $"Это общий бумажный Master Recovery Key для всей установки GeniaFolder. " +
            $"Он создаётся один раз при защите «{folderName}» и сможет восстановить " +
            "любую папку, которую вы позже привяжете к этой установке. " +
            "Храните его вне компьютера.";

        RecoveryKeyText.Text = recoveryKey;
        FingerprintText.Text =
            $"Fingerprint Master Recovery: {fingerprint}";

        SavedBox.Checked += (_, _) => ContinueButton.IsEnabled = true;
        SavedBox.Unchecked += (_, _) => ContinueButton.IsEnabled = false;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (SavedBox.IsChecked == true)
            DialogResult = true;
    }
}
