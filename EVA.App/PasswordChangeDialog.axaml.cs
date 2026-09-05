using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SukiUI.Controls;

namespace EVA.App;

public partial class PasswordChangeDialog : SukiWindow
{
    private readonly bool _isFirstRun;

    public PasswordChangeDialog()
    {
        InitializeComponent();
        _isFirstRun = !PasswordStore.HasStoredPassword();
        CurrentPasswordLabel.IsVisible = !_isFirstRun;
        CurrentPasswordText.IsVisible = !_isFirstRun;
        CurrentPasswordToggle.IsVisible = !_isFirstRun;
        CurrentPasswordError.IsVisible = !_isFirstRun;
        Title = _isFirstRun ? "Set encryption password" : "Change encryption password";
        NewPasswordText.PropertyChanged += NewPasswordText_PropertyChanged;
    }

    private void NewPasswordText_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty)
        {
            UpdatePasswordStrength(NewPasswordText.Text);
        }
    }

    private void UpdatePasswordStrength(string? password)
    {
        var strength = CalculatePasswordStrength(password);
        PasswordStrengthBar.Value = strength;
        
        PasswordStrengthBar.Foreground = strength switch 
        {
            < 25  => new SolidColorBrush(Colors.Crimson),
            < 50  => new SolidColorBrush(Colors.OrangeRed),
            < 75  => new SolidColorBrush(Colors.Orange),
            _     => new SolidColorBrush(Colors.MediumSeaGreen)
        };
    }

    private static double CalculatePasswordStrength(string? password)
    {
        if (string.IsNullOrEmpty(password)) return 0;

        // Length is the primary factor
        // NIST recommends minimum 8, passphrases typically 20+
        double lengthScore = password.Length switch
        {
            < 8  => 10,
            < 12 => 25,
            < 16 => 45,
            < 20 => 60,
            < 28 => 75,
            < 36 => 88,
            _    => 95   // very long passphrase
        };

        // Character variety adds a small bonus only
        double varietyBonus = 0;
        if (password.Any(char.IsLower))  varietyBonus += 1;
        if (password.Any(char.IsUpper))  varietyBonus += 1;
        if (password.Any(char.IsDigit))  varietyBonus += 1.5;
        if (password.Any(c => !char.IsLetterOrDigit(c))) varietyBonus += 1.5;
        
        // spaces indicate a passphrase — bonus
        if (password.Any(c => c == ' ')) varietyBonus += 1;

        return Math.Min(lengthScore + varietyBonus, 100);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        PasswordStore.SavePassword(NewPasswordText.Text!.Trim());
        Close();
    }

    private bool Validate()
    {
        var valid = true;
        var errors = new List<string>();

        NewPasswordError.Opacity = 0;
        CurrentPasswordError.Opacity = 0;
        ConfirmPasswordError.Opacity = 0;
        AcknowledgeError.Opacity = 0;
        ErrorSummaryBorder.Opacity = 0;

        if (!_isFirstRun)
        {
            var storedPassword = PasswordStore.GetPassword(PasswordStore.PasswordReference);
            if (storedPassword is null || storedPassword != CurrentPasswordText.Text?.Trim())
            {
                CurrentPasswordError.Text = "Current password is incorrect";
                CurrentPasswordError.Opacity = 1;
                errors.Add("Current password is incorrect");
                valid = false;
            }
        }

        if (string.IsNullOrWhiteSpace(NewPasswordText.Text))
        {
            NewPasswordError.Text = "A new password is required";
            NewPasswordError.Opacity = 1;
            errors.Add("New password is required");
            valid = false;
        } else if(NewPasswordText.Text.Length < 16)
        {
            NewPasswordError.Text = "Password must be at least 16 characters";
            NewPasswordError.Opacity = 1;
            errors.Add("New password must be at least 16 characters");
            valid = false;
        }
        else if (string.IsNullOrWhiteSpace(ConfirmPasswordText.Text))
        {
            ConfirmPasswordError.Text = "Please confirm the new password";
            ConfirmPasswordError.Opacity = 1;
            errors.Add("A confirmation password is required");
            valid = false;
        }
        else if (NewPasswordText.Text != ConfirmPasswordText.Text)
        {
            ConfirmPasswordError.Text = "Must match the new password";
            ConfirmPasswordError.Opacity = 1;
            errors.Add("Passwords do not match");
            valid = false;
        }

        if (AcknowledgeWarningCheck.IsChecked != true)
        {
            AcknowledgeError.Opacity = 1;
            errors.Add("Please acknowledge to continue");
            valid = false;
        }

        if (!valid)
        {
            ErrorSummaryBorder.Opacity = 1;
        }

        return valid;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CurrentPasswordToggle_Click(object? sender, RoutedEventArgs e) => TogglePassword(CurrentPasswordText, CurrentPasswordHiddenIcon, CurrentPasswordVisibleIcon);

    private void NewPasswordToggle_Click(object? sender, RoutedEventArgs e) => TogglePassword(NewPasswordText, NewPasswordHiddenIcon, NewPasswordVisibleIcon);

    private void ConfirmPasswordToggle_Click(object? sender, RoutedEventArgs e) => TogglePassword(ConfirmPasswordText, ConfirmPasswordHiddenIcon, ConfirmPasswordVisibleIcon);

    private static void TogglePassword(TextBox textBox, Control hiddenIcon, Control visibleIcon)
    {
        textBox.RevealPassword = !textBox.RevealPassword;
        hiddenIcon.IsVisible = !textBox.RevealPassword;
        visibleIcon.IsVisible = textBox.RevealPassword;
    }
}
