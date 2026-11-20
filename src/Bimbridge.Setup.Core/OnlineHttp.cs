// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace Bimbridge.Setup
{
    /// <summary>
    /// HTTPS transport for the online update. No credentials or authorization headers: it only works for public releases.
    /// HTTPS only; after redirects the final target must be github.com or *.githubusercontent.com; size and time are bounded.
    /// Call from a background thread (never from the Revit thread).
    /// </summary>
    public static class OnlineHttp
    {
        public static TimeSpan Timeout = TimeSpan.FromMinutes(5);

        private static HttpClient NewClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (NotSupportedException) { }
            var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, UseCookies = false }) { Timeout = Timeout };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("BIMBRIDGE-Updater/1");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json, application/octet-stream, text/plain");
            return c;
        }

        public static bool AllowedFinalHost(Uri u) =>
            u != null && u.Scheme == Uri.UriSchemeHttps &&
            (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
             u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

        private static HttpResponseMessage Send(HttpClient c, string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !AllowedFinalHost(u)) throw new OnlineUpdateException("URL not allowed for the update: " + url);
            HttpResponseMessage resp;
            // Network errors are translated here so the caller does not depend on System.Net.Http.
            try { resp = c.GetAsync(u, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult(); }
            catch (HttpRequestException ex) { throw new OnlineUpdateException("could not connect to GitHub: " + (ex.InnerException?.Message ?? ex.Message)); }
            catch (System.Threading.Tasks.TaskCanceledException) { throw new OnlineUpdateException("GitHub did not respond in time"); }
            if (!AllowedFinalHost(resp.RequestMessage?.RequestUri)) { resp.Dispose(); throw new OnlineUpdateException("the download was redirected to a server that is not allowed"); }
            if (resp.StatusCode == HttpStatusCode.Forbidden || (int)resp.StatusCode == 429) { resp.Dispose(); throw new OnlineUpdateException("GitHub rate-limited anonymous requests from this network; try again later"); }
            if (resp.StatusCode == HttpStatusCode.NotFound) { resp.Dispose(); throw new OnlineUpdateException("not found (is the repository private or does it have no releases?)"); }
            if (!resp.IsSuccessStatusCode) { var code = (int)resp.StatusCode; resp.Dispose(); throw new OnlineUpdateException("GitHub responded " + code); }
            return resp;
        }

        private static void Copy(Stream from, Stream to, long maxBytes)
        {
            var buf = new byte[81920];
            long total = 0; int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > maxBytes) throw new OnlineUpdateException("the response exceeds the maximum allowed size (" + maxBytes + " bytes)");
                to.Write(buf, 0, n);
            }
        }

        public static string GetText(string url, long maxBytes)
        {
            using (var c = NewClient())
            using (var resp = Send(c, url))
            using (var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
            using (var ms = new MemoryStream())
            {
                Copy(s, ms, maxBytes);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static void GetFile(string url, string destination, long maxBytes)
        {
            using (var c = NewClient())
            using (var resp = Send(c, url))
            using (var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
            using (var fs = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Copy(s, fs, maxBytes);
                fs.Flush(true);
            }
        }
    }
}
