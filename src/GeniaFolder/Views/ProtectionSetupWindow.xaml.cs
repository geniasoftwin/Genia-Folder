using System.Windows;
using GeniaFolder.Services;

namespace GeniaFolder.Views;

public partial class ProtectionSetupWindow : Window
{
    private readonly bool _requiresMasterRecoveryKey;

    public string Password => PasswordBox.Password;
    public string MasterRecoveryKey => MasterRecoveryKeyBox.Password;

    public ProtectionSetupWindow(
        string folderName,
        bool requiresMasterRecoveryKey,
        string masterRecoveryFingerprint)
    {
        InitializeComponent();

        _requiresMasterRecoveryKey = requiresMasterRecoveryKey;

        if (requiresMasterRecoveryKey)
        {
            InfoText.Text =
                $"Для «{folderName}» будет создан новый случайный 256-битный FEK. " +
                "Он будет защищён отдельным паролем этой папки и уже существующим общим " +
                "Master Recovery Key установки GeniaFolder.";

            MasterRecoveryPanel.Visibility = Visibility.Visible;
            MasterFingerprintText.Text =
                string.IsNullOrWhiteSpace(masterRecoveryFingerprint)
                    ? "Введите общий бумажный Master Recovery Key."
                    : $"Ожидаемый fingerprint Master Recovery: {masterRecoveryFingerprint}";
        }
        else
        {
            InfoText.Text =
                $"Для «{folderName}» будет создан случайный 256-битный FEK. " +
                "Это первая защищённая папка: GeniaFolder также создаст один общий " +
                "Master Recovery Key для всей установки и покажет его на следующем шаге.";
        }

        PasswordBox.PasswordChanged += (_, _) => UpdateState();
        ConfirmBox.PasswordChanged += (_, _) => UpdateState();
        MasterRecoveryKeyBox.PasswordChanged += (_, _) => UpdateState();
        AcknowledgeBox.Checked += (_, _) => UpdateState();
        AcknowledgeBox.Unchecked += (_, _) => UpdateState();

        Loaded += (_, _) => PasswordBox.Focus();
    }

    public void ClearSecrets()
    {
        PasswordBox.Clear();
        ConfirmBox.Clear();
        MasterRecoveryKeyBox.Clear();
    }

    private void UpdateState()
    {
        ErrorText.Text = string.Empty;

        CreateButton.IsEnabled =
            PasswordBox.Password.Length >=
                ProtectionService.MinimumPasswordLength &&
            ConfirmBox.Password.Length > 0 &&
            (!_requiresMasterRecoveryKey ||
             MasterRecoveryKeyBox.Password.Length > 0) &&
            AcknowledgeBox.IsChecked == true;
    }

    private void Create_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (PasswordBox.Password.Length <
            ProtectionService.MinimumPasswordLength)
        {
            ErrorText.Text =
                $"Пароль должен содержать не менее {ProtectionService.MinimumPasswordLength} символов.";
            return;
        }

        if (!string.Equals(
            PasswordBox.Password,
            ConfirmBox.Password,
            StringComparison.Ordinal))
        {
            ErrorText.Text = "Пароли не совпадают.";
            ConfirmBox.Focus();
            ConfirmBox.SelectAll();
            return;
        }

        if (_requiresMasterRecoveryKey &&
            string.IsNullOrWhiteSpace(
                MasterRecoveryKeyBox.Password))
        {
            ErrorText.Text =
                "Введите общий бумажный Master Recovery Key.";
            MasterRecoveryKeyBox.Focus();
            return;
        }

        if (AcknowledgeBox.IsChecked != true)
        {
            ErrorText.Text =
                "Подтвердите предупреждение о текущем этапе защиты.";
            return;
        }

        DialogResult = true;
    }
}
