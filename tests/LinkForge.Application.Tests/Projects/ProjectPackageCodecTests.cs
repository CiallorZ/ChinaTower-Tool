using System.Text;
using LinkForge.Application.Projects;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;

namespace LinkForge.Application.Tests.Projects;

/// <summary>项目包 JSON 的严格编解码与秘密边界（LF-015）。</summary>
public class ProjectPackageCodecTests
{
    private readonly ProjectPackageCodec _codec = new();

    [Fact]
    public void Encode_and_decode_should_round_trip_all_non_secret_fields()
    {
        var package = CreatePackage();

        var bytes = _codec.Encode(package.Project, package.Environments, package.Assets);
        var decoded = _codec.Decode(bytes);

        decoded.Project.Name.ShouldBe("东门视频项目");
        decoded.Environments.ShouldHaveSingleItem().Variables["REGION"].ShouldBe("cn-east");
        decoded.Assets.ShouldHaveSingleItem().CredentialReference.ShouldBe("asset.cam-01.password");

        var text = Encoding.UTF8.GetString(bytes);
        text.ShouldContain("\"schema\": \"linkforge.project-package\"");
        text.ShouldContain("\"credentialReference\": \"asset.cam-01.password\"");
        text.ShouldNotContain("\"password\":");
    }

    [Fact]
    public void Unknown_schema_version_should_be_rejected()
    {
        var bytes = EncodeWithRootReplacement("\"schemaVersion\": 1", "\"schemaVersion\": 99");

        var exception = Should.Throw<ProjectPackageFormatException>(() => _codec.Decode(bytes));

        exception.Message.ShouldContain("不支持的项目包版本 99");
    }

    [Fact]
    public void Unknown_fields_should_be_rejected_instead_of_silently_ignored()
    {
        var bytes = EncodeWithRootReplacement(
            "\"project\":",
            "\"unexpected\": true,\n  \"project\":");

        var exception = Should.Throw<ProjectPackageFormatException>(() => _codec.Decode(bytes));

        exception.Message.ShouldContain("unexpected");
    }

    [Theory]
    [InlineData("password")]
    [InlineData("apiToken")]
    [InlineData("clientSecret")]
    [InlineData("accessKey")]
    public void Secret_shaped_fields_should_be_rejected(string fieldName)
    {
        var bytes = EncodeWithRootReplacement(
            "\"project\":",
            $"\"{fieldName}\": \"plaintext-secret\",\n  \"project\":");

        var exception = Should.Throw<ProjectPackageFormatException>(() => _codec.Decode(bytes));

        exception.Message.ShouldContain("看起来承载秘密");
    }

    [Fact]
    public void Multiple_active_environments_should_be_rejected()
    {
        var package = CreatePackage();
        var environments = new[]
        {
            package.Environments[0] with { Id = "env-1", IsActive = true },
            package.Environments[0] with { Id = "env-2", Name = "生产环境", IsActive = true }
        };

        var exception = Should.Throw<ProjectPackageFormatException>(() =>
            ProjectPackageCodec.Validate(package with { Environments = environments }));

        exception.Message.ShouldContain("最多只能有一个激活环境");
    }

    [Fact]
    public void Invalid_credential_value_should_be_rejected_as_a_reference_only()
    {
        var package = CreatePackage();
        var asset = package.Assets[0] with { CredentialReference = "P@ssw0rd with spaces" };

        var exception = Should.Throw<ProjectPackageFormatException>(() =>
            ProjectPackageCodec.Validate(package with { Assets = [asset] }));

        exception.Message.ShouldContain("凭据引用无效");
    }

    [Fact]
    public void Oversized_payload_should_be_rejected_before_json_parsing()
    {
        var content = new byte[ProjectPackageCodec.MaxFileBytes + 1];

        var exception = Should.Throw<ProjectPackageFormatException>(() => _codec.Decode(content));

        exception.Message.ShouldContain("超过 2 MiB");
    }

    [Fact]
    public void Export_should_not_contain_environment_secret_values()
    {
        var package = CreatePackage();
        var text = Encoding.UTF8.GetString(
            _codec.Encode(package.Project, package.Environments, package.Assets));

        text.ShouldNotContain("\"API_TOKEN\"");
        text.ShouldNotContain("\"password\":");
        text.ShouldNotContain("\"secret\":");
    }

    private byte[] EncodeWithRootReplacement(string oldValue, string newValue)
    {
        var package = CreatePackage();
        var text = Encoding.UTF8.GetString(
            _codec.Encode(package.Project, package.Environments, package.Assets));
        var replaced = text.Replace(oldValue, newValue, StringComparison.Ordinal);
        replaced.ShouldNotBe(text);
        return Encoding.UTF8.GetBytes(replaced);
    }

    private static ProjectPackage CreatePackage()
    {
        var projectId = "project-001";

        return new ProjectPackage
        {
            Project = new Project
            {
                Id = projectId,
                Name = "东门视频项目",
                Description = "视频与 IoT 联调",
                CreatedAt = new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.FromHours(8)),
                DefaultTimeoutSeconds = 15
            },
            Environments =
            [
                new EnvironmentProfile
                {
                    Id = "env-001",
                    ProjectId = projectId,
                    Name = "测试环境",
                    Variables = new Dictionary<string, string>
                    {
                        ["REGION"] = "cn-east",
                        ["DEVICE_PORT"] = "554"
                    },
                    ProxyUrl = "http://proxy.internal:3128",
                    DefaultTimeoutSeconds = 10,
                    IsActive = true
                }
            ],
            Assets =
            [
                new Asset
                {
                    Id = "asset-001",
                    ProjectId = projectId,
                    Name = "东门球机",
                    Host = "10.0.0.8",
                    Port = 554,
                    Kind = AssetKind.Camera,
                    Vendor = "海康",
                    Tags = ["园区", "室外"],
                    CredentialReference = "asset.cam-01.password",
                    Note = "仅验证信令"
                }
            ]
        };
    }
}
