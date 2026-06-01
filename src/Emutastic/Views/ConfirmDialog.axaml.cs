using Avalonia.Controls;

namespace Emutastic.Views;

/// <summary>Yes/No confirmation. ShowDialog&lt;bool&gt; returns true if confirmed.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() : this("Confirm", "Are you sure?", "OK") { }

    public ConfirmDialog(string title, string message, string confirmText = "OK", bool danger = false, bool infoOnly = false)
    {
        InitializeComponent();
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
        var confirm = this.FindControl<Button>("ConfirmButton")!;
        confirm.Content = confirmText;
        confirm.Click += (_, _) => Close(true);
        var cancel = this.FindControl<Button>("CancelButton")!;
        cancel.Click += (_, _) => Close(false);
        // Info-only (single-button) popups hide Cancel, matching upstream's collapsed cancel.
        if (infoOnly) cancel.IsVisible = false;
    }
}
