using CAP.Avalonia.ViewModels.Settings;
using Shouldly;

namespace UnitTests.ViewModels;

/// <summary>Tests for settings-page navigation by page type.</summary>
public class SettingsWindowViewModelTests
{
    private sealed class PageA : ISettingsPage
    {
        public string Title => "A";
        public string Icon => "a";
        public string? Category => null;
        public object ViewModel => this;
    }

    private sealed class PageB : ISettingsPage
    {
        public string Title => "B";
        public string Icon => "b";
        public string? Category => null;
        public object ViewModel => this;
    }

    [Fact]
    public void SelectPage_KnownType_SwitchesSelectedPage()
    {
        var vm = new SettingsWindowViewModel(new ISettingsPage[] { new PageA(), new PageB() });

        vm.SelectPage(typeof(PageB));

        vm.SelectedPage.ShouldBeOfType<PageB>();
    }

    [Fact]
    public void SelectPage_UnknownType_KeepsCurrentSelection()
    {
        var vm = new SettingsWindowViewModel(new ISettingsPage[] { new PageA() });

        vm.SelectPage(typeof(PageB));

        vm.SelectedPage.ShouldBeOfType<PageA>();
    }
}
