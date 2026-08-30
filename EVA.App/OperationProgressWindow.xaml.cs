using System.Windows;

namespace EVA.App;

public partial class OperationProgressWindow : Window
{
    public OperationProgressWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }
}
