using System.Windows;

namespace GeniaFolder.Views;

public partial class RecoveryKeyWindow : Window
{
    public RecoveryKeyWindow(
        string recoveryKey,
        string fingerprint)
    {
        InitializeComponent();

        DescriptionText.Text =
            "Это главный бумажный Master Recovery Key этой установки GeniaFolder. " +
            "Он создаётся один раз, отдельно от папок, и является общим аварийным " +
            "ключом для всех защищённых папок. Храните его вне компьютера.";

        RecoveryKeyText.Text = recoveryKey;
        FingerprintText.Text =
            $"Fingerprint Master Recovery: {fingerprint}";

        SavedBox.Checked +=
            (_, _) => ContinueButton.IsEnabled = true;
        SavedBox.Unchecked +=
            (_, _) => ContinueButton.IsEnabled = false;
    }

    public RecoveryKeyWindow(
        string folderName,
        string recoveryKey,
        string fingerprint)
        : this(recoveryKey, fingerprint)
    {
        // Compatibility overload for older call sites during migration.
    }

    private void Continue_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SavedBox.IsChecked == true)
            DialogResult = true;
    }
}
