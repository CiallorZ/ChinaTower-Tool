using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace LinkForge.Adapters.Onvif;

internal static class OnvifXml
{
    private const int MaxDocumentCharacters = 2 * 1024 * 1024;

    public static XDocument Parse(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxDocumentCharacters,
            MaxCharactersFromEntities = 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });

        return XDocument.Load(reader, LoadOptions.None);
    }

    public static string BuildEnvelope(string body, OnvifCredentials? credentials)
    {
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false
        };

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(output, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("s", "Envelope", "http://www.w3.org/2003/05/soap-envelope");

            if (credentials is { HasValue: true })
            {
                writer.WriteStartElement("s", "Header", "http://www.w3.org/2003/05/soap-envelope");
                writer.WriteStartElement("Security", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd");
                writer.WriteAttributeString("s", "mustUnderstand", "http://www.w3.org/2003/05/soap-envelope", "1");
                writer.WriteStartElement("UsernameToken", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd");
                writer.WriteElementString("Username", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd", credentials.Username);
                writer.WriteStartElement("Password", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd");
                writer.WriteAttributeString("Type", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText");
                writer.WriteString(credentials.Password);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteStartElement("s", "Body", "http://www.w3.org/2003/05/soap-envelope");
            writer.WriteRaw(body);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return output.ToString();
    }

    public static string BuildProbeEnvelope() =>
        BuildEnvelope(
            """
            <d:Probe xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                     xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
              <d:Types>dn:NetworkVideoTransmitter</d:Types>
            </d:Probe>
            """,
            credentials: null);

    public static string BuildGetDeviceInformationBody() =>
        """
        <tds:GetDeviceInformation xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>
        """;

    public static string BuildGetCapabilitiesBody() =>
        """
        <tds:GetCapabilities xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
          <tds:Category>All</tds:Category>
        </tds:GetCapabilities>
        """;

    public static string BuildGetProfilesBody() =>
        """
        <trt:GetProfiles xmlns:trt="http://www.onvif.org/ver10/media/wsdl"/>
        """;

    public static string BuildGetStreamUriBody(string profileToken)
    {
        var escaped = SecurityElementEscape(profileToken);

        return $"""
            <trt:GetStreamUri xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                              xmlns:tt="http://www.onvif.org/ver10/schema">
              <trt:StreamSetup>
                <tt:Stream>RTP-Unicast</tt:Stream>
                <tt:Transport>
                  <tt:Protocol>RTSP</tt:Protocol>
                </tt:Transport>
              </trt:StreamSetup>
              <trt:ProfileToken>{escaped}</trt:ProfileToken>
            </trt:GetStreamUri>
            """;
    }

    public static string? ChildValue(XElement parent, string localName) =>
        parent
            .Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal))
            ?.Value
            .Trim();

    public static string SecurityElementEscape(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
}
