﻿namespace Reloaded.Mod.Launcher.Pages.Dialogs;

/// <summary>
/// Interaction logic for FirstLaunch.xaml
/// </summary>
public partial class FirstLaunch : ReloadedWindow
{
    public new FirstLaunchViewModel ViewModel { get; set; } = Lib.IoC.Get<FirstLaunchViewModel>();

    public Visibility OriginalCloseVisibility;
    public Visibility OriginalMinimizeVisibility;
    public Visibility OriginalMaximizeVisibility;

    public FirstLaunch()
    {
        InitializeComponent();
            
        // Disable Original Button Visibility
        OriginalCloseVisibility = base.ViewModel.CloseButtonVisibility;
        OriginalMinimizeVisibility = base.ViewModel.MinimizeButtonVisibility;
        OriginalMaximizeVisibility = base.ViewModel.MaximizeButtonVisibility;
        base.ViewModel.CloseButtonVisibility = Visibility.Collapsed;
        base.ViewModel.MaximizeButtonVisibility = Visibility.Collapsed;
        base.ViewModel.MinimizeButtonVisibility = Visibility.Collapsed;

        // Init Events
        this.Closing += OnWindowClosing;
        ViewModel.Initialize(() => ActionWrappers.ExecuteWithApplicationDispatcher(this.Close));
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        // Re-Set Button Visibility
        base.ViewModel.CloseButtonVisibility = OriginalCloseVisibility;
        base.ViewModel.MinimizeButtonVisibility = OriginalMinimizeVisibility;
        base.ViewModel.MaximizeButtonVisibility = OriginalMaximizeVisibility;
    }
}