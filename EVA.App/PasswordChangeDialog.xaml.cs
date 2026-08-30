using System.Windows;

namespace EVA.App;

public partial class PasswordChangeDialog : Window
{
    public PasswordChangeDialog()
    {
        InitializeComponent();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var current = CurrentPasswordBox.Password;
        var newPassword = NewPasswordBox.Password;
        var confirmation = ConfirmPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(current))
        {
            System.Windows.MessageBox.Show("Enter the current password.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            System.Windows.MessageBox.Show("The new password must be at least 8 characters.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(newPassword, confirmation, StringComparison.Ordinal))
        {
            System.Windows.MessageBox.Show("The new password and confirmation do not match.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (AcknowledgeWarningCheck.IsChecked != true)
        {
            System.Windows.MessageBox.Show("You must acknowledge the password-change warning before continuing.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var storedPassword = PasswordStore.GetPassword(PasswordStore.PasswordReference);
        if (!string.Equals(storedPassword, current, StringComparison.Ordinal))
        {
            System.Windows.MessageBox.Show("The current password is incorrect.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PasswordStore.SavePassword(newPassword);
        var config = App.ConfigStore.LoadAsync().GetAwaiter().GetResult();
        config.PasswordReference = PasswordStore.PasswordReference;
        App.ConfigStore.SaveAsync(config).GetAwaiter().GetResult();
        System.Windows.MessageBox.Show("Password updated. The next backup will start a new archive chain.", "EVA", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }
}
