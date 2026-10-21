namespace LinkForge.Adapters.Video.Tests;

public class RtspResponseParserTests
{
    [Fact]
    public void Valid_sdp_should_extract_media_and_codec()
    {
        var sdp = LocalRtspServer.ValidSdp();
        var summary = RtspResponseParser.ParseSdp(sdp);

        summary.IsValid.ShouldBeTrue();
        summary.SessionName.ShouldBe("LinkForge Test Camera");
        summary.Codecs.ShouldContain("H264/90000");
        summary.Controls.ShouldContain("trackID=1");
    }

    [Fact]
    public void Invalid_sdp_should_not_be_treated_as_valid()
    {
        var summary = RtspResponseParser.ParseSdp("not an sdp");

        summary.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Www_authenticate_should_only_expose_scheme_names()
    {
        var text = RtspResponseParser.DescribeAuthenticationSchemes(
            "Basic realm=\"camera\", Digest realm=\"camera\", nonce=\"secret\"");

        text.ShouldBe("Basic / Digest");
        text.ShouldNotContain("secret");
    }

    [Fact]
    public void Raw_response_redaction_should_remove_auth_values_and_body()
    {
        const string raw =
            "RTSP/1.0 401 Unauthorized\r\n" +
            "CSeq: 1\r\n" +
            "WWW-Authenticate: Basic realm=\"x\"\r\n\r\n" +
            "v=0\r\n";

        var redacted = RtspResponseParser.RedactRawResponse(raw);

        redacted.ShouldContain("WWW-Authenticate: ***");
        redacted.ShouldNotContain("realm");
        redacted.ShouldNotContain("v=0");
    }
}
