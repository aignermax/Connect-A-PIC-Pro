using Avalonia.Controls;

namespace CAP.Avalonia.Views;

/// <summary>
/// Home screen UserControl shown as the main window's startup state
/// (recent projects, new/open project). All behavior lives in
/// <see cref="ViewModels.Home.HomeViewModel"/>.
/// </summary>
public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }
}
