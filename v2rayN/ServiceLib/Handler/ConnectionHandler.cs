namespace ServiceLib.Handler;

public static class ConnectionHandler
{
    private static readonly string _tag = "ConnectionHandler";

    public static async Task<string> RunAvailabilityCheck()
    {
        var time = await GetRealPingTimeInfo();
        var ip = time > 0 ? await GetIPInfo() ?? Global.None : Global.None;

        return string.Format(ResUI.TestMeOutput, time, ip);
    }

    private static async Task<string?> GetIPInfo()
    {
        var url = AppManager.Instance.Config.SpeedTestItem.IPAPIUrl;
        if (url.IsNullOrEmpty())
        {
            return null;
        }

        var downloadHandle = new DownloadService();
        var result = await downloadHandle.TryDownloadString(url, true, "");
        if (result == null)
        {
            return null;
        }

        var ipInfo = JsonUtils.Deserialize<IPAPIInfo>(result);
        if (ipInfo == null)
        {
            return null;
        }

        var ip = ipInfo.ip ?? ipInfo.clientIp ?? ipInfo.ip_addr ?? ipInfo.query;
        var country = ipInfo.country_code ?? ipInfo.country ?? ipInfo.countryCode ?? ipInfo.location?.country_code;

        return $"({country ?? "unknown"}) {ip}";
    }

    private static async Task<int> GetRealPingTimeInfo()
    {
        var responseTime = -1;
        try
        {
            var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            var webProxy = new WebProxy($"socks5://{Global.Loopback}:{port}");
            var url = AppManager.Instance.Config.SpeedTestItem.SpeedPingTestUrl;

            for (var i = 0; i < 2; i++)
            {
                responseTime = await GetRealPingTime(url, webProxy, 10);
                if (responseTime > 0)
                {
                    break;
                }
                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return -1;
        }
        return responseTime;
    }

    public static async Task<int> GetRealPingTime(string url, IWebProxy? webProxy, int downloadTimeout)
    {
        return await GetRealPingTime(url, webProxy, downloadTimeout, CancellationToken.None);
    }

    public static async Task<int> GetRealPingTime(string url, IWebProxy? webProxy, int downloadTimeout, CancellationToken cancellationToken)
    {
        var responseTime = -1;
        if (url.IsNullOrEmpty())
        {
            return responseTime;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(downloadTimeout));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var client = new HttpClient(new SocketsHttpHandler()
            {
                Proxy = webProxy,
                UseProxy = webProxy != null
            });

            List<int> oneTime = new();
            for (var i = 0; i < 2; i++)
            {
                try
                {
                    var timer = Stopwatch.StartNew();
                    await client.GetAsync(url, cts.Token).ConfigureAwait(false);
                    timer.Stop();
                    oneTime.Add(Math.Max(1, (int)timer.Elapsed.TotalMilliseconds));
                    if (i < 1)
                    {
                        await Task.Delay(100, cts.Token);
                    }
                }
                catch (OperationCanceledException) when (oneTime.Count > 0)
                {
                    break;
                }
            }
            var elapsed = oneTime.Where(x => x > 0).OrderBy(x => x).FirstOrDefault();
            responseTime = elapsed == 0 ? -1 : elapsed;
        }
        catch
        {
        }
        return responseTime;
    }
}
