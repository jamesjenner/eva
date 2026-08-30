using System.Windows;
using System.Windows.Controls;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;
using WpfToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace EVA.App;

public partial class PasswordChangeDialog : Window
{
    public bool IsFirstRun { get; }

    public PasswordChangeDialog(bool isFirstRun = false)
    {
        InitializeComponent();
        IsFirstRun = isFirstRun;

        if (IsFirstRun)
        {
            Title = "Set encryption password";
            CurrentPasswordLabel.Visibility = Visibility.Collapsed;
            CurrentPasswordField.Visibility = Visibility.Collapsed;
        }
        else
        {
            Title = "Change encryption password";
        }

        // JJ
        // // Wire up tracking to update the meter dynamically
        // NewPasswordBox.PasswordChanged += NewPassword_PasswordChanged;
        // NewPasswordText.TextChanged += NewPassword_PasswordChanged;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CurrentPasswordToggle_Click(object sender, RoutedEventArgs e)
    {
        TogglePasswordVisibility(CurrentPasswordBox, CurrentPasswordText, CurrentPasswordToggle, isVisible: CurrentPasswordToggle.IsChecked == true);
    }

    private void NewPasswordToggle_Click(object sender, RoutedEventArgs e)
    {
        TogglePasswordVisibility(NewPasswordBox, NewPasswordText, NewPasswordToggle, isVisible: NewPasswordToggle.IsChecked == true);
    }

    private void ConfirmPasswordToggle_Click(object sender, RoutedEventArgs e)
    {
        TogglePasswordVisibility(ConfirmPasswordBox, ConfirmPasswordText, ConfirmPasswordToggle, isVisible: ConfirmPasswordToggle.IsChecked == true);
    }

    private void NewPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        UpdatePasswordStrength(NewPasswordBox.Password);
    }

    private void NewPasswordText_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePasswordStrength(NewPasswordText.Text);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var current = IsFirstRun ? string.Empty : CurrentPasswordBox.Password;
        var newPassword = GetActivePassword(NewPasswordBox, NewPasswordText);
        var confirmation = GetActivePassword(ConfirmPasswordBox, ConfirmPasswordText);

        if (!IsFirstRun)
        {
            if (string.IsNullOrWhiteSpace(current))
            {
                System.Windows.MessageBox.Show("Enter the current password.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var storedPassword = PasswordStore.GetPassword(PasswordStore.PasswordReference);
            if (!string.Equals(storedPassword, current, StringComparison.Ordinal))
            {
                System.Windows.MessageBox.Show("The current password is incorrect.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
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
            System.Windows.MessageBox.Show("You must acknowledge the password warning before continuing.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PasswordStore.SavePassword(newPassword);
        var config = App.ConfigStore.LoadAsync().GetAwaiter().GetResult();
        config.PasswordReference = PasswordStore.PasswordReference;
        App.ConfigStore.SaveAsync(config).GetAwaiter().GetResult();
        System.Windows.MessageBox.Show(IsFirstRun
            ? "Password saved. The next backup will start a new archive chain."
            : "Password updated. The next backup will start a new archive chain.", "EVA", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private static void TogglePasswordVisibility(WpfPasswordBox passwordBox, WpfTextBox textBox, WpfToggleButton toggleButton, bool isVisible)
    {
        if (isVisible)
        {
            textBox.Text = passwordBox.Password;
            textBox.Visibility = Visibility.Visible;
            passwordBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            passwordBox.Password = textBox.Text;
            passwordBox.Visibility = Visibility.Visible;
            textBox.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdatePasswordStrength(string pwd)
    {
        if (StrengthMeter == null) return;

        // Handle empty or baseline states instantly
        if (string.IsNullOrEmpty(pwd))
        {
            StrengthMeter.Value = 0;
            StrengthMeter.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        // 1. Calculate the Character Pool Size (Pool = number of possible characters available)
        int poolSize = 0;
        if (System.Text.RegularExpressions.Regex.IsMatch(pwd, @"[a-z]")) poolSize += 26;
        if (System.Text.RegularExpressions.Regex.IsMatch(pwd, @"[A-Z]")) poolSize += 26;
        if (System.Text.RegularExpressions.Regex.IsMatch(pwd, @"[0-9]")) poolSize += 10;
        if (System.Text.RegularExpressions.Regex.IsMatch(pwd, @"[^a-zA-Z0-9]")) poolSize += 33; // Symbols

        // Fallback if they type something unexpected
        if (poolSize == 0) poolSize = 26; 

        // 2. Calculate Information Entropy: Length * Log2(PoolSize)
        // This rewards length exponentially, making passphrases look beautifully smooth
        double entropy = pwd.Length * (Math.Log(poolSize) / Math.Log(2));

        // 3. Map Entropy to a 0 - 100 percentage bar
        // 60+ bits of entropy is generally considered highly secure for basic user accounts
        double targetMaxEntropy = 60.0;
        int percentage = (int)Math.Min((entropy / targetMaxEntropy) * 100, 100);

        // Apply fluid value to the progress bar
        StrengthMeter.Value = percentage;

        // 4. Smooth, granular tiers based on percentage thresholds
        if (percentage < 25)
        {
            StrengthMeter.Foreground = System.Windows.Media.Brushes.Red;
        }
        else if (percentage < 50)
        {
            StrengthMeter.Foreground = System.Windows.Media.Brushes.Orange;
        }
        else if (percentage < 75)
        {
            StrengthMeter.Foreground = System.Windows.Media.Brushes.YellowGreen;
        }
        else
        {
            StrengthMeter.Foreground = System.Windows.Media.Brushes.Green;
        }
    }


    private static string GetActivePassword(WpfPasswordBox passwordBox, WpfTextBox textBox)
    {
        return passwordBox.Visibility == Visibility.Visible ? passwordBox.Password : textBox.Text;
    }
}
