namespace LinkForge.Adapters.Video.Tests;

public class RtspUrlParserTests
{
    [Fact]
    public void Parse_should_keep_userinfo_private_and_redact_for_display()
    {
        var ok = RtspUrlParser.TryParse(
            "rtsp://admin:SuperSecret123@10.20.30.40:554/Streaming/Channels/101",
            out var endpoint,
            out var error);

        ok.ShouldBeTrue(error);
        endpoint!.Host.ShouldBe("10.20.30.40");
        endpoint.Port.ShouldBe(554);
        endpoint.UserInfo.ShouldBe("admin:SuperSecret123");
        endpoint.Redacted.ShouldContain("***:***@");
        endpoint.Redacted.ShouldNotContain("SuperSecret123");
        endpoint.Redacted.ShouldContain("10.20.30.40");
    }

    [Theory]
    [InlineData("http://camera/live")]
    [InlineData("rtsp://")]
    [InlineData("rtsp://host:0/live")]
    [InlineData("not-a-url")]
    public void Invalid_urls_should_be_rejected(string value)
    {
        RtspUrlParser.TryParse(value, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Rtsps_and_ipv6_should_be_supported()
    {
        var ok = RtspUrlParser.TryParse("rtsps://[2001:db8::1]:322/live", out var endpoint, out var error);

        ok.ShouldBeTrue(error);
        endpoint!.Scheme.ShouldBe("rtsps");
        endpoint.Port.ShouldBe(322);
        endpoint.Redacted.ShouldContain("[2001:db8::1]:322");
    }
}
