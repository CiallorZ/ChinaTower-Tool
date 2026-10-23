using System.Net;
using System.Text;

namespace LinkForge.Adapters.Onvif.Tests;

public class WindowsOnvifProbeTests
{
    [Fact]
    public void Discovery_response_should_extract_endpoint_name_scopes_and_types()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                        xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing">
              <s:Body>
                <d:ProbeMatches>
                  <d:ProbeMatch>
                    <a:EndpointReference><a:Address>urn:uuid:device-1</a:Address></a:EndpointReference>
                    <d:Types>dn:NetworkVideoTransmitter tds:Device</d:Types>
                    <d:Scopes>onvif://www.onvif.org/name/前门摄像机 onvif://www.onvif.org/type/video_encoder</d:Scopes>
                    <d:XAddrs>http://10.0.0.8/onvif/device_service https://10.0.0.8/onvif/device_service</d:XAddrs>
                  </d:ProbeMatch>
                </d:ProbeMatches>
              </s:Body>
            </s:Envelope>
            """;

        var devices = WindowsOnvifProbe.ParseDiscoveryResponse(xml);

        devices.Count.ShouldBe(2);
        devices.ShouldAllBe(device => device.Name == "前门摄像机");
        devices.ShouldContain(device => device.Endpoint == "http://10.0.0.8/onvif/device_service");
        devices.ShouldContain(device => device.Types.Count == 2);
    }

    [Fact]
    public async Task Device_information_should_be_parsed_from_local_soap_service()
    {
        await using var server = new LocalSoapServer(async context =>
        {
            var body = await LocalSoapServer.ReadBodyAsync(context.Request);
            body.ShouldContain("GetDeviceInformation");
            body.ShouldContain("UsernameToken");
            body.ShouldContain("PasswordText");
            body.ShouldContain("LocalSecret42");

            await LocalSoapServer.WriteAsync(
                context,
                200,
                """
                <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                            xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                  <s:Body>
                    <tds:GetDeviceInformationResponse>
                      <tds:Manufacturer>Acme</tds:Manufacturer>
                      <tds:Model>Cam-X1</tds:Model>
                      <tds:FirmwareVersion>1.2.3</tds:FirmwareVersion>
                      <tds:SerialNumber>SN-001</tds:SerialNumber>
                      <tds:HardwareId>HW-01</tds:HardwareId>
                    </tds:GetDeviceInformationResponse>
                  </s:Body>
                </s:Envelope>
                """);
        });
        var probe = new WindowsOnvifProbe();

        var result = await probe.GetDeviceInformationAsync(
            new OnvifEndpoint(server.BaseUri),
            new OnvifCredentials("admin", "LocalSecret42"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Status.ShouldBe(OnvifObservationStatus.Passed);
        result.Facts.ShouldContain(fact => fact.Label == "厂商" && fact.Value == "Acme");
        result.Facts.ShouldContain(fact => fact.Label == "型号" && fact.Value == "Cam-X1");
        result.RawOutput.ShouldNotBeNull().ShouldNotContain("LocalSecret42");
    }

    [Fact]
    public async Task Media_profiles_and_stream_uri_should_use_first_profile()
    {
        var call = 0;
        await using var server = new LocalSoapServer(async context =>
        {
            var body = await LocalSoapServer.ReadBodyAsync(context.Request);
            call++;

            if (body.Contains("GetProfiles", StringComparison.Ordinal))
            {
                await LocalSoapServer.WriteAsync(
                    context,
                    200,
                    """
                    <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                                xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                                xmlns:tt="http://www.onvif.org/ver10/schema">
                      <s:Body>
                        <trt:GetProfilesResponse>
                          <trt:Profiles token="profile-1">
                            <tt:Name>MainStream</tt:Name>
                            <tt:VideoEncoderConfiguration>
                              <tt:Encoding>H264</tt:Encoding>
                              <tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution>
                              <tt:RateControl><tt:FrameRateLimit>25</tt:FrameRateLimit></tt:RateControl>
                            </tt:VideoEncoderConfiguration>
                          </trt:Profiles>
                        </trt:GetProfilesResponse>
                      </s:Body>
                    </s:Envelope>
                    """);
                return;
            }

            body.ShouldContain("profile-1");
            await LocalSoapServer.WriteAsync(
                context,
                200,
                """
                <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                            xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                            xmlns:tt="http://www.onvif.org/ver10/schema">
                  <s:Body>
                    <trt:GetStreamUriResponse>
                      <trt:MediaUri>
                        <tt:Uri>rtsp://127.0.0.1:554/Streaming/Channels/101</tt:Uri>
                      </trt:MediaUri>
                    </trt:GetStreamUriResponse>
                  </s:Body>
                </s:Envelope>
                """);
        });
        var probe = new WindowsOnvifProbe();

        var result = await probe.GetMediaProfilesAsync(
            new OnvifEndpoint(server.BaseUri),
            null,
            null,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Status.ShouldBe(OnvifObservationStatus.Passed);
        result.Facts.ShouldContain(fact => fact.Label == "Profile Token 1" && fact.Value == "profile-1");
        result.Facts.ShouldContain(fact => fact.Value == "H264");
        result.Facts.ShouldContain(fact => fact.Value == "1920 × 1080");
        result.Facts.ShouldContain(fact => fact.Value.StartsWith("rtsp://", StringComparison.Ordinal));
        call.ShouldBe(2);
    }

    [Fact]
    public async Task Unauthorized_should_fail_without_claiming_success_or_suggesting_tls_disable()
    {
        await using var server = new LocalSoapServer(context =>
            LocalSoapServer.WriteAsync(context, 401, "Unauthorized"));
        var probe = new WindowsOnvifProbe();

        var result = await probe.GetDeviceInformationAsync(
            new OnvifEndpoint(server.BaseUri),
            new OnvifCredentials("admin", "wrong"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Status.ShouldBe(OnvifObservationStatus.Failed);
        result.Summary.ShouldContain("401");
        result.Suggestion.ShouldNotBeNullOrWhiteSpace();
        result.Suggestion.Contains("不提供", StringComparison.Ordinal).ShouldBeTrue();
        result.Suggestion.ShouldContain("WS-Security");
    }

    [Fact]
    public async Task Malformed_xml_should_fail_readably()
    {
        await using var server = new LocalSoapServer(context =>
            LocalSoapServer.WriteAsync(context, 200, "<not-closed>"));
        var probe = new WindowsOnvifProbe();

        var result = await probe.GetCapabilitiesAsync(
            new OnvifEndpoint(server.BaseUri),
            null,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Status.ShouldBe(OnvifObservationStatus.Failed);
        result.Summary.ShouldContain("SOAP");
    }

    [Fact]
    public async Task Oversized_response_should_be_rejected()
    {
        await using var server = new LocalSoapServer(async context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/soap+xml";
            context.Response.ContentLength64 = 3 * 1024 * 1024;
            var chunk = Encoding.ASCII.GetBytes(new string('x', 81920));

            for (var written = 0; written < 3 * 1024 * 1024; written += chunk.Length)
            {
                await context.Response.OutputStream.WriteAsync(chunk);
            }

            context.Response.Close();
        });
        var probe = new WindowsOnvifProbe();

        var result = await probe.GetDeviceInformationAsync(
            new OnvifEndpoint(server.BaseUri),
            null,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Status.ShouldBe(OnvifObservationStatus.Failed);
        result.Summary.ShouldContain("上限");
    }

    [Fact]
    public async Task Cancellation_should_throw_operation_cancelled()
    {
        await using var server = new LocalSoapServer(async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            await LocalSoapServer.WriteAsync(context, 200, "<ok/>");
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var probe = new WindowsOnvifProbe();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            probe.GetDeviceInformationAsync(
                new OnvifEndpoint(server.BaseUri),
                null,
                TimeSpan.FromSeconds(5),
                cts.Token));
    }
}
