using System;
using System.ComponentModel;
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
    private bool _restoreToMinimizedAfterQuickMenu;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => RefreshGlobalHotkeyPolling();
        Closed += (_, _) => _globalHotkeyPoller.Stop();
        _globalHotkeyPoller.Tick += OnGlobalHotkeyTick;
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

        switch (e.Key)
        {
            // Navigate back from a detail or friend-profile overlay
            // Also close nav sidebar when open
            case Key.Escape:
            case Key.BrowserBack:
                if (vm.ShowQuickMenu)
                {
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

        bool ctrlDown = (GameLauncher.Services.NativeMethods.GetAsyncKeyState(GameLauncher.Services.NativeMethods.VK_LCONTROL) & 0x8000) != 0;
        bool shiftDown = (GameLauncher.Services.NativeMethods.GetAsyncKeyState(GameLauncher.Services.NativeMethods.VK_LSHIFT) & 0x8000) != 0;
        bool bothDown = ctrlDown && shiftDown;

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

    private void OpenGlobalQuickMenu()
    {
        if (_boundVm == null) return;

        bool compatibilityMode = _boundVm.SettingsVm.CompatibilityOverlayMode;
        bool wasMinimized = WindowState == WindowState.Minimized;
        if (wasMinimized)
        {
            WindowState = WindowState.FullScreen;
            _restoreToMinimizedAfterQuickMenu = compatibilityMode && _boundVm.DetailVm.IsGameRunning;
        }

        if (compatibilityMode)
            Topmost = true;

        Activate();
        _boundVm.ToggleQuickMenu();
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_boundVm == null || e.PropertyName != nameof(MainViewModel.ShowQuickMenu))
            return;

        if (_boundVm.ShowQuickMenu)
        {
            if (_boundVm.SettingsVm.CompatibilityOverlayMode)
                Topmost = true;
            return;
        }

        Topmost = false;
        if (_restoreToMinimizedAfterQuickMenu)
        {
            _restoreToMinimizedAfterQuickMenu = false;
            WindowState = WindowState.Minimized;
        }
    }
}
