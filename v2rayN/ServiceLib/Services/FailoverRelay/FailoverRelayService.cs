namespace ServiceLib.Services.FailoverRelay;

public sealed class FailoverRelayService
{
    private readonly FailoverRelayOptions _options;
    private readonly FailoverCandidateStateStore _stateStore;
    private FailoverRelayHealthConfirmationService? _healthConfirmation;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public FailoverRelayService(FailoverRelayOptions options, IEnumerable<FailoverRelayCandidate> candidates)
    {
        _options = options;
        _stateStore = new FailoverCandidateStateStore(candidates);
    }

    public int ListenPort { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(IPAddress.Loopback, _options.ListenPort);
        _listener.Start();
        ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _healthConfirmation = new FailoverRelayHealthConfirmationService(_options, _stateStore.GetAllCandidates(), _cts.Token);
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _acceptTask = null;
        ListenPort = 0;
    }

    public void MarkHealthRecovered(string profileId)
    {
        _stateStore.MarkHealthRecovered(profileId);
        _healthConfirmation?.MarkHealthRecovered(profileId);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        try
        {
            var firstByte = new byte[1];
            var read = await stream.ReadAsync(firstByte, cancellationToken);
            if (read == 0)
            {
                return;
            }

            await using var buffered = new PrefixedReadStream(stream, firstByte);
            if (firstByte[0] == 0x05)
            {
                await HandleSocks5ClientAsync(buffered, cancellationToken);
                return;
            }

            if (IsHttpMethodStart(firstByte[0]))
            {
                await HandleHttpClientAsync(buffered, cancellationToken);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException)
        {
        }
    }

    private async Task HandleSocks5ClientAsync(Stream clientStream, CancellationToken cancellationToken)
    {
        var request = await Socks5Handshake.ReadClientConnectAsync(clientStream, cancellationToken);
        var protocol = "socks5-connect";
        var target = $"{request.Host}:{request.Port}";
        var connectCandidateAsync = (Stream stream, CancellationToken token) =>
            Socks5Handshake.ConnectCandidateAsync(stream, request, token);
        var candidates = _stateStore.GetRequestCandidates();

        var firstSession = await OpenFirstCandidateSessionAsync(
            protocol,
            target,
            connectCandidateAsync,
            candidates,
            startIndex: 0,
            cancellationToken);
        if (firstSession is null)
        {
            await Socks5Handshake.WriteClientFailureAsync(clientStream, cancellationToken);
            return;
        }

        await Socks5Handshake.WriteClientSuccessAsync(clientStream, cancellationToken);
        await PromoteAndPumpCandidateSequentiallyAsync(
            protocol,
            target,
            clientStream,
            candidates,
            firstSession.Index,
            firstSession.Session,
            connectCandidateAsync,
            cancellationToken);
    }

    private async Task HandleHttpClientAsync(Stream clientStream, CancellationToken cancellationToken)
    {
        HttpProxyRequest request;
        try
        {
            request = await HttpProxyRequestReader.ReadAsync(
                clientStream,
                _options.MaxHttpHeaderBytes,
                _options.MaxPlainHttpReplayBodyBytes,
                cancellationToken);
        }
        catch (InvalidDataException)
        {
            await HttpConnectHandshake.WritePlainHttpUnsupportedAsync(clientStream, cancellationToken);
            return;
        }

        if (!request.IsConnect)
        {
            await HandlePlainHttpClientAsync(clientStream, request, cancellationToken);
            return;
        }

        var connectRequest = HttpConnectHandshake.ParseClientConnect(request.RawHeaderText);
        var protocol = "http-connect";
        var target = $"{connectRequest.Host}:{connectRequest.Port}";
        var connectCandidateAsync = (Stream stream, CancellationToken token) =>
            HttpConnectHandshake.ConnectCandidateAsync(stream, connectRequest, _options.MaxHttpHeaderBytes, token);
        var candidates = _stateStore.GetRequestCandidates();

        var firstSession = await OpenFirstCandidateSessionAsync(
            protocol,
            target,
            connectCandidateAsync,
            candidates,
            startIndex: 0,
            cancellationToken);
        if (firstSession is null)
        {
            await HttpConnectHandshake.WriteClientFailureAsync(clientStream, cancellationToken);
            return;
        }

        await clientStream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), cancellationToken);
        await PromoteAndPumpCandidateSequentiallyAsync(
            protocol,
            target,
            clientStream,
            candidates,
            firstSession.Index,
            firstSession.Session,
            connectCandidateAsync,
            cancellationToken);
    }

    private async Task HandlePlainHttpClientAsync(
        Stream clientStream,
        HttpProxyRequest request,
        CancellationToken cancellationToken)
    {
        if (IsDirectTarget(request.Host))
        {
            await HandleDirectPlainHttpClientAsync(clientStream, request, cancellationToken);
            return;
        }

        if (!request.Replayable)
        {
            await HandleNonReplayablePlainHttpClientAsync(clientStream, request, cancellationToken);
            return;
        }

        var replayBytes = request.ToReplayBytes();
        if (await TryRelayPlainHttpRoundAsync(
                clientStream,
                request,
                replayBytes,
                _options.CandidateFirstByteTimeout,
                cancellationToken))
        {
            return;
        }

        if (await TryRelayPlainHttpRoundAsync(
                clientStream,
                request,
                replayBytes,
                _options.CandidateSecondRoundFirstByteTimeout,
                cancellationToken))
        {
            return;
        }

        if (await TryRelayPlainHttpFallbackAsync(clientStream, request, replayBytes, cancellationToken))
        {
            return;
        }

        await HttpConnectHandshake.WriteClientFailureAsync(clientStream, cancellationToken);
    }

    private async Task<bool> TryRelayPlainHttpRoundAsync(
        Stream clientStream,
        HttpProxyRequest request,
        byte[] replayBytes,
        TimeSpan firstByteTimeout,
        CancellationToken cancellationToken)
    {
        var target = $"{request.Host}:{request.Port}";
        foreach (var candidate in _stateStore.GetRequestCandidates())
        {
            if (!await ShouldUseCandidateBeforeRequestAsync(candidate, cancellationToken))
            {
                continue;
            }

            var candidateClient = await TryConnectCandidateAsync(candidate, cancellationToken);
            if (candidateClient is null)
            {
                await ReportCandidateFailureAsync("http-plain", target, candidate, "candidate-connect");
                continue;
            }

            using var _ = candidateClient;
            await using var candidateStream = candidateClient.GetStream();
            await ReportCandidateStatusAsync(candidate, "http-plain", target, FailoverHealthStatus.Requesting, "selected");
            try
            {
                await candidateStream.WriteAsync(replayBytes, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                await ReportCandidateFailureAsync("http-plain", target, candidate, ex.GetType().Name);
                continue;
            }

            var initialCandidatePayload = await ReadSomeWithTimeoutAsync(
                candidateStream,
                firstByteTimeout,
                cancellationToken);
            if (initialCandidatePayload.Length == 0)
            {
                await ReportCandidateFirstByteTimeoutAsync("http-plain", target, candidate);
                continue;
            }

            await ReportCandidateSelectedAsync("http-plain", target, candidate);
            await clientStream.WriteAsync(initialCandidatePayload, cancellationToken);
            await StreamRelay.PumpAsync(clientStream, candidateStream, cancellationToken);
            return true;
        }

        return false;
    }

    private async Task<bool> TryRelayPlainHttpFallbackAsync(
        Stream clientStream,
        HttpProxyRequest request,
        byte[] replayBytes,
        CancellationToken cancellationToken)
    {
        var target = $"{request.Host}:{request.Port}";
        foreach (var candidate in _stateStore.GetRequestCandidates())
        {
            var candidateClient = await TryConnectCandidateAsync(candidate, cancellationToken);
            if (candidateClient is null)
            {
                await ReportCandidateFailureAsync("http-plain", target, candidate, "candidate-connect");
                continue;
            }

            using var _ = candidateClient;
            await using var candidateStream = candidateClient.GetStream();
            try
            {
                await candidateStream.WriteAsync(replayBytes, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                await ReportCandidateFailureAsync("http-plain", target, candidate, ex.GetType().Name);
                continue;
            }

            await ReportCandidateSelectedAsync(
                "http-plain",
                target,
                candidate,
                FailoverHealthStatus.Fallback,
                "all-first-byte-timeout");
            await StreamRelay.PumpAsync(clientStream, candidateStream, cancellationToken);
            return true;
        }

        return false;
    }

    private async Task HandleDirectPlainHttpClientAsync(
        Stream clientStream,
        HttpProxyRequest request,
        CancellationToken cancellationToken)
    {
        using var targetClient = new TcpClient();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.CandidateConnectTimeout);
            await targetClient.ConnectAsync(request.Host, request.Port, timeoutCts.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            Logging.SaveLog($"failover relay direct failed: protocol=http-plain, target={request.Host}:{request.Port}, reason={ex.GetType().Name}");
            await HttpConnectHandshake.WriteClientFailureAsync(clientStream, cancellationToken);
            return;
        }

        await using var targetStream = targetClient.GetStream();
        if (request.Replayable)
        {
            await targetStream.WriteAsync(request.ToDirectReplayBytes(), cancellationToken);
        }
        else
        {
            await targetStream.WriteAsync(request.DirectHeaderBytes, cancellationToken);
        }

        Logging.SaveLog($"failover relay direct selected: protocol=http-plain, target={request.Host}:{request.Port}");
        await StreamRelay.PumpAsync(clientStream, targetStream, cancellationToken);
    }

    private async Task HandleNonReplayablePlainHttpClientAsync(
        Stream clientStream,
        HttpProxyRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in _stateStore.GetRequestCandidates())
        {
            if (!await ShouldUseCandidateBeforeRequestAsync(candidate, cancellationToken))
            {
                continue;
            }

            var candidateClient = await TryConnectCandidateAsync(candidate, cancellationToken);
            if (candidateClient is null)
            {
                await ReportCandidateFailureAsync("http-plain", $"{request.Host}:{request.Port}", candidate, "candidate-connect");
                continue;
            }

            using var _ = candidateClient;
            await using var candidateStream = candidateClient.GetStream();
            try
            {
                await candidateStream.WriteAsync(request.HeaderBytes, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                await ReportCandidateFailureAsync("http-plain", $"{request.Host}:{request.Port}", candidate, ex.GetType().Name);
                continue;
            }

            Logging.SaveLog($"failover relay http-plain non-replayable stream: target={request.Host}:{request.Port}, candidate={candidate.ProfileId}");
            await ReportCandidateSelectedAsync("http-plain", $"{request.Host}:{request.Port}", candidate);
            await StreamRelay.PumpAsync(clientStream, candidateStream, cancellationToken);
            return;
        }

        await HttpConnectHandshake.WriteClientFailureAsync(clientStream, cancellationToken);
    }

    private async Task<CandidateSessionAtIndex?> OpenFirstCandidateSessionAsync(
        string protocol,
        string target,
        Func<Stream, CancellationToken, Task<bool>> connectCandidateAsync,
        IReadOnlyList<FailoverRelayCandidate> candidates,
        int startIndex,
        CancellationToken cancellationToken)
    {
        for (var index = startIndex; index < candidates.Count; index++)
        {
            if (!await ShouldUseCandidateBeforeRequestAsync(candidates[index], cancellationToken))
            {
                continue;
            }

            var session = await OpenCandidateSessionAsync(
                protocol,
                target,
                candidates[index],
                connectCandidateAsync,
                cancellationToken);
            if (session is not null)
            {
                return new CandidateSessionAtIndex(index, session);
            }
        }

        return null;
    }

    private async Task<CandidateSession?> OpenCandidateSessionAsync(
        string protocol,
        string target,
        FailoverRelayCandidate candidate,
        Func<Stream, CancellationToken, Task<bool>> connectCandidateAsync,
        CancellationToken cancellationToken)
    {
        var candidateClient = await TryConnectCandidateAsync(candidate, cancellationToken);
        if (candidateClient is null)
        {
            await ReportCandidateFailureAsync(protocol, target, candidate, "candidate-connect");
            return null;
        }

        var candidateStream = candidateClient.GetStream();
        if (!await RunWithTimeout(
                token => connectCandidateAsync(candidateStream, token),
                _options.CandidateHandshakeTimeout,
                cancellationToken))
        {
            await ReportCandidateFailureAsync(protocol, target, candidate, "handshake-timeout-or-failure");
            candidateClient.Dispose();
            return null;
        }

        return new CandidateSession(candidate, candidateClient, candidateStream);
    }

    private async Task PromoteAndPumpCandidateSequentiallyAsync(
        string protocol,
        string target,
        Stream clientStream,
        IReadOnlyList<FailoverRelayCandidate> candidates,
        int firstSessionIndex,
        CandidateSession firstSession,
        Func<Stream, CancellationToken, Task<bool>> connectCandidateAsync,
        CancellationToken cancellationToken)
    {
        CandidateSession? selected = null;
        var firstSessionDisposed = false;
        try
        {
            var initialClientPayload = await ReadSomeWithTimeoutAsync(
                clientStream,
                _options.CandidateFirstByteTimeout,
                cancellationToken);
            if (initialClientPayload.Length == 0)
            {
                selected = firstSession;
                await ReportCandidateSelectedAsync(protocol, target, selected.Candidate);
                await StreamRelay.PumpAsync(clientStream, selected.Stream, cancellationToken);
                return;
            }

            var firstRound = await TryPromoteCandidateRoundAsync(
                protocol,
                target,
                candidates,
                firstSessionIndex,
                firstSession,
                connectCandidateAsync,
                initialClientPayload,
                _options.CandidateFirstByteTimeout,
                cancellationToken);
            firstSessionDisposed = firstRound.FirstSessionDisposed;
            if (firstRound.Promotion.Success)
            {
                selected = firstRound.Promotion.Session!;
                await ReportCandidateSelectedAsync(
                    protocol,
                    target,
                    selected.Candidate,
                    firstRound.Promotion.SelectionStatus,
                    firstRound.Promotion.SelectionReason);
                await clientStream.WriteAsync(firstRound.Promotion.InitialCandidatePayload, cancellationToken);
                await StreamRelay.PumpAsync(clientStream, selected.Stream, cancellationToken);
                return;
            }

            var secondRound = await TryPromoteCandidateRoundAsync(
                protocol,
                target,
                candidates,
                0,
                null,
                connectCandidateAsync,
                initialClientPayload,
                _options.CandidateSecondRoundFirstByteTimeout,
                cancellationToken);
            if (secondRound.Promotion.Success)
            {
                selected = secondRound.Promotion.Session!;
                await ReportCandidateSelectedAsync(
                    protocol,
                    target,
                    selected.Candidate,
                    secondRound.Promotion.SelectionStatus,
                    secondRound.Promotion.SelectionReason);
                await clientStream.WriteAsync(secondRound.Promotion.InitialCandidatePayload, cancellationToken);
                await StreamRelay.PumpAsync(clientStream, selected.Stream, cancellationToken);
                return;
            }

            selected = await OpenFallbackStableSessionAsync(
                protocol,
                target,
                candidates,
                connectCandidateAsync,
                cancellationToken);
            if (selected is null)
            {
                return;
            }

            await selected.Stream.WriteAsync(initialClientPayload, cancellationToken);
            await ReportCandidateSelectedAsync(
                protocol,
                target,
                selected.Candidate,
                FailoverHealthStatus.Fallback,
                "all-first-byte-timeout");
            await StreamRelay.PumpAsync(clientStream, selected.Stream, cancellationToken);
            return;
        }
        finally
        {
            if (selected is not null)
            {
                selected.Dispose();
            }
            if (!firstSessionDisposed && selected != firstSession)
            {
                firstSession.Dispose();
            }
        }
    }

    private async Task<CandidateRoundResult> TryPromoteCandidateRoundAsync(
        string protocol,
        string target,
        IReadOnlyList<FailoverRelayCandidate> candidates,
        int startIndex,
        CandidateSession? firstSession,
        Func<Stream, CancellationToken, Task<bool>> connectCandidateAsync,
        byte[] initialClientPayload,
        TimeSpan firstByteTimeout,
        CancellationToken cancellationToken)
    {
        var index = startIndex;
        var firstSessionDisposed = false;
        while (index < candidates.Count)
        {
            var opened = firstSession is not null && index == startIndex
                ? new CandidateSessionAtIndex(startIndex, firstSession)
                : await OpenFirstCandidateSessionAsync(
                    protocol,
                    target,
                    connectCandidateAsync,
                    candidates,
                    index,
                    cancellationToken);
            if (opened is null)
            {
                return new CandidateRoundResult(
                    new CandidatePromotionResult(false, null, [], FailoverHealthStatus.Unknown, null),
                    firstSessionDisposed);
            }

            var session = opened.Session;
            index = opened.Index + 1;
            var promotion = await ProbeCandidateSessionAsync(
                protocol,
                target,
                session,
                initialClientPayload,
                firstByteTimeout,
                cancellationToken);
            if (!promotion.Success)
            {
                session.Dispose();
                if (session == firstSession)
                {
                    firstSessionDisposed = true;
                }
                continue;
            }

            return new CandidateRoundResult(promotion, firstSessionDisposed);
        }

        return new CandidateRoundResult(
            new CandidatePromotionResult(false, null, [], FailoverHealthStatus.Unknown, null),
            firstSessionDisposed);
    }

    private async Task<CandidateSession?> OpenFallbackStableSessionAsync(
        string protocol,
        string target,
        IReadOnlyList<FailoverRelayCandidate> candidates,
        Func<Stream, CancellationToken, Task<bool>> connectCandidateAsync,
        CancellationToken cancellationToken)
    {
        var fallbackCandidates = _stateStore.GetRequestCandidates()
            .Where(candidate => candidates.Any(x => x.ProfileId == candidate.ProfileId))
            .ToList();
        if (fallbackCandidates.Count == 0)
        {
            fallbackCandidates = candidates.ToList();
        }

        foreach (var candidate in fallbackCandidates)
        {
            var session = await OpenCandidateSessionAsync(
                protocol,
                target,
                candidate,
                connectCandidateAsync,
                cancellationToken);
            if (session is not null)
            {
                return session;
            }
        }

        return null;
    }

    private async Task<CandidatePromotionResult> ProbeCandidateSessionAsync(
        string protocol,
        string target,
        CandidateSession session,
        byte[] initialClientPayload,
        TimeSpan firstByteTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReportCandidateStatusAsync(
                session.Candidate,
                protocol,
                target,
                FailoverHealthStatus.Requesting,
                "selected");
            await session.Stream.WriteAsync(initialClientPayload, cancellationToken);
            var initialCandidatePayload = await ReadSomeWithTimeoutAsync(
                session.Stream,
                firstByteTimeout,
                cancellationToken);
            if (initialCandidatePayload.Length > 0)
            {
                return new CandidatePromotionResult(
                    true,
                    session,
                    initialCandidatePayload,
                    FailoverHealthStatus.Normal,
                    null);
            }

            await ReportCandidateFirstByteTimeoutAsync(protocol, target, session.Candidate);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && cancellationToken.IsCancellationRequested)
        {
            return new CandidatePromotionResult(false, session, [], FailoverHealthStatus.Unknown, null);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new CandidatePromotionResult(false, session, [], FailoverHealthStatus.Unknown, null);
            }
            await ReportCandidateFailureAsync(protocol, target, session.Candidate, ex.GetType().Name);
        }

        return new CandidatePromotionResult(false, session, [], FailoverHealthStatus.Unknown, null);
    }

    private async Task<TcpClient?> TryConnectCandidateAsync(FailoverRelayCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.CandidateConnectTimeout);
            await client.ConnectAsync(IPAddress.Loopback, candidate.InboundPort, timeoutCts.Token);
            return client;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            Logging.SaveLog($"failover relay candidate connect failed: candidate={candidate.ProfileId}, type={ex.GetType().Name}");
            return null;
        }
    }

    private static async Task<bool> RunWithTimeout(
        Func<CancellationToken, Task<bool>> action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            return await action(timeoutCts.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadSomeWithTimeoutAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer, timeoutCts.Token);
            return read > 0 ? buffer[..read] : [];
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return [];
        }
    }

    private static bool IsHttpMethodStart(byte value)
    {
        return value is (byte)'C' or (byte)'G' or (byte)'P' or (byte)'H' or (byte)'D' or (byte)'O' or (byte)'T';
    }

    private static bool IsDirectTarget(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IsPrivateOrLoopbackAddress(address);
    }

    private static bool IsPrivateOrLoopbackAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || (bytes[0] & 0xfe) == 0xfc;
        }

        return false;
    }

    private async Task ReportCandidateSelectedAsync(
        string protocol,
        string target,
        FailoverRelayCandidate candidate,
        string status = FailoverHealthStatus.Normal,
        string? reason = null)
    {
        _stateStore.MarkRequestSuccess(candidate.ProfileId);
        if (status == FailoverHealthStatus.Normal)
        {
            _healthConfirmation?.MarkRequestSuccess(candidate.ProfileId);
        }

        if (_options.CandidateSelectedReporterAsync is not null)
        {
            try
            {
                await _options.CandidateSelectedReporterAsync(candidate, protocol, target);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("failover relay candidate selected reporter", ex);
            }
        }
        await ReportCandidateStatusAsync(candidate, protocol, target, status, reason);
        Logging.SaveLog($"failover relay selected: protocol={protocol}, target={target}, candidate={candidate.ProfileId}");
    }

    private async Task ReportCandidateFirstByteTimeoutAsync(string protocol, string target, FailoverRelayCandidate candidate)
    {
        Logging.SaveFailoverLog(
            $"event=relay-target-first-byte-timeout profileId={candidate.ProfileId} remarks={candidate.DisplayName} priority={candidate.Sort} target={target} probeUrl={_options.SpeedPingTestUrl} reason=first-byte-timeout action=mark-degraded");
        await ReportCandidateStatusAsync(
            candidate,
            protocol,
            target,
            FailoverHealthStatus.Degraded,
            "first-byte-timeout");
        StartBackgroundHealthConfirmation(candidate, protocol, target, "first-byte-timeout");
    }

    private void StartBackgroundHealthConfirmation(
        FailoverRelayCandidate candidate,
        string protocol,
        string target,
        string reason)
    {
        var healthConfirmation = _healthConfirmation;
        if (healthConfirmation is null)
        {
            return;
        }

        var lifecycleToken = _cts?.Token ?? CancellationToken.None;
        _ = ConfirmHealthInBackgroundAsync(
            healthConfirmation,
            candidate,
            protocol,
            target,
            reason,
            lifecycleToken);
    }

    private async Task ConfirmHealthInBackgroundAsync(
        FailoverRelayHealthConfirmationService healthConfirmation,
        FailoverRelayCandidate candidate,
        string protocol,
        string target,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var confirmation = await healthConfirmation.ConfirmUntilThresholdAsync(candidate, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (confirmation.Success)
            {
                _stateStore.MarkRequestSuccess(candidate.ProfileId);
                Logging.SaveFailoverLog(
                    $"event=relay-health-confirm-success profileId={candidate.ProfileId} remarks={candidate.DisplayName} priority={candidate.Sort} target={target} probeUrl={_options.SpeedPingTestUrl} reason={confirmation.Reason} action=mark-normal");
                await ReportCandidateStatusAsync(
                    candidate,
                    protocol,
                    target,
                    FailoverHealthStatus.Normal,
                    confirmation.Reason);
                return;
            }

            if (confirmation.FailureThresholdReached)
            {
                await ReportHealthConfirmationThresholdFailureAsync(candidate, confirmation);
                return;
            }

            Logging.SaveFailoverLog(
                $"event=relay-health-confirm-suspect profileId={candidate.ProfileId} remarks={candidate.DisplayName} priority={candidate.Sort} target={target} probeUrl={_options.SpeedPingTestUrl} reason={confirmation.Reason} confirmAttempt={confirmation.ConsecutiveFailures} failureThreshold={_options.HealthConfirmationFailureThreshold} action=keep-degraded");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logging.SaveLog("failover relay background health confirmation", ex);
        }
    }

    private async Task<CandidateFailureDecision> ReportCandidateFailureAsync(string protocol, string target, FailoverRelayCandidate candidate, string reason)
    {
        var confirmation = _healthConfirmation is not null
            ? await _healthConfirmation.ConfirmAfterRequestFailureAsync(candidate, CancellationToken.None)
            : new FailoverRelayHealthConfirmationResult(candidate.ProfileId, false, "health-confirmation-unavailable", null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _options.HealthConfirmationFailureThreshold, true);

        var thresholdReached = confirmation.FailureThresholdReached;
        var shouldWriteExternalFailure = thresholdReached
            && (_healthConfirmation?.TryConsumeFailureReport(confirmation) ?? true);
        var eventName = reason == "first-byte-timeout"
            ? "relay-target-first-byte-timeout"
            : "relay-request-failure";
        Logging.SaveFailoverLog(
            $"event={eventName} profileId={candidate.ProfileId} remarks={candidate.DisplayName} priority={candidate.Sort} target={target} probeUrl={_options.SpeedPingTestUrl} reason={reason} action=confirm-health");
        var confirmationEvent = thresholdReached
            ? shouldWriteExternalFailure
                ? "relay-health-confirm-failed"
                : "relay-health-confirm-deduped"
            : "relay-health-confirm-suspect";
        var confirmationAction = shouldWriteExternalFailure ? "mark-failed" : "keep-normal";
        Logging.SaveFailoverLog(
            confirmation.Success
                ? $"event=relay-health-confirm-success profileId={candidate.ProfileId} remarks={candidate.DisplayName} priority={candidate.Sort} target={target} probeUrl={_options.SpeedPingTestUrl} reason={confirmation.Reason} action=keep-normal"
                : $"event={confirmationEvent} profileId={candidate.ProfileId} remarks={candidate.DisplayName} priority={candidate.Sort} target={target} probeUrl={_options.SpeedPingTestUrl} reason={confirmation.Reason} confirmAttempt={confirmation.ConsecutiveFailures} failureThreshold={_options.HealthConfirmationFailureThreshold} action={confirmationAction}");
        var failureCount = 0;
        if (shouldWriteExternalFailure)
        {
            _stateStore.MarkRequestFailure(candidate.ProfileId);
            _healthConfirmation?.MarkExternalFailed(candidate.ProfileId, reason);
            if (_options.CandidateFailureReporterAsync is not null)
            {
                try
                {
                    failureCount = await _options.CandidateFailureReporterAsync(candidate, protocol, target, reason);
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("failover relay candidate failure reporter", ex);
                }
            }
        }
        else if (reason != "first-byte-timeout")
        {
            await ReportCandidateStatusAsync(candidate, protocol, target, FailoverHealthStatus.Degraded, reason);
        }

        var countMessage = failureCount > 0 ? $", failuresToday={failureCount}" : string.Empty;
        var action = shouldWriteExternalFailure ? "mark-failed" : "keep-candidate";
        Logging.SaveLog($"failover relay candidate failure observed: protocol={protocol}, target={target}, candidate={candidate.ProfileId}, reason={reason}, healthReason={confirmation.Reason}, confirmFailures={confirmation.ConsecutiveFailures}, action={action}{countMessage}");
        var keepCurrentStatus = confirmation.Success
            ? FailoverHealthStatus.Normal
            : FailoverHealthStatus.Fallback;
        return new CandidateFailureDecision(
            reason == "first-byte-timeout" && !shouldWriteExternalFailure,
            reason != "first-byte-timeout" && !shouldWriteExternalFailure,
            shouldWriteExternalFailure,
            keepCurrentStatus);
    }

    private async Task ReportCandidateStatusAsync(
        FailoverRelayCandidate candidate,
        string protocol,
        string target,
        string status,
        string? reason)
    {
        if (_options.CandidateStatusReporterAsync is null)
        {
            return;
        }

        try
        {
            await _options.CandidateStatusReporterAsync(candidate, protocol, target, status, reason);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("failover relay candidate status reporter", ex);
        }
    }

    private async Task<bool> ShouldUseCandidateBeforeRequestAsync(
        FailoverRelayCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (_healthConfirmation is null)
        {
            return true;
        }

        var decision = await _healthConfirmation.CheckBeforeRequestAsync(candidate, cancellationToken);
        if (!decision.ShouldUse && decision.FailureThresholdReached)
        {
            await ReportHealthConfirmationThresholdFailureAsync(candidate, decision.Reason);
        }

        return decision.ShouldUse;
    }

    private async Task ReportHealthConfirmationThresholdFailureAsync(FailoverRelayCandidate candidate, string reason)
    {
        _stateStore.MarkRequestFailure(candidate.ProfileId);
        _healthConfirmation?.MarkExternalFailed(candidate.ProfileId, reason);
        var failureCount = 0;
        if (_options.CandidateFailureReporterAsync is not null)
        {
            try
            {
                failureCount = await _options.CandidateFailureReporterAsync(
                    candidate,
                    "relay-health-confirm",
                    _options.SpeedPingTestUrl,
                    reason);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("failover relay health confirmation failure reporter", ex);
            }
        }

        Logging.SaveFailoverLog(
            $"event=relay-health-confirm-failed profileId={candidate.ProfileId} remarks={candidate.DisplayName} priority={candidate.Sort} probeUrl={_options.SpeedPingTestUrl} reason={reason} confirmAttempt={_options.HealthConfirmationFailureThreshold} failureThreshold={_options.HealthConfirmationFailureThreshold} failuresToday={failureCount} action=mark-failed");
    }

    private async Task ReportHealthConfirmationThresholdFailureAsync(
        FailoverRelayCandidate candidate,
        FailoverRelayHealthConfirmationResult confirmation)
    {
        if (!(_healthConfirmation?.TryConsumeFailureReport(confirmation) ?? true))
        {
            Logging.SaveFailoverLog(
                $"event=relay-health-confirm-deduped profileId={candidate.ProfileId} remarks={candidate.DisplayName} priority={candidate.Sort} probeUrl={_options.SpeedPingTestUrl} reason={confirmation.Reason} confirmAttempt={confirmation.ConsecutiveFailures} failureThreshold={_options.HealthConfirmationFailureThreshold} action=skip-duplicate");
            return;
        }

        await ReportHealthConfirmationThresholdFailureAsync(candidate, confirmation.Reason);
    }

    private sealed record CandidatePromotionResult(
        bool Success,
        CandidateSession? Session,
        byte[] InitialCandidatePayload,
        string SelectionStatus,
        string? SelectionReason);
    private sealed record CandidateRoundResult(CandidatePromotionResult Promotion, bool FirstSessionDisposed);
    private sealed record CandidateSessionAtIndex(int Index, CandidateSession Session);
    private sealed record CandidateFailureDecision(
        bool KeepCurrentCandidate,
        bool TryNextWithoutExternalFailure,
        bool ExternalFailureWritten,
        string KeepCurrentStatus);

    private sealed class CandidateSession(FailoverRelayCandidate candidate, TcpClient client, Stream stream) : IDisposable
    {
        public FailoverRelayCandidate Candidate { get; } = candidate;
        public Stream Stream { get; } = stream;

        public void Dispose()
        {
            Stream.Dispose();
            client.Dispose();
        }
    }

    private sealed class PrefixedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly Queue<byte> _prefix;

        public PrefixedReadStream(Stream inner, IEnumerable<byte> prefix)
        {
            _inner = inner;
            _prefix = new Queue<byte>(prefix);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = 0;
            while (read < count && _prefix.Count > 0)
            {
                buffer[offset + read++] = _prefix.Dequeue();
            }

            return read > 0 ? read : _inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = 0;
            while (read < buffer.Length && _prefix.Count > 0)
            {
                buffer.Span[read++] = _prefix.Dequeue();
            }

            return read > 0 ? read : await _inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
    }
}
