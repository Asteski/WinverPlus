using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.System.Profile;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using WinverUWP.Helpers;

namespace WinverUWP;

public sealed partial class MainPage : Page
{
    private string OSName = "";
    private ResourceLoader resourceLoader;
    private UISettings _uiSettings;
    private CompositionSpriteShape shape;
    private bool isCopying;
    private bool isDeviceCopying;
    private bool settingsReady;
    private bool rememberCards;
    private bool collapsibleCards;
    private readonly Dictionary<Windows.UI.Xaml.Controls.Primitives.ToggleButton, ControlTemplate> cardHeaderTemplates = new Dictionary<Windows.UI.Xaml.Controls.Primitives.ToggleButton, ControlTemplate>();
    private string selectedTheme = "System";
    private readonly SemaphoreSlim settingsWriteLock = new SemaphoreSlim(1, 1);


    public MainPage()
    {
        InitializeComponent();
        Language = Windows.Globalization.ApplicationLanguages.Languages[0];


        _uiSettings = new UISettings();

        resourceLoader = ResourceLoader.GetForCurrentView();

        ApplicationViewTitleBar titleBar = ApplicationView.GetForCurrentView().TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
        coreTitleBar.ExtendViewIntoTitleBar = true;

        UpdateTitleBarLayout(coreTitleBar);
        coreTitleBar.LayoutMetricsChanged += CoreTitleBar_LayoutMetricsChanged;
        coreTitleBar.IsVisibleChanged += CoreTitleBar_IsVisibleChanged;

        Window.Current.SetTitleBar(TitleBar);

        Window.Current.Activated += Current_Activated;
        ActualThemeChanged += (_, _) => RefreshThemeChrome();
    }

    private void LoadSettings()
    {
        JsonObject settings = null;
        try
        {
            var path = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "settings.json");
            if (System.IO.File.Exists(path))
                JsonObject.TryParse(System.IO.File.ReadAllText(path), out settings);
        }
        catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        var legacy = ApplicationData.Current.LocalSettings.Values;
        bool ReadBool(string key, bool fallback) => settings?.ContainsKey(key) == true && settings[key].ValueType == JsonValueType.Boolean
            ? settings[key].GetBoolean() : fallback;
        bool LegacyCard(string key) => !(legacy[key] is bool expanded) || expanded;
        rememberCards = ReadBool("RememberCardStates", false);
        ShowLegalToggle.IsOn = ReadBool("ShowLegalSection", false);
        RememberCardsToggle.IsOn = rememberCards;
        collapsibleCards = ReadBool("CollapsibleCards", false);
        CollapsibleCardsToggle.IsOn = collapsibleCards;
        if (settings?.ContainsKey("Theme") == true && settings["Theme"].ValueType == JsonValueType.String)
            selectedTheme = settings["Theme"].GetString();
        if (selectedTheme != "Light" && selectedTheme != "Dark") selectedTheme = "System";
        DeviceExpander.IsExpanded = !collapsibleCards || !rememberCards || ReadBool("DeviceExpander", LegacyCard("DeviceExpander"));
        SpecExpander.IsExpanded = !collapsibleCards || !rememberCards || ReadBool("SpecExpander", LegacyCard("SpecExpander"));
        LegalExpander.IsExpanded = !collapsibleCards || !rememberCards || ReadBool("LegalExpander", LegacyCard("LegalExpander"));
        LightThemeButton.IsChecked = selectedTheme == "Light";
        DarkThemeButton.IsChecked = selectedTheme == "Dark";
        SystemThemeButton.IsChecked = selectedTheme == "System";
        ApplyLegalVisibility();
        ApplyCardBehavior();
        ApplyTheme();
        settingsReady = true;
    }

    private async Task SaveSettingsAsync()
    {
        if (!settingsReady) return;
        var settings = new JsonObject
        {
            ["CollapsibleCards"] = JsonValue.CreateBooleanValue(collapsibleCards),
            ["RememberCardStates"] = JsonValue.CreateBooleanValue(rememberCards),
            ["ShowLegalSection"] = JsonValue.CreateBooleanValue(ShowLegalToggle.IsOn),
            ["Theme"] = JsonValue.CreateStringValue(selectedTheme),
            ["DeviceExpander"] = JsonValue.CreateBooleanValue(DeviceExpander.IsExpanded),
            ["SpecExpander"] = JsonValue.CreateBooleanValue(SpecExpander.IsExpanded),
            ["LegalExpander"] = JsonValue.CreateBooleanValue(LegalExpander.IsExpanded)
        };
        string json = settings.Stringify();
        await settingsWriteLock.WaitAsync();
        try
        {
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("settings.json", CreationCollisionOption.OpenIfExists);
            using (var transaction = await file.OpenTransactedWriteAsync())
            {
                transaction.Stream.Size = 0;
                using (var writer = new Windows.Storage.Streams.DataWriter(transaction.Stream))
                {
                    writer.WriteString(json);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                await transaction.CommitAsync();
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally { settingsWriteLock.Release(); }
    }

    private static T FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && element.Name == name) return element;
            var match = FindChild<T>(child, name);
            if (match != null) return match;
        }
        return null;
    }

    private void ApplyCardBehavior()
    {
        RememberCardsToggle.IsEnabled = collapsibleCards;
        foreach (var card in new[] { DeviceExpander, SpecExpander, LegalExpander })
        {
            if (!collapsibleCards) card.IsExpanded = true;
            if (collapsibleCards)
            {
                var oldHeader = FindChild<Windows.UI.Xaml.Controls.Primitives.ToggleButton>(card, "ExpanderHeader");
                // The native template is already correct at startup. Only restore it
                // after this page has previously installed a non-collapsible header.
                if (oldHeader == null || !cardHeaderTemplates.ContainsKey(oldHeader)) continue;
                cardHeaderTemplates.Remove(oldHeader);
                var template = card.Template;
                card.Template = null;
                card.Template = template;
                card.ApplyTemplate();
                continue;
            }
            card.ApplyTemplate();
            var header = FindChild<Windows.UI.Xaml.Controls.Primitives.ToggleButton>(card, "ExpanderHeader");
            if (header == null) continue;
            if (!cardHeaderTemplates.ContainsKey(header))
            {
                cardHeaderTemplates[header] = header.Template;
                header.PointerPressed += (sender, args) =>
                {
                    if (collapsibleCards) return;
                    var source = args.OriginalSource as DependencyObject;
                    while (source != null && source != header)
                    {
                        if (source is Button) return;
                        source = VisualTreeHelper.GetParent(source);
                    }
                    args.Handled = true;
                };
                header.KeyDown += (sender, args) =>
                {
                    if (!collapsibleCards && (args.Key == Windows.System.VirtualKey.Space || args.Key == Windows.System.VirtualKey.Enter)) args.Handled = true;
                };
            }
            header.IsTabStop = collapsibleCards;
            header.Template = collapsibleCards ? cardHeaderTemplates[header] : (ControlTemplate)XamlReader.Load(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ToggleButton'><Border x:Name='PassiveHeaderBackground' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='4,4,0,0' Padding='{TemplateBinding Padding}'><Grid><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='0'/></Grid.ColumnDefinitions><ContentPresenter Margin='0,0,16,0' Content='{TemplateBinding Content}' ContentTemplate='{TemplateBinding ContentTemplate}' HorizontalAlignment='Stretch' VerticalAlignment='Center'/></Grid></Border></ControlTemplate>");
            header.ApplyTemplate();
            var background = FindChild<Border>(header, "PassiveHeaderBackground");
            if (background != null)
            {
                // Handle input inside the template, before ToggleButton's class
                // handlers can capture the pointer and start a collapse animation.
                background.PointerPressed += PassiveHeaderPointer;
                background.PointerReleased += PassiveHeaderPointer;
                background.Tapped += (sender, args) =>
                {
                    if (!collapsibleCards && !IsCopyButtonSource(args.OriginalSource as DependencyObject, header)) args.Handled = true;
                };
            }
        }
    }

    private static bool IsCopyButtonSource(DependencyObject source, DependencyObject header)
    {
        while (source != null && source != header)
        {
            if (source is Button) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void PassiveHeaderPointer(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (!collapsibleCards && !IsCopyButtonSource(args.OriginalSource as DependencyObject, (DependencyObject)sender)) args.Handled = true;
    }

    private async void CollapsibleCardsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!settingsReady) return;
        collapsibleCards = CollapsibleCardsToggle.IsOn;
        ApplyCardBehavior();
        await SaveSettingsAsync();
    }

    private void ApplyLegalVisibility() => LegalExpander.Visibility = ShowLegalToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;

    private async void ShowLegalToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!settingsReady) return;
        ApplyLegalVisibility();
        await SaveSettingsAsync();
    }

    private async void RememberCardsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!settingsReady) return;
        rememberCards = RememberCardsToggle.IsOn;
        await SaveSettingsAsync();
    }

    private async void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!settingsReady) return;
        selectedTheme = (string)((RadioButton)sender).Tag;
        ApplyTheme();
        await SaveSettingsAsync();
    }

    private void ApplyTheme()
    {
        RequestedTheme = selectedTheme == "Light" ? ElementTheme.Light : selectedTheme == "Dark" ? ElementTheme.Dark : ElementTheme.Default;
        if (Window.Current.Content is FrameworkElement root) root.RequestedTheme = RequestedTheme;
        RefreshThemeChrome();
    }

    private void RefreshThemeChrome()
    {
        var foreground = ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        if (shape != null)
        {
            shape.FillBrush.Dispose();
            shape.FillBrush = Window.Current.Compositor.CreateColorBrush(foreground);
        }
        var titleBar = ApplicationView.GetForCurrentView().TitleBar;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = ActualTheme == ElementTheme.Dark ? Colors.Gray : Colors.DimGray;
        titleBar.ButtonHoverBackgroundColor = ActualTheme == ElementTheme.Dark ? Color.FromArgb(24,255,255,255) : Color.FromArgb(12,0,0,0);
        titleBar.ButtonPressedBackgroundColor = ActualTheme == ElementTheme.Dark ? Color.FromArgb(16,255,255,255) : Color.FromArgb(8,0,0,0);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        WindowsBrand.Visibility = SpecificationsView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = SettingsBackButton.Visibility = Visibility.Visible;
        SettingsButton.Opacity = 0;
        SettingsButton.IsHitTestVisible = false;
        SettingsButton.IsTabStop = false;
        MoreInfoButton.Visibility = Visibility.Collapsed;
        AppTitle.Margin = new Thickness(48, 0, 0, 0);
        Window.Current.SetTitleBar(AppTitle);
        SettingsBackButton.Focus(FocusState.Programmatic);
    }

    private void SettingsBackButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsView.Visibility = SettingsBackButton.Visibility = Visibility.Collapsed;
        WindowsBrand.Visibility = SpecificationsView.Visibility = Visibility.Visible;
        DeviceCopyButton.Focus(FocusState.Pointer);
        VisualStateManager.GoToState(SettingsButton, "Normal", false);
        SettingsButton.IsHitTestVisible = true;
        SettingsButton.IsTabStop = true;
        SettingsButton.Opacity = 1;
        MoreInfoButton.Visibility = Visibility.Visible;
        AppTitle.Margin = (Thickness)Application.Current.Resources["TitleBarLeftMargin"];
        Window.Current.SetTitleBar(TitleBar);
        DeviceCopyButton.Focus(FocusState.Pointer);
    }

    private void CoreTitleBar_IsVisibleChanged(CoreApplicationViewTitleBar sender, object args)
    {
        TitleBar.Visibility = sender.IsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CoreTitleBar_LayoutMetricsChanged(CoreApplicationViewTitleBar sender, object args)
    {
        UpdateTitleBarLayout(sender);
    }

    private void Current_Activated(object sender, Windows.UI.Core.WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == Windows.UI.Core.CoreWindowActivationState.Deactivated)
            AppTitle.Style = (Style)Application.Current.Resources["InactivatedAppTitle"];
        else
            AppTitle.Style = (Style)Application.Current.Resources["ActivatedAppTitle"];
    }

    private void UpdateTitleBarLayout(CoreApplicationViewTitleBar coreTitleBar)
    {
        // Update title bar control size as needed to account for system size changes.
        TitleBar.Height = coreTitleBar.Height;

        // Ensure the custom title bar does not overlap window caption controls
        Thickness currMargin = TitleBar.Margin;
        TitleBar.Margin = new Thickness(currMargin.Left, currMargin.Top, coreTitleBar.SystemOverlayRightInset, currMargin.Bottom);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (isCopying)
            return;

        isCopying = true;
        DataPackage package = new DataPackage();

        Dictionary<string, string> data = new Dictionary<string, string>()
        {
            { resourceLoader.GetString("Edition/Text"), Edition.Text },
            { resourceLoader.GetString("Version/Text"), Version.Text },
            { resourceLoader.GetString("InstalledOn/Text"), InstalledOn.Text },
            { resourceLoader.GetString("OSBuild/Text"), Build.Text },
            { resourceLoader.GetString("Experience/Text"), Experience.Text },
        };

        if (Expiration.Visibility == Visibility.Visible)
            data.Add(resourceLoader.GetString("Expiration/Text"), Expiration.Text);

        int maxLength = data.Keys.Max(f => f.Length + 5);

        var lines = data.Select(f => string.Format($"{{0,-{maxLength}}}", f.Key) + f.Value);

        string targetText = string.Join(Environment.NewLine, lines);

        package.SetText(targetText);
        Clipboard.SetContent(package);
        CopyToClipboardSuccessAnimation.Begin();
        CopyToClipboardSuccessAnimation.Completed += CopyToClipboardSuccessAnimation_Completed;
    }

    private void CopyToClipboardSuccessAnimation_Completed(object sender, object e)
    {
        isCopying = false;
        CopyToClipboardSuccessAnimation.Completed -= CopyToClipboardSuccessAnimation_Completed;
    }

    private void SetTitleBarBackground() => RefreshThemeChrome();

    public void Initialize()
    {
        if (settingsReady) return;
        LoadSettings();
        string deviceFamilyVersion = AnalyticsInfo.VersionInfo.DeviceFamilyVersion;
        ulong version = ulong.Parse(deviceFamilyVersion);
        ulong build = (version & 0x00000000FFFF0000L) >> 16;
        var revision = version & 0x000000000000FFFF;

        OSName = build >= 21996 ? "Windows11" : "Windows10";
        UpdateWindowsBrand();
        SetTitleBarBackground();

        _uiSettings.ColorValuesChanged += async (uiSettings, _) =>
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                SetTitleBarBackground();
            });

        };

        LoadDeviceInfo();


        Build.Text = build.ToString();

        if (revision != 0)
            Build.Text += $".{revision}";

        string productName = "";

        if (AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Desktop")
            productName = WinbrandHelper.GetWinbrand();
        else
            productName = RegistryHelper.GetInfoString("ProductName");

        Edition.Text = productName;
        LicensingText.Text = resourceLoader.GetString("Trademark/Text").Replace("Windows", productName);

        var displayVersion = RegistryHelper.GetInfoString("DisplayVersion");
        if (string.IsNullOrEmpty(displayVersion))
            displayVersion = RegistryHelper.GetInfoString("ReleaseId");

        Version.Text = displayVersion;

        var userCulture = CultureInfoHelper.GetCurrentCulture();
        InstalledOn.Text = GetWindowsInstallationDateTime().ToString("d", userCulture);


        DateTime? expiration = ExpirationHelper.GetSystemExpiration();
        if (expiration != null)
            Expiration.Text = expiration.Value.ToString("g", userCulture);
        else
        {
            Expiration.Visibility = Visibility.Collapsed;
            ExpirationLabel.Visibility = Visibility.Collapsed;
        }

        var ownerName = RegistryHelper.GetInfoString("RegisteredOwner");
        if (ownerName != null)
            OwnerText.Text = ownerName;
        OwnerText.Visibility = string.IsNullOrEmpty(ownerName) ? Visibility.Collapsed : Visibility.Visible;

        var ownerOrg = RegistryHelper.GetInfoString("RegisteredOrganization");
        if (ownerOrg != null)
            OrgText.Text = ownerOrg;
        OrgText.Visibility = string.IsNullOrEmpty(ownerOrg) ? Visibility.Collapsed : Visibility.Visible;

        if (string.IsNullOrEmpty(ownerName) && string.IsNullOrEmpty(ownerOrg))
            LicenseTo.Visibility = Visibility.Collapsed;

    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    private static ulong GetInstalledMemoryInKilobytes()
    {
        if (GetPhysicallyInstalledSystemMemory(out ulong memory) && memory > 0)
            return memory;

        // The SMBIOS cache is readable in an app container, unlike the firmware API.
        var bytes = RegistryHelper.GetInfoBinary("SMBiosData",
            @"\Registry\Machine\SYSTEM\CurrentControlSet\Services\mssmbios\Data");
        if (bytes == null || bytes.Length < 8)
            return 0;
        int end = (int)Math.Min((ulong)bytes.Length, 8UL + BitConverter.ToUInt32(bytes, 4));
        for (int offset = 8; offset + 4 <= end;)
        {
            byte type = bytes[offset];
            int length = bytes[offset + 1];
            if (length < 4 || offset + length > end)
                break;
            if (type == 17 && length >= 14)
            {
                ushort size = BitConverter.ToUInt16(bytes, offset + 12);
                if (size == 0x7fff && length >= 32)
                    memory += (BitConverter.ToUInt32(bytes, offset + 28) & 0x7fffffffUL) * 1024UL;
                else if (size != 0xffff)
                    memory += (size & 0x8000) != 0 ? (ulong)(size & 0x7fff) : (ulong)size * 1024UL;
            }
            if (type == 127)
                break;
            offset += length;
            while (offset + 1 < end && (bytes[offset] != 0 || bytes[offset + 1] != 0))
                offset++;
            offset += 2;
        }
        return memory;
    }

    private void LoadDeviceInfo()
    {
        string unavailable = resourceLoader.GetString("DeviceUnavailable");
        string Read(string value, string path) => RegistryHelper.GetInfoString(value, path) ?? unavailable;
        DeviceName.Text = RegistryHelper.GetInfoString("Hostname", @"\Registry\Machine\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters")
            ?? new Windows.Security.ExchangeActiveSyncProvisioning.EasClientDeviceInformation().FriendlyName;
        var culture = CultureInfoHelper.GetCurrentCulture();
        var adapters = DeviceInfoHelper.GetGraphicsAdapters();
        GraphicsCard.Text = adapters.Count == 0 ? unavailable : string.Join(Environment.NewLine, adapters.Select(adapter =>
            string.Format(resourceLoader.GetString("DeviceGraphicsFormat"), adapter.Name, (adapter.Memory / 1048576UL).ToString("N0", culture))));
        var storage = DeviceInfoHelper.GetStorage();
        Storage.Text = storage.Total == 0 ? unavailable : string.Format(resourceLoader.GetString("DeviceStorageFormat"),
            (storage.Total / 1073741824.0).ToString("N0", culture));
        Experience.Text = DeviceInfoHelper.GetExperienceVersion() ?? unavailable;
        Processor.Text = Read("ProcessorNameString", @"\Registry\Machine\HARDWARE\DESCRIPTION\System\CentralProcessor\0").Trim();
        ulong memory = GetInstalledMemoryInKilobytes();
        InstalledRam.Text = memory > 0
            ? string.Format(resourceLoader.GetString("DeviceMemoryFormat"), (memory / 1048576.0).ToString("N0", CultureInfoHelper.GetCurrentCulture()))
            : unavailable;
        DeviceId.Text = Read("MachineId", @"\Registry\Machine\SOFTWARE\Microsoft\SQMClient").Trim('{', '}').ToUpperInvariant();

    }

    public async Task RefreshStorageAsync()
    {
        if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.ApplicationModel.FullTrustProcessLauncher")) return;
        var requested = DateTime.UtcNow.AddSeconds(-1);
        try
        {
            await Windows.ApplicationModel.FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
            for (int attempt = 0; attempt < 50; attempt++)
            {
                await Task.Delay(100);
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync("storage-inventory.json") as StorageFile;
                if (file == null) continue;
                if (!JsonObject.TryParse(await FileIO.ReadTextAsync(file), out var inventory)) continue;
                if (!DateTime.TryParse(inventory.GetNamedString("CapturedUtc", ""), null, System.Globalization.DateTimeStyles.RoundtripKind, out var captured) || captured < requested) continue;
                double total = inventory.GetNamedNumber("Total", 0);
                if (total > 0)
                {
                    var culture = CultureInfoHelper.GetCurrentCulture();
                    Storage.Text = string.Format(resourceLoader.GetString("DeviceStorageFormat"),
                        (total / 1073741824).ToString("N0", culture));
                }
                break;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private void DeviceCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (isDeviceCopying) return;
        var fields = new[]
        {
            ("DeviceNameLabel", DeviceName.Text), ("ProcessorLabel", Processor.Text),
            ("InstalledRamLabel", InstalledRam.Text), ("GraphicsCardLabel", GraphicsCard.Text),
            ("StorageLabel", Storage.Text), ("DeviceIdLabel", DeviceId.Text)
        };
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, fields.Select(f => resourceLoader.GetString(f.Item1 + "/Text") + "\t" + f.Item2)));
        Clipboard.SetContent(package);
        isDeviceCopying = true;
        DeviceCopyToClipboardSuccessAnimation.Completed += DeviceCopyAnimation_Completed;
        DeviceCopyToClipboardSuccessAnimation.Begin();
    }

    private void DeviceCopyAnimation_Completed(object sender, object e)
    {
        isDeviceCopying = false;
        DeviceCopyToClipboardSuccessAnimation.Completed -= DeviceCopyAnimation_Completed;
    }

    private void UpdateWindowsBrand()
    {
        if (Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.Composition.CompositionShape"))
        {
            using FirstDisposableTuple<CanvasPathBuilder, float, float> path = OSName == "Windows11" ? OSPathsHelper.GetWindows11Path() : OSPathsHelper.GetWindows10Path();
            using CanvasGeometry canvasGeo = CanvasGeometry.CreatePath(path.Item1);
            CompositionPath compPath = new(canvasGeo);
            var compositor = Window.Current.Compositor;
            var compGeo = compositor.CreatePathGeometry(compPath);
            shape = compositor.CreateSpriteShape(compGeo);
            shape.FillBrush = compositor.CreateColorBrush(ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black);
            var shapeVisual = compositor.CreateShapeVisual();
            shapeVisual.Shapes.Add(shape);
            shapeVisual.Size = new(path.Item2, path.Item3);
            Windows.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(CompatibleCanvas, shapeVisual);
            CompatibleCanvas.Width = path.Item2;
            CompatibleCanvas.Height = path.Item3;
        }
        else
        {
            string path = (string)Application.Current.Resources[$"{OSName}Path"];
            var geo = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path);
            NonCompatiblePath.Data = geo;
        }
    }

    private async void MoreInfoButton_Click(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:about"));
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsView.Visibility == Visibility.Visible)
        {
            SettingsBackButton_Click(sender, e);
            return;
        }
        _ = ApplicationView.GetForCurrentView().TryConsolidateAsync();
    }

    private DateTime GetWindowsInstallationDateTime()
    {
        var seconds = RegistryHelper.GetInfoDWord("InstallDate");
        DateTime installDate = DateTimeOffset.FromUnixTimeSeconds(seconds.Value).LocalDateTime;
        return installDate;
    }

    private async void Expander_Collapsed(Microsoft.UI.Xaml.Controls.Expander sender, Microsoft.UI.Xaml.Controls.ExpanderCollapsedEventArgs args)
    {
        if (!collapsibleCards) { sender.IsExpanded = true; return; }
        if (rememberCards) await SaveSettingsAsync();
    }

    private async void Expander_Expanding(Microsoft.UI.Xaml.Controls.Expander sender, Microsoft.UI.Xaml.Controls.ExpanderExpandingEventArgs args)
    {
        if (!collapsibleCards) { sender.IsExpanded = true; return; }
        if (rememberCards) await SaveSettingsAsync();
    }
}