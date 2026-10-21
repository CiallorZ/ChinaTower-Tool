using LinkForge.Application.Assets;
using LinkForge.Domain.Assets;

namespace LinkForge.Application.Tests.Assets;

/// <summary>
/// 资产类型的显示名与解析（LF-004）。
/// </summary>
public class AssetKindNamesTests
{
    [Fact]
    public void Every_defined_kind_should_have_a_display_name_and_be_listed()
    {
        // 守卫：新增枚举值时若忘记加显示名，"资产管理"下拉框会少一项且无人察觉。
        foreach (var kind in Enum.GetValues<AssetKind>())
        {
            AssetKindNames.All.ShouldContain(kind);
            AssetKindNames.Display(kind).ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData("Camera", AssetKind.Camera)]
    [InlineData("camera", AssetKind.Camera)]
    [InlineData("摄像机", AssetKind.Camera)]
    [InlineData("摄像头", AssetKind.Camera)]
    [InlineData("IPC", AssetKind.Camera)]
    [InlineData("NVR", AssetKind.Recorder)]
    [InlineData("录像机", AssetKind.Recorder)]
    [InlineData("IoT设备", AssetKind.IotDevice)]
    [InlineData("IoT 设备", AssetKind.IotDevice)]
    [InlineData("MQTT", AssetKind.Broker)]
    [InlineData("服务器", AssetKind.Host)]
    [InlineData("未知", AssetKind.Unknown)]
    public void Known_writings_should_parse(string text, AssetKind expected)
    {
        AssetKindNames.TryParse(text, out var kind).ShouldBeTrue();
        kind.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("摄像头1号")]
    [InlineData("乱七八糟")]
    public void Unknown_writings_should_not_be_guessed(string text)
    {
        // 猜类型会让导入结果与用户的表格不一致，因此只做精确匹配。
        AssetKindNames.TryParse(text, out var kind).ShouldBeFalse();
        kind.ShouldBe(AssetKind.Unknown);
    }

    [Fact]
    public void Unknown_kind_should_display_as_unknown()
    {
        AssetKindNames.Display((AssetKind)999).ShouldBe("未知");
    }
}
