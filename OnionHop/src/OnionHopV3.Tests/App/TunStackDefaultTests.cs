using OnionHopV3.App.ViewModels;
using Xunit;

namespace OnionHopV3.Tests.App;

/// <summary>
/// Linux's default TUN stack became gVisor, because ufw and firewalld drop what the Mixed and System
/// stacks send through the tunnel interface (#97). Settings saved before that carry "Mixed" for
/// everyone, so that one is switched once; a "Mixed" saved afterwards is the user's choice.
/// </summary>
public sealed class TunStackDefaultTests
{
    [Fact]
    public void A_Mixed_saved_under_the_old_default_is_switched_on_Linux()
    {
        Assert.True(AppStateViewModel.IsOldLinuxTunStackDefault(null, AppStateViewModel.TunStackMixed, linux: true));
        Assert.True(AppStateViewModel.IsOldLinuxTunStackDefault(0, AppStateViewModel.TunStackMixed, linux: true));
    }

    [Fact]
    public void A_Mixed_chosen_after_the_change_stays()
    {
        Assert.False(AppStateViewModel.IsOldLinuxTunStackDefault(1, AppStateViewModel.TunStackMixed, linux: true));
    }

    [Fact]
    public void Other_stacks_and_other_systems_are_left_alone()
    {
        Assert.False(AppStateViewModel.IsOldLinuxTunStackDefault(null, AppStateViewModel.TunStackSystem, linux: true));
        Assert.False(AppStateViewModel.IsOldLinuxTunStackDefault(null, AppStateViewModel.TunStackGvisor, linux: true));
        Assert.False(AppStateViewModel.IsOldLinuxTunStackDefault(null, AppStateViewModel.TunStackMixed, linux: false));
    }
}
