using System.Windows;

namespace Sample.Wpf.NeonControlCenter;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = MissionControlViewModel.Shared;
    }

    private void EngageButton_Click(object sender, RoutedEventArgs e)
    {
        MissionControlViewModel.Shared.Engage();
    }

    private void AbortButton_Click(object sender, RoutedEventArgs e)
    {
        MissionControlViewModel.Shared.Abort();
    }

    private void ClearFeedButton_Click(object sender, RoutedEventArgs e)
    {
        MissionControlViewModel.Shared.ClearAlertFeed();
    }
}
