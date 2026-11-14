using System.Net;
using System.Net.Sockets;
using System.Text;
using WindowsAxon.Windows;

namespace WindowsAxon.Agent.Tests.Tools;

public class PublicWebFetcherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connect_timeouts_try_the_next_address(bool socketTimeout)
    {
        var attempts = 0;
        using var fetcher = new PublicWebFetcher(new TimeoutThenSuccessHandler(socketTimeout, () => attempts++),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8") }));

        await fetcher.FetchAsync("https://public.invalid", TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
    }

    private sealed class TimeoutThenSuccessHandler(bool socketTimeout, Action attempted) : HttpMessageHandler
    {
        private int _attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            attempted();

            if (++_attempts == 1)
            {
                if (socketTimeout)
                {
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "Timed out.",
                        new SocketException((int)SocketError.TimedOut));
                }

                throw new TaskCanceledException("ConnectTimeout", new TimeoutException());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Article") });
        }
    }

    [Fact]
    public async Task Concurrent_origins_on_the_same_ip_keep_separate_proxy_contexts()
    {
        var inner = new RecordingProxy(new Uri("http://proxy.invalid"));
        var proxy = new PublicWebFetcher.OriginProxy(inner);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        async Task Check(string host)
        {
            proxy.SetOrigin(new Uri($"https://{host}/page"));

            if (Interlocked.Increment(ref count) == 2)
            {
                ready.SetResult();
            }

            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            proxy.GetProxy(new Uri("https://1.1.1.1/page"));
        }

        await Task.WhenAll(Check("one.invalid"), Check("two.invalid"));

        Assert.Contains(inner.Destinations, u => u.Host == "one.invalid");
        Assert.Contains(inner.Destinations, u => u.Host == "two.invalid");
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Blocked_status_is_checked_by_the_fetcher(int status)
    {
        using var fetcher = Fetcher(new FixtureHandler(_ => new HttpResponseMessage((HttpStatusCode)status)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.FetchAsync("https://public.invalid", TestContext.Current.CancellationToken));

        Assert.Equal(WebTools.BlockedStatusMessage(status), error.Message);
    }

    [Fact]
    public async Task An_early_transport_cancellation_does_not_claim_the_request_deadline_expired()
    {
        var error = new OperationCanceledException("Transport cancelled before the deadline.");
        var handler = new FixtureHandler(_ => throw error);
        using var fetcher = Fetcher(handler);

        var result = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fetcher.FetchAsync("https://public.invalid", TestContext.Current.CancellationToken));

        Assert.Same(error, result);
        Assert.Equal(1, handler.Calls);
    }

    [Fact(Timeout = 40000)]
    public async Task Request_deadline_also_limits_response_buffering()
    {
        var handler = new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new PendingContent()
        });
        using var fetcher = Fetcher(handler);

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            fetcher.FetchAsync("https://public.invalid", TestContext.Current.CancellationToken));

        Assert.Equal("Web lookup exceeded its 30-second request timeout.", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class PendingContent : HttpContent
    {
        public PendingContent() => Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.InvalidResponse)]
    [InlineData(HttpRequestError.ProxyTunnelError)]
    [InlineData(HttpRequestError.ConnectionError)]
    public async Task Non_connect_failures_are_not_retried_and_preserve_the_original_error(HttpRequestError kind)
    {
        var error = new HttpRequestException(kind, "Original failure.");
        var handler = new FixtureHandler(_ => throw error);
        using var fetcher = new PublicWebFetcher(handler, (_, _) => Task.FromResult(new[]
        {
            IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")
        }));

        var result = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fetcher.FetchAsync("https://public.invalid", TestContext.Current.CancellationToken));

        Assert.Same(error, result);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_try_another_address()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var handler = new FixtureHandler(_ =>
        {
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        });
        using var fetcher = new PublicWebFetcher(handler, (_, _) => Task.FromResult(new[]
        {
            IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync("https://public.invalid", stop.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Sequential_fetches_reuse_the_proxy_connection()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);

            for (var index = 0; index < 2; index++)
            {
                Assert.NotNull(await reader.ReadLineAsync(timeout.Token));

                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 })
                {
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 7\r\n\r\nArticle"), timeout.Token);
            }
        }, timeout.Token);
        var proxy = new WebProxy($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        using var fetcher = new PublicWebFetcher(resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("1.1.1.1") }),
                                                 proxy: proxy);

        await fetcher.FetchAsync("http://public.invalid/one", timeout.Token);
        await fetcher.FetchAsync("http://public.invalid/two", timeout.Token);
        await server;

        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData("http", "GET http://1.1.1.1/article HTTP/1.1")]
    [InlineData("https", "CONNECT 1.1.1.1:443 HTTP/1.1")]
    public async Task Proxy_receives_a_pinned_destination_and_rules_receive_the_original_host(string scheme,
                                                                                             string expectedLine)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new RecordingProxy(new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"));
        using var fetcher = new PublicWebFetcher(resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("1.1.1.1") }),
                                                 proxy: proxy);
        var pending = fetcher.FetchAsync($"{scheme}://public.invalid/article", timeout.Token);
        using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        var firstLine = await reader.ReadLineAsync(timeout.Token);
        var headers = new List<string>();

        while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } header)
        {
            headers.Add(header);
        }

        var status = scheme == "http" ? "200 OK" : "502 Bad Gateway";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: 7\r\nConnection: close\r\n\r\nArticle"), timeout.Token);

        if (scheme == "http")
        {
            Assert.Contains("Article", (await pending).Content);
            Assert.Contains("Host: public.invalid", headers);
            Assert.Contains(headers, h => h.StartsWith("User-Agent:", StringComparison.Ordinal));
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => pending);
        }

        Assert.Equal(expectedLine, firstLine);
        Assert.All(proxy.Destinations, uri => Assert.Equal("public.invalid", uri.Host));
    }

    private sealed class RecordingProxy(Uri address) : IWebProxy
    {
        public List<Uri> Destinations { get; } = [];

        public ICredentials? Credentials
        {
            get; set;
        }

        public Uri GetProxy(Uri destination)
        {
            lock (Destinations)
            {
                Destinations.Add(destination);
            }

            return address;
        }

        public bool IsBypassed(Uri host) => false;
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("192.0.0.8")]
    [InlineData("192.0.2.255")]
    [InlineData("198.19.255.255")]
    [InlineData("2001:2::1")]
    [InlineData("2001:1ff:ffff::1")]
    [InlineData("3fff:fff::1")]
    public void Nonpublic_addresses_are_rejected(string address)
    {
        Assert.False(PublicWebFetcher.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("192.0.78.1")]
    [InlineData("192.0.1.1")]
    [InlineData("192.2.0.1")]
    [InlineData("192.0.3.1")]
    [InlineData("192.0.0.9")]
    [InlineData("192.0.0.10")]
    [InlineData("2001:470::1")]
    [InlineData("2001:678::1")]
    [InlineData("2001:200::1")]
    [InlineData("2001:3::1")]
    public void Public_addresses_are_accepted(string address)
    {
        Assert.True(PublicWebFetcher.IsPublic(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Redirect_to_metadata_is_blocked_before_the_second_request()
    {
        var handler = new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://169.254.169.254/latest/meta-data/") }
        });
        using var fetcher = Fetcher(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.FetchAsync("https://public.invalid", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Public_plain_text_is_encoded_before_offline_render()
    {
        using var fetcher = Fetcher(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<script>sendSecret()</script>")
        }));
        var result = await fetcher.FetchAsync("https://public.invalid", TestContext.Current.CancellationToken);

        Assert.Equal("https://public.invalid/", result.Url.AbsoluteUri);
        Assert.Contains("&lt;script&gt;", result.Content);
    }

    [Fact]
    public async Task Redirect_loop_has_a_fixed_limit()
    {
        var handler = new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("/loop", UriKind.Relative) }
        });
        using var fetcher = Fetcher(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.FetchAsync("https://public.invalid", TestContext.Current.CancellationToken));
        Assert.Equal(6, handler.Calls);
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls
        {
            get; private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(reply(request));
        }
    }

    private static PublicWebFetcher Fetcher(HttpMessageHandler handler) =>
        new(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("1.1.1.1") }));

    [Fact]
    public async Task A_failed_ipv6_connection_falls_back_to_the_checked_ipv4_address()
    {
        var targets = new List<string>();
        var handler = new FixtureHandler(request =>
        {
            targets.Add(request.RequestUri!.Host);
            Assert.Equal("public.invalid", request.Headers.Host);
            Assert.NotEmpty(request.Headers.UserAgent);

            if (targets.Count == 1)
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "IPv6 is unavailable.",
                    new SocketException((int)SocketError.NetworkUnreachable));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Article") };
        });
        using var fetcher = new PublicWebFetcher(handler, (_, _) => Task.FromResult(new[]
        {
            IPAddress.Parse("2606:4700:4700::1111"), IPAddress.Parse("1.1.1.1")
        }));

        var result = await fetcher.FetchAsync("https://public.invalid", TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls);
        Assert.Contains("1.1.1.1", targets);
        Assert.Equal("public.invalid", result.Url.Host);
    }

    [Fact]
    public async Task Mixed_public_and_private_dns_answers_are_rejected_before_any_request()
    {
        var handler = new FixtureHandler(_ => throw new InvalidOperationException("Must not connect."));
        using var fetcher = new PublicWebFetcher(handler, (_, _) => Task.FromResult(new[]
        {
            IPAddress.Parse("1.1.1.1"), IPAddress.Loopback
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.FetchAsync("https://public.invalid", TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Redirect_revalidates_dns_and_cannot_rebind_to_a_private_address()
    {
        var resolves = 0;
        var handler = new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("/next", UriKind.Relative) }
        });
        using var fetcher = new PublicWebFetcher(handler, (_, _) => Task.FromResult(new[]
        {
            ++resolves == 1 ? IPAddress.Parse("1.1.1.1") : IPAddress.Loopback
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.FetchAsync("https://public.invalid", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("https://bücher.invalid/", "xn--bcher-kva.invalid")]
    [InlineData("http://public.invalid:8080/", "public.invalid:8080")]
    [InlineData("https://[2606:4700::1111]/", "[2606:4700::1111]")]
    public void Host_header_is_ascii_and_keeps_the_port(string url, string expected)
    {
        Assert.Equal(expected, PublicWebFetcher.HostHeader(new Uri(url)));
    }

    [Theory]
    [InlineData("intranet.invalid", "10.1.2.3", true)]
    [InlineData("192.168.1.0/24", "192.168.1.1", true)]
    [InlineData("192.168.1.1", "192.168.1.1", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("intranet.invalid", "127.0.0.1", false)]
    [InlineData("127.0.0.0/8", "127.0.0.1", false)]
    [InlineData("169.254.0.0/16", "169.254.169.254", false)]
    public async Task Allowed_internal_hosts_unlock_private_ranges_only(string allowed, string address, bool expected)
    {
        var handler = new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Internal") });
        using var fetcher = new PublicWebFetcher(handler,
                                                 (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }),
                                                 allowedPrivateHosts: [allowed]);

        var fetch = fetcher.FetchAsync("http://intranet.invalid", TestContext.Current.CancellationToken);

        if (expected)
        {
            Assert.Contains("Internal", (await fetch).Content);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fetch);
            Assert.Equal(0, handler.Calls);
        }
    }

    [Fact]
    public async Task Public_page_cannot_redirect_to_an_allowed_internal_host()
    {
        var handler = new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://intranet.invalid/admin") }
        });
        using var fetcher = new PublicWebFetcher(handler,
                                                 (host, _) => Task.FromResult(new[]
                                                 {
                                                     IPAddress.Parse(host == "intranet.invalid" ? "10.1.2.3" : "1.1.1.1")
                                                 }),
                                                 allowedPrivateHosts: ["intranet.invalid"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.FetchAsync("https://public.invalid", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }
}
