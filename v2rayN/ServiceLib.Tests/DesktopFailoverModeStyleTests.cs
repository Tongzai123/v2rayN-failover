using System.Xml.Linq;
using Xunit;

namespace ServiceLib.Tests;

public class DesktopFailoverModeStyleTests
{
    [Fact]
    public void FailoverModeSegmentsUsePaintedBackgroundsInsteadOfBorderlessTheme()
    {
        var styles = LoadGlobalStyles();
        var segmentStyle = FindStyle(styles, "Button.FailoverModeSegment");
        var offActiveStyle = FindStyle(styles, "Button.FailoverModeSegment.OffMode.active");
        var activeModeStyle = FindStyle(styles, "Button.FailoverModeSegment.ActiveMode.active");

        Assert.DoesNotContain(
            segmentStyle.Elements(styles.Root!.Name.Namespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Theme");
        Assert.Contains(
            segmentStyle.Elements(styles.Root.Name.Namespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Background"
                && (string?)setter.Attribute("Value") == "Transparent");
        Assert.Contains(
            offActiveStyle.Elements(styles.Root.Name.Namespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Background"
                && (string?)setter.Attribute("Value") == "#2878D7");
        Assert.Contains(
            activeModeStyle.Elements(styles.Root.Name.Namespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Background"
                && (string?)setter.Attribute("Value") == "#18A672");
    }

    [Fact]
    public void ProfilesViewDisplaysFailoverGroupRemarkAsGreenTagWithoutFixedLabel()
    {
        var profilesView = LoadDesktopView("ProfilesView.axaml");
        var ns = profilesView.Root!.Name.Namespace;
        var listBox = profilesView.Descendants(ns + "ListBox")
            .Single(element => (string?)element.Attribute("Name") == "lstGroup"
                || (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "lstGroup");
        var template = listBox.Descendants(ns + "DataTemplate").First();

        Assert.DoesNotContain(
            template.Descendants(ns + "Label"),
            label => (string?)label.Attribute("Content") == "{x:Static resx:ResUI.TbFailoverGroup}");
        Assert.Contains(
            template.Descendants(ns + "Label"),
            label => (string?)label.Attribute("Content") == "{Binding Remarks}"
                && ((string?)label.Attribute("Classes"))?.Contains("Solid Green", StringComparison.Ordinal) == true
                && (string?)label.Attribute("IsVisible") == "{Binding IsFailoverGroup}");
        Assert.Contains(
            template.Descendants(ns + "TextBlock"),
            textBlock => (string?)textBlock.Attribute("Text") == "{Binding Remarks}"
                && (string?)textBlock.Attribute("IsVisible") == "{Binding !IsFailoverGroup}");
    }

    [Theory]
    [InlineData("TbFailoverHealthNormal", "Solid Green")]
    [InlineData("TbFailoverHealthFailed", "Solid Red")]
    [InlineData("TbFailoverHealthProbing", "Solid Orange")]
    [InlineData("TbFailoverHealthRequesting", "Solid Blue")]
    [InlineData("TbFailoverHealthDegraded", "Solid Orange")]
    [InlineData("TbFailoverHealthFallback", "Solid Blue")]
    [InlineData("TbFailoverHealthUnknown", "Solid Grey")]
    public void ProfilesViewUsesDistinctFailoverHealthTagColors(string resourceName, string expectedClasses)
    {
        var profilesView = LoadDesktopView("ProfilesView.axaml");
        var ns = profilesView.Root!.Name.Namespace;

        Assert.Contains(
            profilesView.Descendants(ns + "Label"),
            label => (string?)label.Attribute("Content") == $"{{x:Static resx:ResUI.{resourceName}}}"
                && (string?)label.Attribute("Classes") == expectedClasses
                && (string?)label.Attribute("Theme") == "{DynamicResource TagLabel}");
    }

    [Fact]
    public void ProfilesViewUsesBlueActiveTagForCurrentFailoverNode()
    {
        var profilesView = LoadDesktopView("ProfilesView.axaml");
        var ns = profilesView.Root!.Name.Namespace;

        Assert.Contains(
                profilesView.Descendants(ns + "Label"),
            label => (string?)label.Attribute("Content") == "{x:Static resx:ResUI.TipActiveServer}"
                && (string?)label.Attribute("Classes") == "Solid Blue"
                && (string?)label.Attribute("IsVisible") == "{Binding ShowCurrentFailoverActiveLabel}"
                && (string?)label.Attribute("Theme") == "{DynamicResource TagLabel}");
    }

    [Fact]
    public void ProfilesViewUsesOrangePendingActiveTagForOriginalNormalNode()
    {
        var profilesView = LoadDesktopView("ProfilesView.axaml");
        var ns = profilesView.Root!.Name.Namespace;

        Assert.Contains(
            profilesView.Descendants(ns + "Label"),
            label => (string?)label.Attribute("Content") == "{x:Static resx:ResUI.TbPendingActiveServer}"
                && (string?)label.Attribute("Classes") == "Solid Orange"
                && (string?)label.Attribute("IsVisible") == "{Binding ShowPendingActiveLabel}"
                && (string?)label.Attribute("Theme") == "{DynamicResource TagLabel}");
    }

    [Fact]
    public void MsgViewUsesGreenActiveGroupTagOnlyWhenFailoverModeIsEnabled()
    {
        var msgView = LoadDesktopView("MsgView.axaml");
        var ns = msgView.Root!.Name.Namespace;

        Assert.Equal(
            "vms:MsgViewModel",
            (string?)msgView.Root.Attribute(XName.Get("DataType", "http://schemas.microsoft.com/winfx/2006/xaml")));
        Assert.Contains(
            msgView.Descendants(ns + "Label"),
            label => ((string?)label.Attribute("Name") == "lblActiveFailoverGroup"
                    || (string?)label.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "lblActiveFailoverGroup")
                && ((string?)label.Attribute("Classes"))?.Contains("Solid Green", StringComparison.Ordinal) == true
                && (string?)label.Attribute("Theme") == "{DynamicResource TagLabel}"
                && (string?)label.Attribute("IsVisible") == "{Binding ShowActiveFailoverGroupTag}");
        Assert.Contains(
            msgView.Descendants(ns + "TextBlock"),
            textBlock => ((string?)textBlock.Attribute("Name") == "txtActiveFailoverGroup"
                    || (string?)textBlock.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "txtActiveFailoverGroup")
                && (string?)textBlock.Attribute("IsVisible") == "{Binding !ShowActiveFailoverGroupTag}");
    }

    [Fact]
    public void MsgViewDisplaysFailoverCoreCheckResultTagBesideActiveGroup()
    {
        var msgView = LoadDesktopView("MsgView.axaml");
        var ns = msgView.Root!.Name.Namespace;

        Assert.Contains(
            msgView.Descendants(ns + "Label"),
            label => ((string?)label.Attribute("Name") == "lblFailoverCoreCheckResult"
                    || (string?)label.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "lblFailoverCoreCheckResult")
                && (string?)label.Attribute("Content") == "{Binding FailoverCoreCheckResultText}"
                && (string?)label.Attribute("IsVisible") == "{Binding ShowFailoverCoreCheckResult}"
                && (string?)label.Attribute("Theme") == "{DynamicResource TagLabel}");
    }

    [Fact]
    public void MainWindowPlacesCheckCoreMenuAfterExitMenu()
    {
        var mainWindow = LoadDesktopView("MainWindow.axaml");
        var ns = mainWindow.Root!.Name.Namespace;
        var menuItems = mainWindow.Descendants(ns + "MenuItem").ToList();
        var exitIndex = menuItems.FindIndex(item =>
            (string?)item.Attribute("Name") == "menuClose"
            || (string?)item.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "menuClose");
        var checkIndex = menuItems.FindIndex(item =>
            (string?)item.Attribute("Name") == "menuCheckFailoverCore"
            || (string?)item.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "menuCheckFailoverCore");

        Assert.True(exitIndex >= 0);
        Assert.True(checkIndex > exitIndex);
        Assert.Equal("{x:Static resx:ResUI.menuCheckFailoverCore}", (string?)menuItems[checkIndex].Attribute("Header"));
    }

    [Fact]
    public void FailoverGroupResourceTextUsesTransferGroupWording()
    {
        var zhHans = LoadResource("ResUI.zh-Hans.resx");
        var invariant = LoadResource("ResUI.resx");

        Assert.Equal("转移分组", GetResourceValue(zhHans, "TbFailoverGroup"));
        Assert.Equal("复制到转移分组", GetResourceValue(zhHans, "menuCopyToFailoverGroup"));
        Assert.Equal("先激活转移分组。", GetResourceValue(zhHans, "MsgActivateFailoverGroupFirst"));
        Assert.Equal("Transfer group", GetResourceValue(invariant, "TbFailoverGroup"));
        Assert.Equal("Copy to transfer group", GetResourceValue(invariant, "menuCopyToFailoverGroup"));
        Assert.Equal("Activate transfer group first.", GetResourceValue(invariant, "MsgActivateFailoverGroupFirst"));
    }

    [Fact]
    public void FailoverCoreCheckResourceTextExists()
    {
        var zhHans = LoadResource("ResUI.zh-Hans.resx");
        var invariant = LoadResource("ResUI.resx");

        Assert.Equal("检测核心", GetResourceValue(zhHans, "menuCheckFailoverCore"));
        Assert.Equal("检测中", GetResourceValue(zhHans, "TbFailoverCoreChecking"));
        Assert.Equal("同核心Xray", GetResourceValue(zhHans, "TbFailoverCoreSameXray"));
        Assert.Equal("同核心sing-box", GetResourceValue(zhHans, "TbFailoverCoreSameSingBox"));
        Assert.Equal("混合核心", GetResourceValue(zhHans, "TbFailoverCoreMixed"));
        Assert.Equal("无故障队列", GetResourceValue(zhHans, "TbFailoverCoreEmpty"));
        Assert.Equal("Check core", GetResourceValue(invariant, "menuCheckFailoverCore"));
    }

    [Fact]
    public void DesktopProjectDoesNotExplicitlyDuplicateDefaultAxamlResources()
    {
        var project = LoadRepositoryXml("v2rayN", "v2rayN.Desktop", "v2rayN.Desktop.csproj");
        var explicitAvaloniaResources = project.Root!
            .Descendants("AvaloniaResource")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => value != null)
            .ToList();

        Assert.DoesNotContain(@"Assets\**", explicitAvaloniaResources);
    }

    [Fact]
    public void DesktopProjectStillIncludesNonXamlAvaloniaAssets()
    {
        var project = LoadRepositoryXml("v2rayN", "v2rayN.Desktop", "v2rayN.Desktop.csproj");
        var explicitAvaloniaResources = project.Root!
            .Descendants("AvaloniaResource")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => value != null)
            .ToList();

        Assert.Contains(@"Assets\*.ico", explicitAvaloniaResources);
        Assert.Contains(@"Assets\Fonts\**", explicitAvaloniaResources);
    }

    private static XDocument LoadGlobalStyles()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(
                directory.FullName,
                "v2rayN",
                "v2rayN.Desktop",
                "Assets",
                "GlobalStyles.axaml");
            if (File.Exists(path))
            {
                return XDocument.Load(path);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate v2rayN/v2rayN.Desktop/Assets/GlobalStyles.axaml.");
    }

    private static XDocument LoadDesktopView(string fileName)
    {
        return LoadRepositoryXml("v2rayN", "v2rayN.Desktop", "Views", fileName);
    }

    private static XDocument LoadResource(string fileName)
    {
        return LoadRepositoryXml("v2rayN", "ServiceLib", "Resx", fileName);
    }

    private static XDocument LoadRepositoryXml(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(path))
            {
                return XDocument.Load(path);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(pathParts)}.");
    }

    private static XElement FindStyle(XDocument document, string selector)
    {
        var ns = document.Root!.Name.Namespace;
        return document.Root.Elements(ns + "Style")
            .Single(element => (string?)element.Attribute("Selector") == selector);
    }

    private static string? GetResourceValue(XDocument document, string name)
    {
        return document.Root!
            .Elements("data")
            .Single(element => (string?)element.Attribute("name") == name)
            .Element("value")
            ?.Value;
    }
}
