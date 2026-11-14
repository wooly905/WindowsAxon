using System.Net;
using System.Net.Sockets;

namespace WindowsAxon.Windows;

/// <summary>Fetches public pages through a connection whose resolved address is checked before use.</summary>
internal sealed class PublicWebFetcher : IDisposable
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private static readonly IPNetwork GlobalV6 = IPNetwork.Parse("2000::/3");
    // Special-purpose ranges, plus IPv6 transition ranges that can encode IPv4 destinations.
    private static readonly IPNetwork[] Restricted = new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
        "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16",
        "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        "2001::/23", "2001:db8::/32", "2002::/16", "3fff::/20"
    }.Select(IPNetwork.Parse).ToArray();
    private static readonly IPNetwork[] PublicExceptions = new[]
    {
        "192.0.0.9/32", "192.0.0.10/32", "2001:1::1/128", "2001:1::2/128", "2001:1::3/128",
        "2001:3::/32", "2001:4:112::/48", "2001:20::/28", "2001:30::/28"
    }.Select(IPNetwork.Parse).ToArray();
    // Only these private ranges can be allowed. Loopback, link-local, and other special ranges stay blocked.
    private static readonly IPNetwork[] AllowablePrivate = new[]
    {
        "10.0.0.0/8", "100.64.0.0/10", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"
    }.Select(IPNetwork.Parse).ToArray();
    private readonly HttpClient _client;
    private readonly HashSet<string> _privateHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IPNetwork> _privateNetworks = [];
    private readonly OriginProxy _proxy;
    private readonly bool _ownsHandler;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public PublicWebFetcher(HttpMessageHandler? handler = null,
                            Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
                            IWebProxy? proxy = null,
                            IEnumerable<string>? allowedPrivateHosts = null)
    {
        foreach (var entry in allowedPrivateHosts ?? [])
        {
            AddAllowedPrivateHost(entry.Trim());
        }

        _proxy = new OriginProxy(proxy ?? HttpClient.DefaultProxy);
        _ownsHandler = handler is null;
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            Proxy = _proxy,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseCookies = false
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; WindowsAxon/0.1)");
        _resolve = resolve ?? ((host, token) => Dns.GetHostAddressesAsync(host, token));
    }

    public async Task<(Uri Url, string Content)> FetchAsync(string url, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            return await FetchCoreAsync(url, deadline.Token);
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Web lookup exceeded its 30-second request timeout.", ex);
        }
    }

    private async Task<(Uri Url, string Content)> FetchCoreAsync(string url, CancellationToken cancellationToken)
    {
        var uri = BrowserTools.WebUrl(url);
        // A public page must not redirect to an internal address that the approval card did not show.
        var allowPrivate = true;

        for (var redirects = 0; redirects <= 5; redirects++)
        {
            var (sent, isPrivate) = await SendAsync(uri, allowPrivate, cancellationToken);
            using var response = sent;
            allowPrivate = isPrivate;

            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("The redirect has no destination.");
                uri = BrowserTools.WebUrl(new Uri(uri, location).AbsoluteUri);
                continue;
            }

            if ((int)response.StatusCode is 403 or 429 or 503)
            {
                throw new InvalidOperationException(WebTools.BlockedStatusMessage((int)response.StatusCode));
            }

            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType;

            if (mediaType is not ("text/html" or "text/plain" or "application/xhtml+xml"))
            {
                throw new InvalidOperationException("Web lookup accepts HTML or plain text only.");
            }

            await response.Content.LoadIntoBufferAsync(MaxBytes, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            // Plain text must not become markup when the offline browser parses it.
            return (uri, mediaType == "text/plain" ? "<pre>" + WebUtility.HtmlEncode(content) + "</pre>" : content);
        }

        throw new InvalidOperationException("Too many web redirects.");
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return (address.AddressFamily == AddressFamily.InterNetwork || GlobalV6.Contains(address))
            && (PublicExceptions.Any(network => network.Contains(address))
                || !Restricted.Any(network => network.Contains(address)));
    }

    private void AddAllowedPrivateHost(string entry)
    {
        if (entry.Length == 0)
        {
            return;
        }

        if (entry.Contains('/'))
        {
            _privateNetworks.Add(IPNetwork.Parse(entry));
        }
        else if (IPAddress.TryParse(entry, out var address))
        {
            _privateNetworks.Add(new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
        }
        else
        {
            _privateHosts.Add(new Uri("http://" + entry).IdnHost);
        }
    }

    internal bool IsAllowedPrivate(string host, IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return AllowablePrivate.Any(network => network.Contains(address))
            && (_privateHosts.Contains(host) || _privateNetworks.Any(network => network.Contains(address)));
    }

    private async Task<(HttpResponseMessage Response, bool IsPrivate)> SendAsync(Uri uri,
                                                                                bool allowPrivate,
                                                                                CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(uri.DnsSafeHost, out var literal)
            ? [literal]
            : await _resolve(uri.DnsSafeHost, cancellationToken);
        var isPrivate = addresses.Any(address => !IsPublic(address));

        if (addresses.Length == 0
            || (isPrivate && !(allowPrivate
                && addresses.All(address => IsPublic(address) || IsAllowedPrivate(uri.IdnHost, address)))))
        {
            throw new InvalidOperationException(_privateHosts.Count + _privateNetworks.Count == 0
                ? "Web lookup permits public addresses only."
                : "Web lookup permits public addresses and the allowed internal hosts only. A public page cannot redirect to an internal host.");
        }

        HttpRequestException? lastError = null;
        // A connect failure through a proxy is a proxy failure. Other addresses would fail the same way.
        var viaProxy = _ownsHandler && !_proxy.IsBypassed(uri) && _proxy.GetProxy(uri) is { } proxyUri && proxyUri != uri;

        foreach (var address in addresses)
        {
            // Pin the URI even through a proxy. Host preserves virtual hosting, TLS SNI, and certificate checks.
            var target = new UriBuilder(uri) { Host = address.ToString() }.Uri;
            _proxy.SetOrigin(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Host = HostHeader(uri);

            try
            {
                return (await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken), isPrivate);
            }
            catch (HttpRequestException ex) when (!viaProxy && IsConnectFailure(ex))
            {
                lastError = ex;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested
                && ex.InnerException is TimeoutException)
            {
                lastError = new HttpRequestException(HttpRequestError.ConnectionError,
                                                     viaProxy ? "The proxy connection timed out." : "HTTP connection timed out.",
                                                     ex);
                if (viaProxy)
                {
                    throw lastError;
                }
            }
            finally
            {
                _proxy.SetOrigin(null);
            }
        }

        throw lastError!;
    }

    // The Host header must be ASCII. IPv6 literals keep their brackets.
    internal static string HostHeader(Uri uri)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;

        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
    }

    private static bool IsConnectFailure(HttpRequestException error) =>
        error.HttpRequestError == HttpRequestError.ConnectionError
        && error.InnerException is SocketException socket
        && socket.SocketErrorCode is SocketError.ConnectionRefused or SocketError.NetworkUnreachable
            or SocketError.HostUnreachable or SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported
            or SocketError.TimedOut;

    public void Dispose() => _client.Dispose();

    // PAC and bypass rules must see the original hostname, not the pinned IP.
    internal sealed class OriginProxy(IWebProxy inner) : IWebProxy
    {
        private readonly AsyncLocal<Uri?> _origin = new();

        public ICredentials? Credentials { get; set; } = inner.Credentials;

        public void SetOrigin(Uri? origin) => _origin.Value = origin;

        public Uri? GetProxy(Uri destination) => inner.GetProxy(_origin.Value ?? destination);

        public bool IsBypassed(Uri host) => inner.IsBypassed(_origin.Value ?? host);
    }
}
