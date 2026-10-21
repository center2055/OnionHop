using OnionHopV3.Core;
using OnionHopV3.Core.Services;
using Xunit;
using Strategy = OnionHopV3.Core.Services.SmartConnectAdvisor.Strategy;

namespace OnionHopV3.Tests.Services;

/// <summary>
/// With Smart Connect on, the CLI's --bridge-type used to change nothing: the plan built its own
/// list (#89). The chosen bridge type now goes first and the plan follows as the fallback.
/// </summary>
public sealed class ChosenBridgeLeadTests
{
    private static Strategy Bridge(string transport) =>
        new($"bridge:{transport}", "plan", new OnionHopConnectOptions { UseTorBridges = true, SelectedBridgeType = transport });

    private static readonly Strategy[] IranPlan =
    [
        Bridge("webtunnel"),
        Bridge("snowflake"),
        Bridge("obfs4"),
        Bridge("meek"),
        new("direct", "plan", new OnionHopConnectOptions())
    ];

    [Fact]
    public void The_chosen_bridge_goes_first_and_is_not_tried_twice()
    {
        var chosen = new OnionHopConnectOptions { SelectedBridgeType = "obfs4", CustomSniHosts = "a.example" };

        var order = SmartConnectAdvisor.LeadWithChosenBridge(IranPlan, chosen);

        Assert.Equal(
            ["bridge:obfs4", "bridge:webtunnel", "bridge:snowflake", "bridge:meek", "direct"],
            order.Select(strategy => strategy.Name));
    }

    [Fact]
    public void The_chosen_attempt_keeps_the_users_options_with_smart_connects_budget()
    {
        var chosen = new OnionHopConnectOptions
        {
            SelectedBridgeType = "snowflake",
            CustomSniHosts = "front.example",
            BridgeSourceMode = OnionHopConnectOptions.BridgeSourceCollectorOnly
        };

        var lead = SmartConnectAdvisor.LeadWithChosenBridge(IranPlan, chosen)[0];

        Assert.True(lead.Options.UseTorBridges);
        Assert.Equal("front.example", lead.Options.CustomSniHosts);
        Assert.Equal(OnionHopConnectOptions.BridgeSourceCollectorOnly, lead.Options.BridgeSourceMode);
        Assert.Equal(SmartConnectAdvisor.BridgeAttemptTimeoutSeconds, lead.Options.SmartConnectAttemptTimeoutSeconds);
    }

    [Fact]
    public void Meek_azure_replaces_the_plans_meek()
    {
        var order = SmartConnectAdvisor.LeadWithChosenBridge(IranPlan, new OnionHopConnectOptions { SelectedBridgeType = "meek-azure" });

        Assert.Equal("bridge:meek-azure", order[0].Name);
        Assert.DoesNotContain(order, strategy => strategy.Name == "bridge:meek");
        Assert.Equal(IranPlan.Length, order.Count);
    }

    [Theory]
    [InlineData("automatic")]
    [InlineData("")]
    public void Automatic_bridges_leave_the_plan_alone(string bridgeType)
    {
        var order = SmartConnectAdvisor.LeadWithChosenBridge(IranPlan, new OnionHopConnectOptions { SelectedBridgeType = bridgeType });

        Assert.Same(IranPlan, order);
    }
}
