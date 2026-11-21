using ConnectDesktop.Core.Protocol;
using Xunit;

namespace ConnectDesktop.Core.Tests;

public sealed class ProtocolFeaturesTests
{
    [Theory]
    [InlineData("1.0.4", true)]
    [InlineData("1.0.4+abc", true)]
    [InlineData("1.0.3", false)]
    [InlineData("", false)]
    [InlineData("lixo", false)]
    [InlineData("1.1", true)]
    public void Supports_do_drag_and_drop(string remoteVersion, bool expected)
    {
        Assert.Equal(expected, ProtocolFeatures.Supports(remoteVersion, ProtocolFeatures.DragAndDrop));
    }

    [Fact]
    public void Supports_com_null_retorna_false()
    {
        Assert.False(ProtocolFeatures.Supports(null, ProtocolFeatures.DragAndDrop));
    }

    [Fact]
    public void DragAndDrop_e_1_0_4()
    {
        Assert.Equal(new Version(1, 0, 4), ProtocolFeatures.DragAndDrop);
    }
}
