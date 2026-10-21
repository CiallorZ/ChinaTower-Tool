using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LinkForge.Adapters.Http.Tests;

/// <summary>LF-007 HTTP 传输契约测试。</summary>
public class HttpApiTransportTests
{
    private readonly HttpApiTransport _transport = new();

    [Fact]
    public async Task Method_query_headers_and_json_body_should_reach_the_server()
    {
        var received = default(string);

        await using var server = new LocalHttpServer(async context =>
        {
            var body = await LocalHttpServer.ReadBodyAsync(context.Request);
            received =
                $"{context.Request.HttpMethod}|{context.Request.Url?.Query}|" +
                $"{context.Request.Headers["X-Test"]}|{body}";

            await LocalHttpServer.WriteAsync(
                context,
                201,
                "{\"ok\":true}",
                "application/json");
        });

        var response = await _transport.SendAsync(new HttpApiRequest
        {
            Method = "POST",
            Url = new Uri(server.BaseUri, "api/devices").ToString(),
            Query = new Dictionary<string, string>
            {
                ["site"] = "北区",
                ["page"] = "2"
            },
            Headers = new Dictionary<string, string>
            {
                ["X-Test"] = "yes"
            },
            Body = "{\"name\":\"camera\"}",
            BodyKind = HttpApiBodyKind.Json
        });

        received.ShouldNotBeNull();
        received.ShouldStartWith("POST|?site=%E5%8C%97%E5%8C%BA&page=2|yes|");
        received.ShouldContain("\"name\":\"camera\"");
        response.StatusCode.ShouldBe(201);
        response.Body.ShouldBe("{\"ok\":true}");
        response.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    public async Task Http_error_status_should_still_return_a_complete_response(int status)
    {
        await using var server = new LocalHttpServer(context =>
            LocalHttpServer.WriteAsync(context, status, "error-body"));

        var response = await _transport.SendAsync(new HttpApiRequest
        {
            Method = "GET",
            Url = server.BaseUri.ToString()
        });

        response.StatusCode.ShouldBe(status);
        response.Body.ShouldBe("error-body");
        response.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Response_headers_should_include_set_cookie_without_cookie_container()
    {
        await using var server = new LocalHttpServer(context =>
            LocalHttpServer.WriteAsync(
                context,
                200,
                "ok",
                headers: [new("Set-Cookie", "session=secret")]));

        var response = await _transport.SendAsync(new HttpApiRequest
        {
            Method = "GET",
            Url = server.BaseUri.ToString()
        });

        response.ResponseHeaders["Set-Cookie"].ShouldContain("session=secret");
    }

    [Fact]
    public async Task Basic_bearer_and_api_key_auth_should_be_applied_without_entering_the_url()
    {
        var seen = new List<string>();

        await using var server = new LocalHttpServer(async context =>
        {
            seen.Add(context.Request.Headers["Authorization"] ?? string.Empty);
            seen.Add(context.Request.Headers["X-API-Key"] ?? string.Empty);
            await LocalHttpServer.WriteAsync(context, 200, "ok");
        });

        await _transport.SendAsync(new HttpApiRequest
        {
            Method = "GET",
            Url = server.BaseUri.ToString(),
            AuthMode = HttpApiAuthMode.Basic,
            BasicUsername = "user",
            BasicPassword = "pass"
        });

        await _transport.SendAsync(new HttpApiRequest
        {
            Method = "GET",
            Url = server.BaseUri.ToString(),
            AuthMode = HttpApiAuthMode.Bearer,
            BearerToken = "token-value"
        });

        await _transport.SendAsync(new HttpApiRequest
        {
            Method = "GET",
            Url = server.BaseUri.ToString(),
            AuthMode = HttpApiAuthMode.ApiKey,
            ApiKeyHeaderName = "X-API-Key",
            ApiKeyValue = "key-value"
        });

        seen.ShouldContain("Basic dXNlcjpwYXNz");
        seen.ShouldContain("Bearer token-value");
        seen.ShouldContain("key-value");
    }

    [Fact]
    public async Task Redirect_should_be_followed_when_enabled()
    {
        await using var server = new LocalHttpServer(context =>
            context.Request.Url!.AbsolutePath == "/start"
                ? LocalHttpServer.WriteAsync(
                    context,
                    302,
                    string.Empty,
                    headers: [new("Location", "/final")])
                : LocalHttpServer.WriteAsync(context, 200, "final"));

        var response = await _transport.SendAsync(new HttpApiRequest
        {
            Method = "GET",
            Url = new Uri(server.BaseUri, "start").ToString(),
            FollowRedirects = true,
            MaxRedirects = 2
        });

        response.StatusCode.ShouldBe(200);
        response.Body.ShouldBe("final");
        response.FinalUri.AbsolutePath.ShouldBe("/final");
    }

    [Fact]
    public async Task Redirect_should_be_returned_unchanged_when_disabled()
    {
        await using var server = new LocalHttpServer(context =>
            LocalHttpServer.WriteAsync(
                context,
                302,
                "redirect",
                headers: [new("Location", "/final")]));

        var response = await _transport.SendAsync(new HttpApiRequest
        {
            Method = "GET",
            Url = server.BaseUri.ToString(),
            FollowRedirects = false
        });

        response.StatusCode.ShouldBe(302);
        response.Body.ShouldBe("redirect");
    }

    [Fact]
    public async Task Redirect_limit_should_be_enforced()
    {
        var requests = 0;

        await using var server = new LocalHttpServer(context =>
        {
            requests++;

            return LocalHttpServer.WriteAsync(
                context,
                302,
                string.Empty,
                headers: [new("Location", $"/hop-{requests}")]);
        });

        var response = await _transport.SendAsync(new HttpApiRequest
        {
            Method = "GET",
            Url = new Uri(server.BaseUri, "loop").ToString(),
            FollowRedirects = true,
            MaxRedirects = 2
        });

        // SocketsHttpHandler 达到上限后返回最后一个 3xx，而不是无限跟随。
        // 这里以“请求次数有限 + 状态仍可解释”作为硬约束。
        requests.ShouldBeLessThanOrEqualTo(3);
        response.StatusCode.ShouldBe(302);
    }

    [Fact]
    public async Task Large_response_should_be_truncated_at_the_byte_limit()
    {
        await using var server = new LocalHttpServer(context =>
            LocalHttpServer.WriteLargeAsync(context, 40 * 1024));

        var response = await _transport.SendAsync(new HttpApiRequest
        {
            Method = "GET",
            Url = server.BaseUri.ToString(),
            MaxResponseBytes = 16 * 1024
        });

        response.IsTruncated.ShouldBeTrue();
        response.ResponseBytes.ShouldBe(16 * 1024);
        response.Body.Length.ShouldBe(16 * 1024);
    }

    [Fact]
    public async Task Timeout_should_be_classified_as_timeout_not_cancellation()
    {
        await using var server = new LocalHttpServer(async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await LocalHttpServer.WriteAsync(context, 200, "late");
        });

        var exception = await Should.ThrowAsync<HttpApiTransportException>(
            () => _transport.SendAsync(new HttpApiRequest
            {
                Method = "GET",
                Url = server.BaseUri.ToString(),
                Timeout = TimeSpan.FromMilliseconds(150)
            }));

        exception.Kind.ShouldBe(HttpApiFailureKind.Timeout);
    }

    [Fact]
    public async Task Cancellation_should_remain_an_operation_cancelled_exception()
    {
        await using var server = new LocalHttpServer(async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await LocalHttpServer.WriteAsync(context, 200, "late");
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(
            () => _transport.SendAsync(
                new HttpApiRequest
                {
                    Method = "GET",
                    Url = server.BaseUri.ToString(),
                    Timeout = TimeSpan.FromSeconds(5)
                },
                cts.Token));
    }

    [Fact]
    public async Task Dns_failure_should_be_classified_as_dns()
    {
        // 开发机的系统代理可能代替客户端完成 DNS，并返回代理错误页；
        // 这不等于客户端完成了 DNS。这里用一个明确失败的本地 DNS 解析器
        // 验证分类逻辑，避免测试结果依赖当前机器的代理配置。
        var exception = HttpApiTransport.ClassifyForTests(
            new HttpRequestException(
                "Name or service not known",
                new System.Net.Sockets.SocketException((int)SocketError.HostNotFound),
                HttpStatusCode.BadGateway));

        exception.Kind.ShouldBe(HttpApiFailureKind.Dns);
    }

    [Fact]
    public async Task Connection_refused_should_be_classified_as_connection()
    {
        var exception = HttpApiTransport.ClassifyForTests(
            new HttpRequestException(
                "Connection refused",
                new System.Net.Sockets.SocketException((int)SocketError.ConnectionRefused)));

        exception.Kind.ShouldBe(HttpApiFailureKind.Connection);
    }

    [Fact]
    public async Task Url_userinfo_should_be_rejected_instead_of_sent()
    {
        var exception = await Should.ThrowAsync<HttpApiTransportException>(
            () => _transport.SendAsync(new HttpApiRequest
            {
                Method = "GET",
                Url = "http://user:password@example.test/"
            }));

        exception.Kind.ShouldBe(HttpApiFailureKind.InvalidRequest);
    }

    [Fact]
    public void Source_must_not_disable_tls_validation()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src",
                "LinkForge.Adapters.Http",
                "HttpApiTransport.cs"));

        source.ShouldNotContain("DangerousAcceptAnyServerCertificateValidator");
        source.ShouldNotContain("ServerCertificateCustomValidationCallback");
        source.ShouldNotContain("ServerCertificateValidationCallback");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LinkForge.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到仓库根目录。");
    }
}
