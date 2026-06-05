using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Emutastic.Services;

namespace Emutastic.Views;

/// <summary>
/// Adds a RetroAchievements friend by username (port of upstream AddFriendDialog).
/// Two-step UI: type username → Lookup (API_GetUserProfile, preview card) →
/// Add Friend (persists via FriendService.AddAsync).
/// </summary>
public partial class AddFriendDialog : Window
{
    /// <summary>Set by the caller before ShowDialog.</summary>
    public FriendService? FriendService { get; set; }

    private FriendService.LookupResult? _pendingPreview;

    public AddFriendDialog()
    {
        InitializeComponent();
        this.FindControl<Button>("LookupBtn")!.Click += async (_, _) => await DoLookup();
        this.FindControl<Button>("AddBtn")!.Click += Add_Click;
        this.FindControl<Button>("CancelBtn")!.Click += (_, _) => Close(false);
        var input = this.FindControl<TextBox>("UsernameInput")!;
        input.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await DoLookup(); }
        };
        Opened += (_, _) => input.Focus();
    }

    private async Task DoLookup()
    {
        if (FriendService == null) return;
        string name = (this.FindControl<TextBox>("UsernameInput")!.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowError("Enter a username first.");
            return;
        }

        var lookupBtn = this.FindControl<Button>("LookupBtn")!;
        var addBtn = this.FindControl<Button>("AddBtn")!;
        lookupBtn.IsEnabled = false;
        this.FindControl<TextBlock>("ErrorText")!.IsVisible = false;
        this.FindControl<Border>("PreviewCard")!.IsVisible = false;
        addBtn.IsEnabled = false;

        try
        {
            var result = await FriendService.LookupAsync(name);
            if (!result.Success)
            {
                ShowError(result.Error ?? "Lookup failed.");
                return;
            }

            _pendingPreview = result;
            this.FindControl<TextBlock>("PreviewName")!.Text = result.Username;
            this.FindControl<TextBlock>("PreviewPoints")!.Text =
                $"{result.PointsHardcore:N0} pts · {result.PointsSoftcore:N0} softcore";
            this.FindControl<TextBlock>("PreviewMotto")!.Text = string.IsNullOrWhiteSpace(result.Motto)
                ? "(no motto set)" : result.Motto;
            var avatar = this.FindControl<Image>("PreviewAvatar")!;
            avatar.Source = null;
            if (!string.IsNullOrEmpty(result.AvatarUrl))
                FriendImageLoader.Load(avatar, result.AvatarUrl, "add-friend", "preview");

            this.FindControl<Border>("PreviewCard")!.IsVisible = true;
            addBtn.IsEnabled = true;
        }
        finally { lookupBtn.IsEnabled = true; }
    }

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (FriendService == null || _pendingPreview == null) return;
        var addBtn = this.FindControl<Button>("AddBtn")!;
        addBtn.IsEnabled = false;
        try
        {
            bool added = await FriendService.AddAsync(_pendingPreview);
            if (!added)
            {
                ShowError("That friend is already on your list.");
                addBtn.IsEnabled = true;
                return;
            }
            Close(true);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            addBtn.IsEnabled = true;
        }
    }

    private void ShowError(string msg)
    {
        var err = this.FindControl<TextBlock>("ErrorText")!;
        err.Text = msg;
        err.IsVisible = true;
    }
}
