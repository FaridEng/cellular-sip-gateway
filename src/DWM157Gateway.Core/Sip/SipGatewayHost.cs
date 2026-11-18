using System.Net;
using System.Text.Json;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace DWM157Gateway.Core.Sip;

public sealed class SipGatewayHost : IAsyncDisposable
{
    private const long DefaultRegistrationLifetimeSeconds = 300;
    private const long MaximumRegistrationLifetimeSeconds = 86400;

    private readonly string _bindAddress;
    private readonly int _sipPort;
    private readonly HashSet<IPAddress> _allowedPeers;
    private readonly SipAccount _account;
    private readonly int _authenticatedContactLifetimeSeconds;
    private readonly string? _contactCachePath;
    private bool _contactCacheLoaded;
    private readonly object _registrationLock = new();
    private SIPTransport? _transport;
    private readonly Dictionary<string, RegisteredSipContact> _registeredContacts = new(StringComparer.OrdinalIgnoreCase);

    public SipGatewayHost(
        string bindAddress,
        int sipPort,
        string username,
        string password,
        IEnumerable<string> allowedTailscaleIPs,
        int authenticatedContactLifetimeSeconds = 86400,
        string? contactCachePath = null)
    {
        _bindAddress = bindAddress;
        _sipPort = sipPort;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("A runtime SIP username and password are required.");
        }

        _account = new SipAccount(username, password, bindAddress);
        _authenticatedContactLifetimeSeconds = Math.Clamp(authenticatedContactLifetimeSeconds, 60, 604800);
        _contactCachePath = string.IsNullOrWhiteSpace(contactCachePath) ? null : Path.GetFullPath(contactCachePath);
        _allowedPeers = allowedTailscaleIPs
            .Select(IPAddress.Parse)
            .ToHashSet();
    }

    public bool IsRunning => _transport is not null;

    public event Action<string>? LogMessage;
    public event Func<SIPUserAgent, SIPServerUserAgent, SIPRequest, RegisteredSipContact, Task>? InviteReceived;

    public bool TryGetRegisteredContact(out RegisteredSipContact contact)
    {
        var contacts = GetRegisteredContacts();
        contact = contacts.FirstOrDefault()!;
        return contact is not null;
    }

    public IReadOnlyList<RegisteredSipContact> GetRegisteredContacts()
    {
        lock (_registrationLock)
        {
            var now = DateTimeOffset.UtcNow;
            var expiredKeys = _registeredContacts
                .Where(pair => pair.Value.ExpiresAt <= now)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in expiredKeys)
            {
                _registeredContacts.Remove(key);
            }

            if (expiredKeys.Length > 0)
            {
                PersistContactCacheLocked();
            }

            return _registeredContacts.Values.OrderBy(contact => contact.PeerAddress.ToString()).ThenBy(contact => contact.Port).ToArray();
        }
    }

    public SIPUserAgent CreateUserAgent()
    {
        var transport = _transport ?? throw new InvalidOperationException("The SIP host is not running.");
        return new SIPUserAgent(transport, null, false, _account);
    }

    public void Start()
    {
        if (_transport is not null)
        {
            return;
        }

        if (_allowedPeers.Count == 0)
        {
            throw new InvalidOperationException("At least one allowlisted Tailscale peer is required before starting SIP.");
        }

        LoadContactCache();

        var bind = IPAddress.Parse(_bindAddress);
        var transport = new SIPTransport();
        transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(bind, _sipPort)));
        transport.SIPTransportRequestReceived += OnRequestAsync;
        _transport = transport;
        LogMessage?.Invoke($"SIP UDP listening on {bind}:{_sipPort}");
    }

    private async Task OnRequestAsync(SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, SIPRequest request)
    {
        if (_allowedPeers.Count > 0 && !_allowedPeers.Contains(remoteEndPoint.GetIPEndPoint().Address))
        {
            LogMessage?.Invoke($"Rejected SIP {request.Method} from non-allowlisted peer {remoteEndPoint}");
            var forbidden = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Forbidden, null);
            await _transport!.SendResponseAsync(forbidden);
            return;
        }

        if (request.Method == SIPMethodsEnum.INVITE)
        {
            if (!await AuthenticateAsync(localEndPoint, remoteEndPoint, request))
            {
                return;
            }

            var contact = RememberAuthenticatedPeer(remoteEndPoint.GetIPEndPoint());

            if (InviteReceived is null)
            {
                var unsupportedResponse = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.NotImplemented, null);
                await _transport!.SendResponseAsync(unsupportedResponse);
                return;
            }

            var userAgent = new SIPUserAgent(_transport, null, true, _account);
            var serverUserAgent = userAgent.AcceptCall(request);
            await InviteReceived.Invoke(userAgent, serverUserAgent, request, contact);
            return;
        }

        if (request.Method is SIPMethodsEnum.REGISTER or SIPMethodsEnum.OPTIONS)
        {
            long? registrationLifetime = null;
            if (request.Method == SIPMethodsEnum.REGISTER)
            {
                if (!await AuthenticateAsync(localEndPoint, remoteEndPoint, request))
                {
                    return;
                }

                registrationLifetime = UpdateRegistration(remoteEndPoint, request);
            }

            var response = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
            if (registrationLifetime.HasValue)
            {
                response.Header.Expires = registrationLifetime.Value;
                var requestContact = request.Header.Contact?.FirstOrDefault();
                if (requestContact is not null)
                {
                    var responseContact = requestContact.CopyOf();
                    responseContact.Expires = registrationLifetime.Value;
                    response.Header.Contact = [responseContact];
                }
            }

            await _transport!.SendResponseAsync(response);
            LogMessage?.Invoke($"Answered SIP {request.Method} from {remoteEndPoint}");
            return;
        }

        // Active SIPUserAgent instances share this transport and own dialog requests.
        // ACK must never receive a response; BYE/CANCEL are answered by the user agent.
        if (request.Method is SIPMethodsEnum.ACK or SIPMethodsEnum.BYE or SIPMethodsEnum.CANCEL)
        {
            return;
        }

        var notImplemented = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.NotImplemented, null);
        await _transport!.SendResponseAsync(notImplemented);
        LogMessage?.Invoke($"Rejected unsupported SIP {request.Method} from {remoteEndPoint}");
    }

    private long UpdateRegistration(SIPEndPoint remoteEndPoint, SIPRequest request)
    {
        var contact = request.Header.Contact?.FirstOrDefault();
        var expiresSeconds = ResolveRegistrationLifetime(contact, request.Header.Expires);
        var remote = remoteEndPoint.GetIPEndPoint();
        var registrationKey = remote.Address.ToString();
        if (expiresSeconds == 0 || contact is null)
        {
            lock (_registrationLock)
            {
                _registeredContacts.Remove(registrationKey);
                PersistContactCacheLocked();
            }

            LogMessage?.Invoke($"Removed SIP registration for {remoteEndPoint}.");
            return 0;
        }

        var user = string.IsNullOrWhiteSpace(contact.ContactURI.User) ? _account.SIPUsername : contact.ContactURI.User;
        // Route back to the authenticated packet source. This also avoids trusting a
        // Contact host/port that is unreachable outside the softphone's local interface.
        var port = remote.Port;
        var destination = $"sip:{user}@{remote.Address}:{port}";
        var registration = new RegisteredSipContact(destination, remote.Address, port, DateTimeOffset.UtcNow.AddSeconds(expiresSeconds));
        lock (_registrationLock)
        {
            _registeredContacts[registrationKey] = registration;
            PersistContactCacheLocked();
        }

        LogMessage?.Invoke($"Updated SIP registration contact at {remote.Address}:{port}; expires in {expiresSeconds}s.");
        return expiresSeconds;
    }

    private static long ResolveRegistrationLifetime(SIPContactHeader? contact, long headerExpires)
    {
        var requestedLifetime = contact is not null && contact.Expires >= 0
            ? contact.Expires
            : headerExpires;

        if (requestedLifetime == 0)
        {
            return 0;
        }

        if (requestedLifetime < 0)
        {
            return DefaultRegistrationLifetimeSeconds;
        }

        return Math.Min(requestedLifetime, MaximumRegistrationLifetimeSeconds);
    }

    internal RegisteredSipContact RememberAuthenticatedPeer(IPEndPoint remote)
    {
        var registrationKey = remote.Address.ToString();
        var destination = $"sip:{_account.SIPUsername}@{remote.Address}:{remote.Port}";
        var contact = new RegisteredSipContact(
            destination,
            remote.Address,
            remote.Port,
            DateTimeOffset.UtcNow.AddSeconds(_authenticatedContactLifetimeSeconds));
        lock (_registrationLock)
        {
            _registeredContacts[registrationKey] = contact;
            PersistContactCacheLocked();
        }

        LogMessage?.Invoke(
            $"Remembered authenticated SIP peer at {remote.Address}:{remote.Port} for incoming routing; expires in {_authenticatedContactLifetimeSeconds}s.");
        return contact;
    }

    private void LoadContactCache()
    {
        lock (_registrationLock)
        {
            if (_contactCacheLoaded)
            {
                return;
            }

            _contactCacheLoaded = true;
            if (_contactCachePath is null || !File.Exists(_contactCachePath))
            {
                return;
            }

            try
            {
                var cached = JsonSerializer.Deserialize<List<PersistedSipContact>>(File.ReadAllText(_contactCachePath)) ?? [];
                var now = DateTimeOffset.UtcNow;
                foreach (var item in cached)
                {
                    if (!IPAddress.TryParse(item.PeerAddress, out var address)
                        || !_allowedPeers.Contains(address)
                        || item.Port is <= 0 or > 65535
                        || item.ExpiresAt <= now)
                    {
                        continue;
                    }

                    _registeredContacts[address.ToString()] = new RegisteredSipContact(
                        $"sip:{_account.SIPUsername}@{address}:{item.Port}",
                        address,
                        item.Port,
                        item.ExpiresAt);
                }

                LogMessage?.Invoke($"Loaded {_registeredContacts.Count} unexpired SIP routing contact(s) from the persistent cache.");
                PersistContactCacheLocked();
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"SIP contact cache could not be read and will be rebuilt: {ex.Message}");
            }
        }
    }

    private void PersistContactCacheLocked()
    {
        if (_contactCachePath is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_contactCachePath)!;
            Directory.CreateDirectory(directory);
            var cached = _registeredContacts.Values
                .OrderBy(contact => contact.PeerAddress.ToString())
                .Select(contact => new PersistedSipContact(
                    contact.PeerAddress.ToString(),
                    contact.Port,
                    contact.ExpiresAt))
                .ToArray();
            var temporaryPath = _contactCachePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _contactCachePath, true);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"SIP contact cache write failed: {ex.Message}");
        }
    }

    private async Task<bool> AuthenticateAsync(SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, SIPRequest request)
    {
        var authentication = SIPRequestAuthenticator.AuthenticateSIPRequest(
            localEndPoint,
            remoteEndPoint,
            request,
            _account);

        if (authentication.Authenticated)
        {
            return true;
        }

        var challenge = SIPResponse.GetResponse(request, authentication.ErrorResponse, null);
        if (authentication.AuthenticationRequiredHeader is not null)
        {
            challenge.Header.AuthenticationHeaders.Add(authentication.AuthenticationRequiredHeader);
        }

        await _transport!.SendResponseAsync(challenge);
        LogMessage?.Invoke($"Challenged SIP {request.Method} from {remoteEndPoint}");
        return false;
    }

    private sealed record PersistedSipContact(string PeerAddress, int Port, DateTimeOffset ExpiresAt);

    private sealed class SipAccount : ISIPAccount
    {
        public SipAccount(string username, string password, string domain)
        {
            ID = Guid.NewGuid().ToString("N");
            SIPUsername = username;
            SIPPassword = password;
            SIPDomain = domain;
        }

        public string ID { get; }
        public string SIPUsername { get; }
        public string SIPPassword { get; }
        public string? HA1Digest => null;
        public string SIPDomain { get; }
        public bool IsDisabled => false;
    }

    public ValueTask DisposeAsync()
    {
        if (_transport is not null)
        {
            _transport.SIPTransportRequestReceived -= OnRequestAsync;
            _transport.Shutdown();
            _transport = null;
        }

        return ValueTask.CompletedTask;
    }
}

public sealed record RegisteredSipContact(string DestinationUri, IPAddress PeerAddress, int Port, DateTimeOffset ExpiresAt);
