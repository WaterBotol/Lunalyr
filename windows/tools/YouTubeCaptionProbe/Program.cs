// YouTubeCaptionProbe <videoId> <language> <out.txt> [seconds]
//
// Proves, for one video, what the Mac's caption sync (3c6c9319) relies on, in the Windows player's own engine: YouTube's
// IFrame player, embedded from a virtual https host in WebView2 exactly as the Music video wallpaper embeds it, loads the
// video's caption track when asked (cc_load_policy, cc_lang_pref, the captions module), and its /api/timedtext reply can be
// read from WebView2 (WebResourceResponseReceived, which sees the embed's own frame). It writes what came back: the
// player's states, the caption tracks, and each timed-text reply's status, size, format and first cues.
//
// Silent and out of sight: Chromium runs with --mute-audio, the WebView2 is muted before anything loads, and the page
// mutes the player on every state. The window is never shown, so it takes no focus. Nothing is kept: the profile is
// InPrivate in a temp folder, removed at the end.

using System.Text;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    private const string Host = "lunalyr-video.example";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            return 2;
        }
        var videoId = args[0];
        var language = args[1];
        var output = args[2];
        var seconds = args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 40;
        var log = new StringBuilder();
        var started = DateTime.Now;
        void Log(string message)
        {
            var line = $"{(DateTime.Now - started).TotalSeconds,6:F1}s  {message}";
            lock (log) log.AppendLine(line);
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var form = new Form
        {
            ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000), Size = new System.Drawing.Size(640, 360),
        };
        var handle = form.Handle; // created, never shown
        var userData = Path.Combine(Path.GetTempPath(), "lunalyr-caption-probe-" + Guid.NewGuid().ToString("N"));
        var context = new ApplicationContext();
        CoreWebView2Controller? controller = null;
        var timer = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log("done");
            context.ExitThread();
        };

        async void Start()
        {
            try
            {
                var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--mute-audio" };
                var environment = await CoreWebView2Environment.CreateAsync(null, userData, options);
                var controllerOptions = environment.CreateCoreWebView2ControllerOptions();
                controllerOptions.IsInPrivateModeEnabled = true;
                controller = await environment.CreateCoreWebView2ControllerAsync(handle, controllerOptions);
                var core = controller.CoreWebView2;
                core.IsMuted = true;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                controller.Bounds = new System.Drawing.Rectangle(0, 0, 640, 360);
                controller.IsVisible = true;
                core.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) =>
                {
                    var html = Encoding.UTF8.GetBytes(Page(videoId, language));
                    e.Response = environment.CreateWebResourceResponse(new MemoryStream(html), 200, "OK",
                        "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: strict-origin-when-cross-origin");
                };
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += (_, e) => Log("page: " + e.TryGetWebMessageAsString());
                core.WebResourceResponseReceived += async (_, e) =>
                {
                    var uri = e.Request.Uri;
                    if (!uri.Contains("/api/timedtext", StringComparison.Ordinal)) return;
                    var query = new Uri(uri).Query;
                    string Param(string name) =>
                        query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == name) is { Length: 2 } pair
                            ? Uri.UnescapeDataString(pair[1]) : "";
                    try
                    {
                        using var stream = await e.Response.GetContentAsync();
                        using var copy = new MemoryStream();
                        if (stream is not null) await stream.CopyToAsync(copy);
                        var body = Encoding.UTF8.GetString(copy.ToArray());
                        var events = CountOf(body, "\"tStartMs\"");
                        var wordTimed = CountOf(body, "\"tOffsetMs\"");
                        Log($"timedtext: HTTP {e.Response.StatusCode}, {copy.Length} bytes, lang={Param("lang")}, kind={Param("kind")}, fmt={Param("fmt")}, "
                            + $"events={events}, word offsets={wordTimed}");
                        Log("  starts: " + Snippet(body));
                    }
                    catch (Exception ex)
                    {
                        Log($"timedtext: HTTP {e.Response.StatusCode}, body unreadable ({ex.GetType().Name}: {ex.Message})");
                    }
                };
                core.Navigate($"https://{Host}/player.html");
                Log("navigating");
                timer.Start();
            }
            catch (Exception ex)
            {
                Log($"failed: {ex.GetType().Name}: {ex.Message}");
                context.ExitThread();
            }
        }

        Start();
        Application.Run(context);
        controller?.Close();
        File.WriteAllText(output, log.ToString(), new UTF8Encoding(false));
        try
        {
            Thread.Sleep(1500);
            Directory.Delete(userData, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return 0;
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>The reply's first few caption texts, so the log shows it holds words (not the lyrics in full).</summary>
    private static string Snippet(string body)
    {
        var texts = new List<string>();
        for (var at = body.IndexOf("\"utf8\":\"", StringComparison.Ordinal); at >= 0 && texts.Count < 3;
             at = body.IndexOf("\"utf8\":\"", at + 8, StringComparison.Ordinal))
        {
            var end = body.IndexOf('"', at + 8);
            if (end < 0) break;
            var text = body[(at + 8)..end].Trim();
            if (text.Length > 1 && text != "\\n") texts.Add(text.Length > 24 ? text[..24] + "…" : text);
        }
        return texts.Count > 0 ? string.Join(" | ", texts) : body[..Math.Min(80, body.Length)].Replace('\n', ' ');
    }

    private static string Page(string videoId, string language) => $$"""
        <!doctype html><html><head><meta charset="utf-8"></head><body style="margin:0;background:#000">
        <div id="p"></div>
        <script>
        function post(m){ try { window.chrome.webview.postMessage(m); } catch (e) {} }
        var lang = '{{language}}';
        function onYouTubeIframeAPIReady(){
          window.player = new YT.Player('p', { width: 640, height: 360, videoId: '{{videoId}}',
            playerVars: { autoplay: 1, mute: 1, playsinline: 1, controls: 0, rel: 0, cc_load_policy: 1, cc_lang_pref: lang, hl: 'en',
                          origin: 'https://{{Host}}' },
            events: {
              onReady: function (e) { e.target.mute(); post('ready'); e.target.playVideo(); },
              onStateChange: function (e) {
                e.target.mute(); post('state ' + e.data);
                if (e.data === 1) {
                  try { e.target.loadModule('captions'); e.target.setOption('captions', 'track', { languageCode: lang }); post('captions asked'); }
                  catch (x) { post('captions error ' + x); }
                  setTimeout(function () {
                    try { post('tracks ' + JSON.stringify((e.target.getOption('captions', 'tracklist') || []).map(function (t) { return t.languageCode + (t.kind ? '/' + t.kind : ''); }))); }
                    catch (x) { post('tracklist error ' + x); }
                  }, 3000);
                }
              },
              onError: function (e) { post('error ' + e.data); }
            } });
        }
        </script>
        <script src="https://www.youtube.com/iframe_api"></script>
        </body></html>
        """;
}
