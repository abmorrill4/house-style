using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace FlareAI.TestKit.Update
{
    /// <summary>
    /// A localhost stand-in for GitHub releases: <c>/repos/x/y/releases/latest</c> answers like the GitHub API, and the
    /// release's assets (the zip and checksums.txt) download from <c>/download/...</c>. Tests use it so the update check
    /// and stager run for real without contacting GitHub. Routes can be replaced to serve errors or wrong files.
    /// </summary>
    public sealed class MockReleaseServer : IDisposable
    {
        private HttpListener _listener;
        private readonly Dictionary<string, Func<HttpListenerResponse, bool>> _routes = new Dictionary<string, Func<HttpListenerResponse, bool>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _hits = new List<string>();
        private readonly object _gate = new object();
        private readonly Thread _thread;

        public MockReleaseServer()
        {
            for (var attempt = 0; ; attempt++)
            {
                Port = FreePort();
                BaseUrl = "http://127.0.0.1:" + Port;
                // A fresh listener per attempt: a failed Start() disposes the listener, so retrying on the same one throws ObjectDisposedException.
                _listener = new HttpListener();
                _listener.Prefixes.Add(BaseUrl + "/");
                try { _listener.Start(); break; }
                catch (HttpListenerException) { try { _listener.Close(); } catch (Exception) { } if (attempt >= 20) throw; }   // another test grabbed the port first
            }
            _thread = new Thread(Loop) { IsBackground = true, Name = "MockReleaseServer" };
            _thread.Start();
        }

        public int Port { get; private set; }
        public string BaseUrl { get; private set; }
        public string LatestUrl { get { return BaseUrl + "/repos/abmorrill4/house-style/releases/latest"; } }

        /// <summary>The test release key (a stand-in for House Style's): the stager under test trusts its public half.</summary>
        private static readonly RSACryptoServiceProvider Key = new RSACryptoServiceProvider(3072, new CspParameters(24)) { PersistKeyInCsp = false };
        public static readonly string PublicKeyXml = Key.ToXmlString(false);

        /// <summary>A checksums.txt.sig body: the base64 RSA-SHA256 signature, as the release job writes it.</summary>
        public static string Sign(byte[] data) { lock (Key) return Convert.ToBase64String(Key.SignData(data, CryptoConfig.MapNameToOID("SHA256"))); }

        /// <summary>Paths requested so far.</summary>
        public IList<string> Hits { get { lock (_gate) return _hits.ToArray(); } }

        /// <summary>
        /// Publishes a release: <paramref name="zipBytes"/> as HouseStyle-&lt;version&gt;.zip, and checksums.txt holding
        /// its SHA-256 (or <paramref name="checksumOverride"/>, to test a mismatch).
        /// </summary>
        public void Publish(string version, byte[] zipBytes, string checksumOverride = null, bool prerelease = false, bool signed = true)
        {
            var zipName = "HouseStyle-" + version + ".zip";
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(zipBytes)).Replace("-", "").ToLowerInvariant();
            var checksums = (checksumOverride ?? hash) + "  " + zipName + "\n";
            var assets = new JArray(
                new JObject { ["name"] = zipName, ["size"] = zipBytes.Length, ["browser_download_url"] = BaseUrl + "/download/" + zipName },
                new JObject { ["name"] = "checksums.txt", ["size"] = checksums.Length, ["browser_download_url"] = BaseUrl + "/download/checksums.txt" });
            if (signed)
            {
                assets.Add(new JObject { ["name"] = "checksums.txt.sig", ["browser_download_url"] = BaseUrl + "/download/checksums.txt.sig" });
                Route("/download/checksums.txt.sig", Bytes(200, "text/plain", Encoding.ASCII.GetBytes(Sign(Encoding.ASCII.GetBytes(checksums)))));
            }
            var json = new JObject
            {
                ["tag_name"] = "v" + version,
                ["name"] = "House Style " + version,
                ["html_url"] = "https://github.com/abmorrill4/house-style/releases/tag/v" + version,
                ["draft"] = false,
                ["prerelease"] = prerelease,
                ["body"] = "Notes for " + version,
                ["assets"] = assets
            }.ToString();
            Route("/repos/abmorrill4/house-style/releases/latest", Bytes(200, "application/json", Encoding.UTF8.GetBytes(json)));
            Route("/download/" + zipName, Bytes(200, "application/zip", zipBytes));
            Route("/download/checksums.txt", Bytes(200, "text/plain", Encoding.ASCII.GetBytes(checksums)));
        }

        /// <summary>Serves <paramref name="body"/> with <paramref name="status"/> at <paramref name="path"/>.</summary>
        public static Func<HttpListenerResponse, bool> Bytes(int status, string type, byte[] body)
        {
            return resp =>
            {
                resp.StatusCode = status; resp.ContentType = type; resp.ContentLength64 = body.Length;
                resp.OutputStream.Write(body, 0, body.Length);
                return true;
            };
        }

        public void Route(string path, Func<HttpListenerResponse, bool> handler) { lock (_gate) _routes[path] = handler; }

        public void Dispose()
        {
            try { _listener.Stop(); } catch (Exception) { }
            try { _listener.Close(); } catch (Exception) { }
            _thread.Join(2000);
        }

        private void Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { return; }
                ThreadPool.QueueUserWorkItem(_ => Serve(ctx));
            }
        }

        private void Serve(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url.AbsolutePath;
                Func<HttpListenerResponse, bool> handler;
                lock (_gate) { _hits.Add(path); _routes.TryGetValue(path, out handler); }
                if (handler == null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
                if (handler(ctx.Response)) ctx.Response.Close();
                else ctx.Response.Abort();
            }
            catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
