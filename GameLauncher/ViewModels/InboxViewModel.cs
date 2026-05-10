using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;

namespace GameLauncher.ViewModels;

public partial class InboxViewModel : ViewModelBase
{
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _showConversation;
    [ObservableProperty] private string _conversationFriend = "";
    [ObservableProperty] private string _newMessageText = "";
    [ObservableProperty] private bool _isSendingMessage;
    [ObservableProperty] private string _messageError = "";

    public ObservableCollection<InboxConversationVm> Conversations { get; } = new();
    public ObservableCollection<GameInvite> PendingInvites { get; } = new();
    public ObservableCollection<Message> ConversationMessages { get; } = new();

    public bool HasConversations => Conversations.Count > 0;
    public bool HasPendingInvites => PendingInvites.Count > 0;
    public bool HasEmptyInbox => !IsLoading && Conversations.Count == 0 && PendingInvites.Count == 0;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(HasEmptyInbox));

    private GameOsClient? _client;
    private string _username = "";

    public Action<string>? OnViewFriendProfile { get; set; }

    public void Load(GameOsClient client, string username)
    {
        _client = client;
        _username = username;
        _ = LoadAsync();
    }

    public void LoadDemo(string username)
    {
        _client = null;
        _username = username;
        IsLoading = false;
        ErrorMessage = "";
        Conversations.Clear();
        PendingInvites.Clear();
        ConversationMessages.Clear();

        Conversations.Add(new InboxConversationVm
        {
            FriendUsername = "NintendoFan42",
            LastMessage = "Dude, have you tried the new DLC for Elden Ring yet?!",
            LastMessageAt = "2026-03-10T18:15:00Z"
        });
        Conversations.Add(new InboxConversationVm
        {
            FriendUsername = "GamingWithLex",
            LastMessage = "We should queue Mario Kart tonight.",
            LastMessageAt = "2026-03-10T16:15:00Z"
        });
        PendingInvites.Add(new GameInvite
        {
            InviteId = "demo-invite",
            From = "RetroKing",
            GameName = "Halo 3",
            SentAt = "2026-03-10T17:00:00Z",
            Status = "pending"
        });

        OnPropertyChanged(nameof(HasConversations));
        OnPropertyChanged(nameof(HasPendingInvites));
        OnPropertyChanged(nameof(HasEmptyInbox));
    }

    [RelayCommand]
    private async Task OpenConversation(string friendUsername)
    {
        if (string.IsNullOrWhiteSpace(friendUsername) || _client == null) return;

        ConversationFriend = friendUsername;
        MessageError = "";
        ShowConversation = true;
        ConversationMessages.Clear();

        try
        {
            var messages = await _client.GetMessagesAsync(friendUsername);
            foreach (var message in messages.OrderBy(m => m.SentAt))
                ConversationMessages.Add(message);
        }
        catch (Exception ex)
        {
            MessageError = $"Could not load messages: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SendMessage()
    {
        if (_client == null || string.IsNullOrWhiteSpace(NewMessageText) || string.IsNullOrWhiteSpace(ConversationFriend))
            return;

        string text = NewMessageText.Trim();
        NewMessageText = "";
        MessageError = "";
        IsSendingMessage = true;
        try
        {
            await _client.SendMessageAsync(ConversationFriend, text);
            ConversationMessages.Add(new Message
            {
                From = _username,
                Text = text,
                SentAt = DateTimeOffset.UtcNow.ToString("o"),
            });
            UpdateConversationPreview(ConversationFriend, text, DateTimeOffset.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            MessageError = $"Send failed: {ex.Message}";
            NewMessageText = text;
        }
        finally
        {
            IsSendingMessage = false;
        }
    }

    [RelayCommand]
    private void CloseConversation()
    {
        ShowConversation = false;
        ConversationFriend = "";
        ConversationMessages.Clear();
        MessageError = "";
    }

    [RelayCommand]
    private void ViewFriendProfile(string friendUsername)
    {
        if (!string.IsNullOrWhiteSpace(friendUsername))
            OnViewFriendProfile?.Invoke(friendUsername);
    }

    [RelayCommand]
    private async Task AcceptInvite(GameInvite? invite)
    {
        if (_client == null || invite == null || string.IsNullOrWhiteSpace(invite.InviteId)) return;
        await _client.RespondInviteAsync(invite.InviteId, "accepted");
        PendingInvites.Remove(invite);
        OnPropertyChanged(nameof(HasPendingInvites));
        OnPropertyChanged(nameof(HasEmptyInbox));
    }

    [RelayCommand]
    private async Task DeclineInvite(GameInvite? invite)
    {
        if (_client == null || invite == null || string.IsNullOrWhiteSpace(invite.InviteId)) return;
        await _client.RespondInviteAsync(invite.InviteId, "declined");
        PendingInvites.Remove(invite);
        OnPropertyChanged(nameof(HasPendingInvites));
        OnPropertyChanged(nameof(HasEmptyInbox));
    }

    private async Task LoadAsync()
    {
        if (_client == null) return;

        IsLoading = true;
        ErrorMessage = "";
        Conversations.Clear();
        PendingInvites.Clear();
        try
        {
            var friendsTask = _client.GetFriendsAsync();
            var invitesTask = _client.GetInvitesAsync();
            await Task.WhenAll(friendsTask, invitesTask);

            foreach (var invite in (await invitesTask)
                .Where(i => string.Equals(i.Status, "pending", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.SentAt))
                PendingInvites.Add(invite);

            var friends = await friendsTask;
            var convoTasks = friends.Select(async friend =>
            {
                try
                {
                    var messages = await _client.GetMessagesAsync(friend);
                    var last = messages.OrderByDescending(m => m.SentAt).FirstOrDefault();
                    return last == null ? null : new InboxConversationVm
                    {
                        FriendUsername = friend,
                        LastMessage = last.Text,
                        LastMessageAt = last.SentAt
                    };
                }
                catch { return null; }
            });

            foreach (var convo in (await Task.WhenAll(convoTasks))
                .Where(c => c != null)
                .OrderByDescending(c => c!.LastMessageAt))
            {
                Conversations.Add(convo!);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not load inbox: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasConversations));
            OnPropertyChanged(nameof(HasPendingInvites));
            OnPropertyChanged(nameof(HasEmptyInbox));
        }
    }

    private void UpdateConversationPreview(string friendUsername, string text, string sentAt)
    {
        var existing = Conversations.FirstOrDefault(c =>
            string.Equals(c.FriendUsername, friendUsername, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.LastMessage = text;
            existing.LastMessageAt = sentAt;
            return;
        }

        Conversations.Insert(0, new InboxConversationVm
        {
            FriendUsername = friendUsername,
            LastMessage = text,
            LastMessageAt = sentAt
        });
        OnPropertyChanged(nameof(HasConversations));
    }
}

public partial class InboxConversationVm : ViewModelBase
{
    [ObservableProperty] private string _friendUsername = "";
    [ObservableProperty] private string _lastMessage = "";
    [ObservableProperty] private string _lastMessageAt = "";
}
