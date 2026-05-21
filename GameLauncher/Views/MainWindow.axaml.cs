using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using GameLauncher.ViewModels;

namespace GameLauncher.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _globalHotkeyPoller = new()
    {
        Interval = TimeSpan.FromMilliseconds(150)
    };
    private MainViewModel? _boundVm;
    private bool _globalHotkeyLatched;

    // Separate overlay window for the global Quick Menu (shown over games without
    // restoring the full launcher — mirrors the Steam/NVIDIA overlay pattern).
    private QuickMenuWindow? _quickMenuWindow;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => RefreshGlobalHotkeyPolling();
        Closed += OnMainWindowClosed;
        _globalHotkeyPoller.Tick += OnGlobalHotkeyTick;
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _globalHotkeyPoller.Stop();
        _quickMenuWindow?.Close();
        _quickMenuWindow = null;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm != null)
        {
            _boundVm.PropertyChanged -= OnMainViewModelPropertyChanged;
            _boundVm.SettingsVm.SettingsApplied -= RefreshGlobalHotkeyPolling;
        }

        if (DataContext is MainViewModel vm)
        {
            _boundVm = vm;
            vm.PropertyChanged += OnMainViewModelPropertyChanged;
            vm.SettingsVm.SettingsApplied += RefreshGlobalHotkeyPolling;

            // Capture the window state at the time of minimise so we can restore
            // to the same state (FullScreen, Normal, Maximized) when the game exits.
            WindowState _stateBeforeMinimize = WindowState;

            vm.MinimizeWindowRequested = () =>
            {
                _stateBeforeMinimize = WindowState;
                WindowState = WindowState.Minimized;
            };
            vm.RestoreWindowRequested  = () =>
            {
                WindowState = _stateBeforeMinimize;
                Activate();
            };
            RefreshGlobalHotkeyPolling();
        }
        else
        {
            _boundVm = null;
            RefreshGlobalHotkeyPolling();
        }
    }

    /// <summary>
    /// Handles global keyboard and gamepad navigation.
    /// Xbox / PlayStation controllers connected via XInput/DirectInput are reported
    /// as standard keyboard keys by Windows:
    ///   Escape / B-button  → close overlays / go back / close nav menu
    ///   Enter / A-button   → confirm; when nav open, selects current page and closes nav
    ///   Left               → open nav sidebar (when not in detail / text input)
    ///   Right              → close nav sidebar (when open)
    ///   Up / Down          → when nav sidebar is open: navigate up/down through menu items
    ///   PageUp  / LB       → navigate to previous page (always available)
    ///   PageDown / RB      → navigate to next page (always available)
    ///   F5                 → refresh / reload library
    ///   Left Shift + Left Ctrl → toggle Quick Menu overlay
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // Left Shift + Left Ctrl → Quick Menu toggle (takes priority).
        // Handle both orderings: user may press Shift first then Ctrl, or Ctrl first then Shift.
        bool isShiftCtrl = (e.Key == Key.LeftCtrl  && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ||
                           (e.Key == Key.LeftShift && e.KeyModifiers.HasFlag(KeyModifiers.Control));
        if (isShiftCtrl)
        {
            vm.ToggleQuickMenu();
            e.Handled = true;
            return;
        }

        // While quick menu is open, prioritize PS5-style hub navigation.
        if (vm.ShowQuickMenu)
        {
            switch (e.Key)
            {
                case Key.Left:
                    vm.QuickMenuVm.MoveHubSelection(-1);
                    e.Handled = true;
                    return;
                case Key.Right:
                    vm.QuickMenuVm.MoveHubSelection(1);
                    e.Handled = true;
                    return;
                case Key.Enter:
                case Key.Space:
                    vm.QuickMenuVm.ActivateSelectedHub();
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            // Navigate back from a detail or friend-profile overlay
            // Also close nav sidebar when open
            case Key.Escape:
            case Key.BrowserBack:
                if (vm.ShowQuickMenu)
                {
                    if (!vm.QuickMenuVm.HandleBackNavigation())
                        vm.ShowQuickMenu = false;
                    e.Handled = true;
                }
                else if (vm.ShowDetail)
                {
                    vm.DetailVm.CloseCommand.Execute(null);
                    e.Handled = true;
                }
                else if (vm.ShowFriendProfile)
                {
                    vm.CloseFriendProfileCommand.Execute(null);
                    e.Handled = true;
                }
                else if (vm.IsNavExpanded)
                {
                    vm.IsNavExpanded = false;
                    e.Handled = true;
                }
                break;

            // LB / PageUp → previous page (always works regardless of nav state)
            case Key.PageUp:
                if (!vm.ShowDetail && !vm.ShowFriendProfile)
                {
                    NavigatePrev(vm);
                    e.Handled = true;
                }
                break;

            // RB / PageDown → next page (always works regardless of nav state)
            case Key.PageDown:
                if (!vm.ShowDetail && !vm.ShowFriendProfile)
                {
                    NavigateNext(vm);
                    e.Handled = true;
                }
                break;

            // Left arrow → open the nav sidebar (when not in a text input)
            case Key.Left:
                if (!vm.ShowDetail && !vm.ShowFriendProfile && !IsTextInputFocused())
                {
                    if (!vm.IsNavExpanded)
                    {
                        vm.IsNavExpanded = true;
                        e.Handled = true;
                    }
                }
                break;

            // Right arrow → close the nav sidebar (when open)
            case Key.Right:
                if (!vm.ShowDetail && !vm.ShowFriendProfile && !IsTextInputFocused())
                {
                    if (vm.IsNavExpanded)
                    {
                        vm.IsNavExpanded = false;
                        e.Handled = true;
                    }
                }
                break;

            // Up arrow → when nav is open, move to previous menu item
            case Key.Up:
                if (vm.IsNavExpanded && !vm.ShowDetail && !vm.ShowFriendProfile && !IsTextInputFocused())
                {
                    NavigatePrev(vm);
                    e.Handled = true;
                }
                break;

            // Down arrow → when nav is open, move to next menu item
            case Key.Down:
                if (vm.IsNavExpanded && !vm.ShowDetail && !vm.ShowFriendProfile && !IsTextInputFocused())
                {
                    NavigateNext(vm);
                    e.Handled = true;
                }
                break;

            // Enter → when nav is open, select current page and close the sidebar
            case Key.Enter:
                if (vm.IsNavExpanded && !vm.ShowDetail && !vm.ShowFriendProfile && !IsTextInputFocused())
                {
                    vm.IsNavExpanded = false;
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>Returns true when a TextBox or similar input control has keyboard focus.</summary>
    private bool IsTextInputFocused()
    {
        var focused = FocusManager?.GetFocusedElement();
        return focused is TextBox or NumericUpDown;
    }

    private static readonly string[] _navPages =
        ["dashboard", "library", "store", "friends", "profile", "settings"];

    private static void NavigatePrev(MainViewModel vm)
    {
        int idx = System.Array.IndexOf(_navPages, vm.ActivePage);
        if (idx > 0)
            vm.NavigateCommand.Execute(_navPages[idx - 1]);
    }

    private static void NavigateNext(MainViewModel vm)
    {
        int idx = System.Array.IndexOf(_navPages, vm.ActivePage);
        if (idx >= 0 && idx < _navPages.Length - 1)
            vm.NavigateCommand.Execute(_navPages[idx + 1]);
    }

    private void RefreshGlobalHotkeyPolling()
    {
        if (OperatingSystem.IsWindows() && _boundVm?.SettingsVm.EnableGlobalQuickMenuHotkey == true)
        {
            if (!_globalHotkeyPoller.IsEnabled)
                _globalHotkeyPoller.Start();
        }
        else
        {
            _globalHotkeyPoller.Stop();
            _globalHotkeyLatched = false;
        }
    }

    private void OnGlobalHotkeyTick(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsWindows() || _boundVm == null || !_boundVm.SettingsVm.EnableGlobalQuickMenuHotkey)
            return;

        bool ctrlDown  = (GameLauncher.Services.NativeMethods.GetAsyncKeyState(GameLauncher.Services.NativeMethods.VK_LCONTROL) & 0x8000) != 0;
        bool shiftDown = (GameLauncher.Services.NativeMethods.GetAsyncKeyState(GameLauncher.Services.NativeMethods.VK_LSHIFT)   & 0x8000) != 0;
        bool bothDown  = ctrlDown && shiftDown;

        if (bothDown && !_globalHotkeyLatched)
        {
            _globalHotkeyLatched = true;
            OpenGlobalQuickMenu();
        }
        else if (!bothDown)
        {
            _globalHotkeyLatched = false;
        }
    }

    /// <summary>
    /// Opens the Quick Menu overlay without restoring or focusing the main launcher window.
    /// When a game is running and the launcher is minimised, shows a separate always-on-top
    /// borderless window positioned at the right edge of the screen — matching the Steam /
    /// NVIDIA overlay pattern for borderless-windowed apps and games.
    /// When the launcher is already in the foreground (no game running), toggles the
    /// inline quick menu panel instead.
    /// </summary>
    private void OpenGlobalQuickMenu()
    {
        if (_boundVm == null) return;

        bool gameIsRunning = _boundVm.DetailVm.IsGameRunning;
        bool launcherMinimized = WindowState == WindowState.Minimized;

        // If a game is running (or the launcher is minimized), use the separate overlay
        // window so we never steal focus from the game.
        if (gameIsRunning || launcherMinimized)
        {
            _boundVm.QuickMenuVm.Refresh(
                currentUsername:     _boundVm.ProfileVm.Username,
                currentGameTitle:     _boundVm.DetailVm.IsGameRunning ? _boundVm.DetailVm.Title : null,
                sessionStartedAt:     _boundVm.DetailVm.IsGameRunning
                    ? Services.PlaytimeService.GetActiveSessionStart(_boundVm.DetailVm.Platform, _boundVm.DetailVm.Title)
                      ?? Services.PlaytimeService.GetAnyActiveSessionStart()
                    : null,
                onlineFriends:        _boundVm.FriendsVm.OnlineFriends
                    .Select(f => new FriendPresenceVm
                    {
                        Username = f.Username,
                        CurrentGame = f.CurrentGame ?? "",
                        Status = f.Status
                    })
                    .ToList(),
                allFriends:           _boundVm.FriendsVm.OnlineFriends
                    .Concat(_boundVm.FriendsVm.OfflineFriends)
                    .Select(f => new FriendPresenceVm
                    {
                        Username = f.Username,
                        CurrentGame = f.CurrentGame ?? "",
                        Status = f.Status
                    })
                    .ToList(),
                unreadCount:          _boundVm.InboxVm.PendingInvites.Count,
                lastMessage:          _boundVm.InboxVm.Conversations
                    .OrderByDescending(c => c.LastMessageAt)
                    .Select(c => c.LastMessage)
                    .FirstOrDefault(),
                unlockedAchievements: _boundVm.DetailVm.HasAchievements ? _boundVm.DetailVm.Achievements.Count(a => a.IsUnlocked) : 0,
                totalAchievements:    _boundVm.DetailVm.HasAchievements ? _boundVm.DetailVm.Achievements.Count : 0,
                achievements:         _boundVm.DetailVm.Achievements,
                recentGames:          _boundVm.DashboardVm.Ps5RecentGames.ToList(),
                activePageKey:        _boundVm.ActivePage,
                pendingDownloadCount: _boundVm.LibraryVm.ReadyToInstall.Count);

            if (_quickMenuWindow == null)
            {
                _quickMenuWindow = new QuickMenuWindow { DataContext = _boundVm.QuickMenuVm };
                // Wire dismiss so closing the overlay window is reflected in the VM
                _boundVm.QuickMenuVm.OnDismiss = () =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _boundVm.ShowQuickMenu = false;
                        _quickMenuWindow?.Hide();
                    });
                };
            }

            if (_quickMenuWindow.IsVisible)
            {
                _quickMenuWindow.Hide();
                _boundVm.ShowQuickMenu = false;
            }
            else
            {
                _quickMenuWindow.ShowOverGame();
            }
            return;
        }

        // Launcher is in the foreground — the inline quick menu is toggled by
        // OnKeyDown (which fires synchronously on the key press and is always
        // processed before the 150 ms poller tick).  Calling ToggleQuickMenu()
        // here as well would create a second toggle within 150 ms that immediately
        // closes the menu the user just opened, causing the open/close glitch.
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_boundVm == null || e.PropertyName != nameof(MainViewModel.ShowQuickMenu))
            return;

        // When the inline quick menu is dismissed, also hide the overlay window if visible.
        if (!_boundVm.ShowQuickMenu)
            _quickMenuWindow?.Hide();
    }
}
