using System;
using Avalonia.Controls;
using Avalonia.Input;
using GameLauncher.ViewModels;

namespace GameLauncher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.MinimizeWindowRequested = () => WindowState = WindowState.Minimized;
            vm.RestoreWindowRequested  = () =>
            {
                WindowState = WindowState.Normal;
                Activate();
            };
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
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        switch (e.Key)
        {
            // Navigate back from a detail or friend-profile overlay
            // Also close nav sidebar when open
            case Key.Escape:
            case Key.BrowserBack:
                if (vm.ShowDetail)
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
}
