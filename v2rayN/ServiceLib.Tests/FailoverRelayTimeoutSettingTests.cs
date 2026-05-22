using System.Reflection;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverRelayTimeoutSettingTests
{
    [Fact]
    public void FailoverRelayOptions_DefaultsUseConfigurableFirstByteTimeoutBudget()
    {
        var options = new FailoverRelayOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(4000), options.CandidateFirstByteTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), options.CandidateSecondRoundFirstByteTimeout);
    }

    [Theory]
    [InlineData(0, 4000)]
    [InlineData(-1, 4000)]
    [InlineData(1499, 4000)]
    [InlineData(1500, 1500)]
    [InlineData(2500, 2500)]
    [InlineData(4200, 4200)]
    public void NormalizeFirstByteTimeoutMs_ReturnsUsableValue(int input, int expected)
    {
        Assert.Equal(expected, FailoverRelayOptions.NormalizeFirstByteTimeoutMs(input));
    }

    [Fact]
    public void LoadConfig_NormalizesMissingOrInvalidFailoverRelayFirstByteTimeout()
    {
        var configFile = Utils.GetConfigPath(Global.ConfigFileName);
        var backupFile = File.Exists(configFile)
            ? Path.Combine(Path.GetDirectoryName(configFile)!, $"{Path.GetFileName(configFile)}.test-backup")
            : null;
        if (backupFile is not null)
        {
            File.Copy(configFile, backupFile, true);
        }

        try
        {
            File.WriteAllText(configFile, """{"GuiItem":{"FailoverRelayFirstByteTimeoutMs":1499}}""");

            var config = ConfigHandler.LoadConfig();

            Assert.NotNull(config);
            Assert.Equal(4000, config!.GuiItem.FailoverRelayFirstByteTimeoutMs);
        }
        finally
        {
            if (backupFile is not null)
            {
                File.Copy(backupFile, configFile, true);
                File.Delete(backupFile);
            }
            else if (File.Exists(configFile))
            {
                File.Delete(configFile);
            }
        }
    }

    [Theory]
    [InlineData("", "4000")]
    [InlineData("abc", "4000")]
    [InlineData("1499", "4000")]
    [InlineData("1500", "1500")]
    [InlineData("2500", "2500")]
    public void OptionSettingViewModel_NormalizesFailoverRelayFirstByteTimeoutText(string input, string expected)
    {
        var previousConfig = SetAppManagerConfig(CreateConfig());
        try
        {
            var viewModel = new OptionSettingViewModel((_, _) => Task.FromResult(true))
            {
                FailoverRelayFirstByteTimeoutMs = input
            };

            viewModel.NormalizeFailoverRelayFirstByteTimeoutMs();

            Assert.Equal(expected, viewModel.FailoverRelayFirstByteTimeoutMs);
        }
        finally
        {
            if (previousConfig is not null)
            {
                SetAppManagerConfig(previousConfig);
            }
        }
    }

    private static Config CreateConfig()
    {
        return new Config
        {
            Inbound =
            [
                new InItem
                {
                    Protocol = EInboundProtocol.socks.ToString(),
                    LocalPort = 10808,
                    UdpEnabled = true,
                    SniffingEnabled = true,
                },
            ],
            CoreBasicItem = new(),
            TunModeItem = new(),
            KcpItem = new(),
            GrpcItem = new(),
            RoutingBasicItem = new() { DomainStrategy = Global.DomainStrategies.First() },
            GuiItem = new() { FailoverRelayFirstByteTimeoutMs = 2500 },
            MsgUIItem = new(),
            UiItem = new() { CurrentLanguage = Global.Languages.First(), MainColumnItem = [], WindowSizeItem = [] },
            ConstItem = new(),
            SpeedTestItem = new(),
            Mux4RayItem = new(),
            Mux4SboxItem = new(),
            HysteriaItem = new(),
            ClashUIItem = new(),
            SystemProxyItem = new(),
            WebDavItem = new(),
            CheckUpdateItem = new(),
            GlobalHotkeys = [],
            CoreTypeItem = [],
            SimpleDNSItem = new(),
        };
    }

    private static Config? SetAppManagerConfig(Config config)
    {
        var field = typeof(AppManager).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AppManager._config field not found");
        var previous = field.GetValue(AppManager.Instance) as Config;
        field.SetValue(AppManager.Instance, config);
        return previous;
    }
}
