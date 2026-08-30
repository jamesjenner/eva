using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Controls.Notifications;
using Avalonia.Media;
using SukiUI.Controls;
using SukiUI.Dialogs;

namespace EVA.App;

public partial class PasswordChangeDialog : SukiWindow
{
    private readonly ISukiDialogManager _dialogManager = new SukiDialogManager();

    // Required by Avalonia XAML loader
    public PasswordChangeDialog() : this(new SukiDialogManager())
    {
    }

    public PasswordChangeDialog(ISukiDialogManager dialogManager)
    {
        InitializeComponent();
        _dialogManager = dialogManager;
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

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var newPassword = NewPasswordText.Text?.Trim() ?? string.Empty;
        var confirmPassword = ConfirmPasswordText.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword != confirmPassword)
        {
            await ShowMessageAsync("Password mismatch", "The new password and confirmation must match.");
            _dialogManager.CreateDialog()
                    .WithActionButton("OK", _ => { }, true)
                    .WithTitle("Password mismatch")
                    .WithContent("The new password and confirmation must match.")
                    .TryShow();            
            return;
        }

        if (AcknowledgeWarningCheck.IsChecked != true)
        {
            await ShowMessageAsync("Confirmation required", "Please confirm the warning before changing the archive password.");
            return;
        }

        PasswordStore.SavePassword(newPassword);
        Close();
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

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var okButton = new Button { Content = "OK", Width = 90, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        okButton.Click += (_, _) => dialog.Close();
        dialog.Content = new Grid
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                okButton
            }
        };
        Grid.SetRow(okButton, 1);
        await dialog.ShowDialog<object?>(this);
    }
}
