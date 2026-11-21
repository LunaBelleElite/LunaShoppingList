using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void ExclusionState_TogglesItem()
    {
        var state = new ExclusionState();
        state.SetExcluded(5, true);
        Assert.True(state.IsExcluded(5));
        state.SetExcluded(5, false);
        Assert.False(state.IsExcluded(5));
    }
}
