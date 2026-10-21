using System.Text;
using LinkForge.Application.Assets;
using LinkForge.Domain.Assets;

namespace LinkForge.Application.Tests.Assets;

/// <summary>
/// 资产 CSV 编解码（LF-004）。
/// </summary>
public class AssetCsvCodecTests
{
    private const string ProjectId = "prj-1";

    [Fact]
    public void Written_file_should_start_with_a_utf8_bom()
    {
        var bytes = AssetCsvCodec.Write([NewAsset("asset-1")]);

        // 没有 BOM 的 UTF-8 在中文版 Excel 里是乱码。
        bytes[..3].ShouldBe([0xEF, 0xBB, 0xBF]);
    }

    [Fact]
    public void Round_trip_should_preserve_every_exported_field()
    {
        var original = new Asset
        {
            Id = "asset-1",
            ProjectId = ProjectId,
            Name = "东门球机",
            Host = "10.0.0.8",
            Port = 8000,
            Kind = AssetKind.Camera,
            Vendor = "海康",
            Tags = ["东门", "球机"],
            CredentialReference = "asset.cam-01.password",
            Note = "夜间补光弱"
        };

        var result = AssetCsvCodec.Read(AssetCsvCodec.Write([original]), ProjectId);

        result.HasFatalError.ShouldBeFalse();
        result.Errors.ShouldBeEmpty();

        var restored = result.Assets.Single();

        restored.Id.ShouldBe(original.Id);
        restored.Name.ShouldBe(original.Name);
        restored.Host.ShouldBe(original.Host);
        restored.Port.ShouldBe(original.Port);
        restored.Kind.ShouldBe(original.Kind);
        restored.Vendor.ShouldBe(original.Vendor);
        restored.Tags.ShouldBe(original.Tags);
        restored.CredentialReference.ShouldBe(original.CredentialReference);
        restored.Note.ShouldBe(original.Note);
    }

    [Fact]
    public void Export_should_use_enum_names_so_round_trips_are_stable()
    {
        var text = Encoding.UTF8.GetString(AssetCsvCodec.Write([NewAsset("asset-1", kind: AssetKind.IotDevice)])[3..]);

        text.ShouldContain("IotDevice");
        text.ShouldNotContain("IoT 设备");
    }

    [Fact]
    public void Missing_required_columns_should_fail_with_the_actual_header()
    {
        var result = AssetCsvCodec.ReadText("foo,bar\r\n1,2\r\n", ProjectId);
        var fatalError = result.FatalError!;

        result.HasFatalError.ShouldBeTrue();
        fatalError.ShouldContain("name");
        fatalError.ShouldContain("host");
        fatalError.ShouldContain("foo", Case.Insensitive, "报错要说明实际表头，否则用户不知道该改哪一列。");
    }

    [Fact]
    public void Empty_input_should_fail_readably()
    {
        AssetCsvCodec.Read([], ProjectId).FatalError.ShouldNotBeNullOrWhiteSpace();
        AssetCsvCodec.ReadText(string.Empty, ProjectId).FatalError.ShouldNotBeNullOrWhiteSpace();
        AssetCsvCodec.ReadText("\r\n\r\n", ProjectId).FatalError.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Oversized_input_should_fail_before_any_parsing()
    {
        var bytes = new byte[AssetCsvCodec.MaxFileBytes + 1];

        var result = AssetCsvCodec.Read(bytes, ProjectId);

        result.HasFatalError.ShouldBeTrue();
        result.FatalError!.ShouldContain("上限");
    }

    [Fact]
    public void Non_utf8_bytes_should_fail_with_an_actionable_message()
    {
        // GBK 编码的"摄像机"：解码不是合法 UTF-8。
        var gbk = new byte[] { 0xC9, 0xE3, 0xCF, 0xF1, 0xBB, 0xFA };

        var result = AssetCsvCodec.Read(gbk, ProjectId);

        result.HasFatalError.ShouldBeTrue();
        result.FatalError!.ShouldContain("UTF-8");
    }

    [Fact]
    public void Quoted_fields_should_survive_commas_quotes_and_newlines()
    {
        var text = "name,host,note\r\n"
            + "\"含,逗号的名称\",10.0.0.8,\"第一行\r\n第二行\"\r\n"
            + "\"含\"\"引号\"\"的名称\",10.0.0.9,\r\n";

        var result = AssetCsvCodec.ReadText(text, ProjectId);

        result.Errors.ShouldBeEmpty();
        result.Assets.Count.ShouldBe(2);
        result.Assets[0].Name.ShouldBe("含,逗号的名称");
        result.Assets[0].Note.ShouldBe("第一行\r\n第二行");
        result.Assets[1].Name.ShouldBe("含\"引号\"的名称");
    }

    [Fact]
    public void Mixed_line_endings_should_be_accepted()
    {
        var text = "name,host\nA,10.0.0.1\r\nB,10.0.0.2\rC,10.0.0.3";

        var result = AssetCsvCodec.ReadText(text, ProjectId);

        result.Assets.Count.ShouldBe(3);
    }

    [Fact]
    public void Trailing_blank_lines_should_be_ignored()
    {
        var text = "name,host\r\nA,10.0.0.1\r\n\r\n\r\n";

        AssetCsvCodec.ReadText(text, ProjectId).Assets.Count.ShouldBe(1);
    }

    [Fact]
    public void A_bad_port_should_reject_only_that_row()
    {
        var text = "name,host,port\r\n好行,10.0.0.1,554\r\n坏行,10.0.0.2,70000\r\n";

        var result = AssetCsvCodec.ReadText(text, ProjectId);

        result.Assets.Count.ShouldBe(1);
        result.Assets[0].Name.ShouldBe("好行");

        // 部分成功必须能被界面识别出来，而不是压成"导入完成"。
        result.IsPartial.ShouldBeTrue();
        result.Errors.Single().LineNumber.ShouldBe(3);
        result.Errors[0].Message.ShouldContain("70000");
    }

    [Fact]
    public void Missing_name_should_reject_the_row_with_its_line_number()
    {
        var text = "name,host\r\n,10.0.0.1\r\n";

        var result = AssetCsvCodec.ReadText(text, ProjectId);

        result.Assets.ShouldBeEmpty();
        result.Errors.Single().LineNumber.ShouldBe(2);
    }

    [Fact]
    public void Line_numbers_should_stay_accurate_after_a_multiline_field()
    {
        var text = "name,host,note\r\nA,10.0.0.1,\"第一行\r\n第二行\"\r\n,10.0.0.2\r\n";

        var result = AssetCsvCodec.ReadText(text, ProjectId);

        // 第二行数据的字段里含一次换行，因此第三行数据落在文件的第 4 物理行。
        result.Errors.Single().LineNumber.ShouldBe(4);
    }

    [Fact]
    public void Credentials_embedded_in_a_url_should_be_stripped_and_reported()
    {
        var text = "name,host\r\n东门摄像机,rtsp://admin:SuperSecret123@10.0.0.8:554/stream\r\n";

        var result = AssetCsvCodec.ReadText(text, ProjectId);

        var asset = result.Assets.Single();

        asset.Host.ShouldBe("10.0.0.8");
        asset.Port.ShouldBe(554, "URL 里的端口应当被采用，而不是丢掉。");
        asset.CredentialReference.ShouldBeNull("绝不能把密码写成引用键。");

        // 明文密码不得出现在结果的任何位置。
        var combined = string.Join(
            "|",
            result.Assets.Select(static a => a.Name + a.Host + a.Vendor + a.Note + a.CredentialReference));
        combined.ShouldNotContain("SuperSecret123");
        combined.ShouldNotContain("admin");

        result.Warnings.Single().Message.ShouldContain("丢弃");
    }

    [Fact]
    public void Bare_user_info_form_should_also_be_stripped()
    {
        var result = AssetCsvCodec.ReadText("name,host\r\n标签,admin:Passw0rd@10.0.0.8\r\n", ProjectId);

        result.Assets.Single().Host.ShouldBe("10.0.0.8");
        result.Warnings.ShouldNotBeEmpty();
    }

    [Fact]
    public void Host_with_a_path_should_be_rejected_with_an_explanation()
    {
        var result = AssetCsvCodec.ReadText("name,host\r\n设备,10.0.0.8/stream\r\n", ProjectId);

        result.Assets.ShouldBeEmpty();
        result.Errors.Single().Message.ShouldContain("路径");
    }

    [Fact]
    public void Ipv6_literals_should_keep_their_brackets_and_port()
    {
        var result = AssetCsvCodec.ReadText("name,host\r\n设备,[2001:db8::1]:8554\r\n", ProjectId);

        var asset = result.Assets.Single();

        asset.Host.ShouldBe("[2001:db8::1]");
        asset.Port.ShouldBe(8554);
    }

    [Fact]
    public void A_password_pasted_into_the_credential_column_should_be_rejected()
    {
        // 真实密码几乎必然带符号，而引用键只允许字母数字与 . _ - :
        var result = AssetCsvCodec.ReadText(
            "name,host,credential\r\n设备,10.0.0.8,Abc123!@#\r\n",
            ProjectId);

        result.Assets.ShouldBeEmpty();
        result.Errors.Single().Message.ShouldContain("引用键");
    }

    [Fact]
    public void A_well_formed_reference_key_should_be_accepted_with_a_reminder()
    {
        var result = AssetCsvCodec.ReadText(
            "name,host,credential\r\n设备,10.0.0.8,asset.cam-01.password\r\n",
            ProjectId);

        result.Assets.Single().CredentialReference.ShouldBe("asset.cam-01.password");
        result.Warnings.Single().Message.ShouldContain("不含密码");
    }

    [Fact]
    public void Unknown_columns_should_be_reported_without_blocking_the_import()
    {
        var result = AssetCsvCodec.ReadText("name,host,型号\r\n设备,10.0.0.8,X1\r\n", ProjectId);

        result.Assets.Count.ShouldBe(1);
        result.Warnings.Single().Message.ShouldContain("型号");
    }

    [Fact]
    public void Column_names_should_be_matched_case_and_separator_insensitively()
    {
        var result = AssetCsvCodec.ReadText("Name, HOST ,Port\r\n设备,10.0.0.8,554\r\n", ProjectId);

        var asset = result.Assets.Single();

        asset.Name.ShouldBe("设备");
        asset.Port.ShouldBe(554);
    }

    [Fact]
    public void Tags_should_be_split_deduplicated_and_trimmed()
    {
        var result = AssetCsvCodec.ReadText(
            "name,host,tags\r\n设备,10.0.0.8,\"东门; 球机 | 东门 、待联调\"\r\n",
            ProjectId);

        result.Assets.Single().Tags.ShouldBe(["东门", "球机", "待联调"]);
    }

    [Fact]
    public void Chinese_asset_kind_names_should_be_accepted()
    {
        var result = AssetCsvCodec.ReadText("name,host,kind\r\n设备,10.0.0.8,摄像机\r\n", ProjectId);

        result.Assets.Single().Kind.ShouldBe(AssetKind.Camera);
    }

    [Fact]
    public void Unknown_kind_should_import_as_unknown_with_a_warning()
    {
        var result = AssetCsvCodec.ReadText("name,host,kind\r\n设备,10.0.0.8,量子设备\r\n", ProjectId);

        result.Assets.Single().Kind.ShouldBe(AssetKind.Unknown);
        result.Warnings.Single().Message.ShouldContain("量子设备");
    }

    [Fact]
    public void Blank_kind_should_default_to_unknown_without_a_warning()
    {
        var result = AssetCsvCodec.ReadText("name,host\r\n设备,10.0.0.8\r\n", ProjectId);

        result.Assets.Single().Kind.ShouldBe(AssetKind.Unknown);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void A_new_asset_without_an_id_should_get_a_fresh_one()
    {
        var result = AssetCsvCodec.ReadText("name,host\r\n设备,10.0.0.8\r\n", ProjectId);

        result.Assets.Single().Id.ShouldNotBeNullOrWhiteSpace();
        result.Assets.Single().ProjectId.ShouldBe(ProjectId);
    }

    [Fact]
    public void Two_imports_without_ids_should_get_different_ids()
    {
        var first = AssetCsvCodec.ReadText("name,host\r\n设备,10.0.0.8\r\n", ProjectId).Assets.Single();
        var second = AssetCsvCodec.ReadText("name,host\r\n设备,10.0.0.8\r\n", ProjectId).Assets.Single();

        first.Id.ShouldNotBe(second.Id);
    }

    [Fact]
    public void An_invalid_id_should_be_rejected_instead_of_silently_replaced()
    {
        var result = AssetCsvCodec.ReadText("id,name,host\r\n带 空格,设备,10.0.0.8\r\n", ProjectId);

        result.Assets.ShouldBeEmpty();
        result.Errors.Single().Message.ShouldContain("id");
    }

    [Fact]
    public void Overlong_note_should_be_truncated_with_a_warning()
    {
        var note = new string('备', AssetCsvLimits.NoteLength + 20);

        var result = AssetCsvCodec.ReadText($"name,host,note\r\n设备,10.0.0.8,{note}\r\n", ProjectId);

        result.Assets.Single().Note!.Length.ShouldBe(AssetCsvLimits.NoteLength);
        result.Warnings.ShouldContain(w => w.Message.Contains("截断"));
    }

    private static Asset NewAsset(string id, AssetKind kind = AssetKind.Camera) => new()
    {
        Id = id,
        ProjectId = ProjectId,
        Name = "设备" + id,
        Host = "10.0.0.1",
        Kind = kind
    };
}
