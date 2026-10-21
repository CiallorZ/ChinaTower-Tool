using LinkForge.Application.Assets;

namespace LinkForge.Application.Tests.Assets;

/// <summary>
/// 资产凭据引用键的校验与归一化（LF-004）。
/// </summary>
/// <remarks>
/// 这里的用例大多在守“明文密码不得进入模型”这条边界：
/// 一个真实密码几乎必然含有白名单之外的字符，
/// 因此这些用例不是苛刻，而是恰好能拦住误粘贴。
/// </remarks>
public class AssetCredentialReferenceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Blank_should_be_treated_as_not_configured(string? key)
    {
        AssetCredentialReference.IsValidKey(key).ShouldBeTrue("空白表示不配置凭据，不是错误。");
        AssetCredentialReference.Normalize(key).ShouldBeNull();
    }

    [Theory]
    [InlineData("asset.cam-01.password")]
    [InlineData("cam01")]
    [InlineData("CAM_01")]
    [InlineData("a.b_c-d:e")]
    [InlineData("vault:tower:camera")]
    public void Whitelist_keys_should_be_accepted(string key)
    {
        AssetCredentialReference.IsValidKey(key).ShouldBeTrue();
        AssetCredentialReference.Normalize(key).ShouldBe(key);
    }

    [Theory]
    // 真实密码的典型字符：这些一出现就说明用户贴的是密码而不是引用键。
    [InlineData("p@ssw0rd")]
    [InlineData("hunter2!")]
    [InlineData("a b")]
    [InlineData("密码")]
    [InlineData("a/b")]
    [InlineData("a=b")]
    [InlineData("a#b")]
    // 路径分隔符与斜杠组合最像“把 vault 路径粘进来了”，同样不是引用键。
    [InlineData("vault/tower/camera")]
    [InlineData("user:pa:ss word")]
    public void Non_whitelist_keys_should_be_rejected(string key)
    {
        AssetCredentialReference.IsValidKey(key).ShouldBeFalse(
            $"“{key}” 含白名单之外的字符，应当被当作明文密码拒绝。");
    }

    [Fact]
    public void Key_longer_than_the_limit_should_be_rejected()
    {
        var tooLong = new string('a', AssetCredentialReference.MaxKeyLength + 1);
        var atLimit = new string('a', AssetCredentialReference.MaxKeyLength);

        AssetCredentialReference.IsValidKey(atLimit).ShouldBeTrue("正好等于上限应放行。");
        AssetCredentialReference.IsValidKey(tooLong).ShouldBeFalse();
    }

    [Fact]
    public void Surrounding_whitespace_should_be_trimmed_before_validation()
    {
        // 粘贴引用键时几乎总会带上空白；合法引用键不该因此被拒。
        AssetCredentialReference.IsValidKey("  asset.cam-01.password  ").ShouldBeTrue();
        AssetCredentialReference.Normalize("  asset.cam-01.password  ")
            .ShouldBe("asset.cam-01.password");
    }

    [Fact]
    public void Whitespace_padded_password_should_still_be_rejected()
    {
        AssetCredentialReference.IsValidKey("  p@ssw0rd  ").ShouldBeFalse();
    }

    [Fact]
    public void Invalid_reason_should_be_actionable_and_not_echo_the_secret()
    {
        var reason = AssetCredentialReference.InvalidReason();

        reason.ShouldNotBeNullOrWhiteSpace();
        reason.Contains("引用键", StringComparison.Ordinal).ShouldBeTrue();
        reason.Contains("凭据存储", StringComparison.Ordinal).ShouldBeTrue();
    }
}
