using System;
using Windows.ApplicationModel.Activation;
using Windows.Globalization;
using WinverUWP.Helpers;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace WinverUWP;

sealed partial class App : Application
{
    private static readonly Size DefaultViewSize = new Size(500, 640);

    public App()
    {
        // Commenting this out since it is too buggy right mow.
        // if (ApiInformation.IsTypePresent("Windows.UI.Composition.CompositionShape"))
        //     Native.VectorDetours.EnableVectorRendering();
        
        ApplicationLanguages.PrimaryLanguageOverride = CultureInfoHelper.GetWindowsDisplayLanguage();
        this.InitializeComponent();
    }

    protected override void OnActivated(IActivatedEventArgs args)
    {
        StartApp();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        StartApp();
    }

    void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
    }

    private void StartApp()
    {
        ApplicationView.PreferredLaunchViewSize = DefaultViewSize;
        ApplicationView.PreferredLaunchWindowingMode = ApplicationViewWindowingMode.PreferredLaunchViewSize;

        Frame frame;
        frame = Window.Current.Content as Frame;
        if (frame != null)
            return;
        frame = new Frame();
        frame.NavigationFailed += OnNavigationFailed;
        frame.Navigate(typeof(MainPage));
        Window.Current.Content = frame;

        var page = (MainPage)frame.Content;
        page.Initialize();
        var size = DefaultViewSize;
        ApplicationView.PreferredLaunchViewSize = size;
        ApplicationView.GetForCurrentView().TryResizeView(size);
        Window.Current.Activate();
        _ = page.RefreshStorageAsync();
    }

}
