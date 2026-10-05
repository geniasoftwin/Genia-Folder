using System.Windows;
using GeniaFolder.Services;

namespace GeniaFolder.Views;

public partial class ProtectionSetupWindow : Window
{
    public string Password => PasswordBox.Password;

    public ProtectionSetupWindow(string folderName)
    {
        InitializeComponent();

        InfoText.Text =
            $"Для «{folderName}» будет создан случайный 256-битный Folder Encryption Key (FEK). " +
            "Он будет отдельно защищён вашим паролем и Master Recovery Key. " +
            "После следующего шага Recovery Key нужно записать на бумагу.";

        PasswordBox.PasswordChanged += (_, _) => UpdateState();
        ConfirmBox.PasswordChanged += (_, _) => UpdateState();
        AcknowledgeBox.Checked += (_, _) => UpdateState();
        AcknowledgeBox.Unchecked += (_, _) => UpdateState();

        Loaded += (_, _) => PasswordBox.Focus();
    }

    public void ClearSecrets()
    {
        PasswordBox.Clear();
        ConfirmBox.Clear();
    }

    private void UpdateState()
    {
        ErrorText.Text = string.Empty;

        CreateButton.IsEnabled =
            PasswordBox.Password.Length >= ProtectionService.MinimumPasswordLength &&
            ConfirmBox.Password.Length > 0 &&
            AcknowledgeBox.IsChecked == true;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Password.Length < ProtectionService.MinimumPasswordLength)
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

        if (AcknowledgeBox.IsChecked != true)
        {
            ErrorText.Text = "Подтвердите предупреждение о текущем этапе защиты.";
            return;
        }

        DialogResult = true;
    }
}
