using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using GeZi.Core.Api;
using GeZi.Core.Support;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;

using GeZi.Services;
namespace GeZi
{
    /// <summary>
    /// 迅雷「网页路线」的验证码取用器。
    /// <para>
    /// <b>为什么需要它：</b>迅雷云盘的 <c>captcha/init</c> 盾按 <c>client_id</c> 分体系 ——
    /// App 的 client_id 和网页的 client_id 签发的 <c>captcha_token</c> 互不通用。
    /// 我们的 <c>pan_token</c> 来自网页登录（<see cref="XunleiConstants.ClientId"/>），
    /// 所以必须配网页体系签发的 captcha；用 App 体系去申请会被判
    /// <c>captcha_invalid / detail="client_id not match"</c>。
    /// </para>
    /// <para>
    /// <b>为什么不自己算签名：</b>网页的 <c>captcha_sign</c> 由一层层混淆的
    /// <c>pub*</c> 函数链算出（service-pubkey 动态加载），纯算法复现成本高且随版本失效。
    /// 网页自己会在 localStorage 里写好 <c>captcha_&lt;clientId&gt;</c>
    /// （含 <c>token</c> 与 <c>expires_at</c>）和 <c>deviceid</c>，
    /// 我们只要把 WebView2 打开几秒读出来即可 —— 这也是 refs/YunX 的做法。
    /// </para>
    /// <para>
    /// 🚨 <b>2026-11-15 修：取 captcha 必须用 token 自己声明的 client_id</b>。
    /// 迅雷网页登录不同入口会签发不同 <c>aud</c>（本机实测见过
    /// <c>Xqp0kJBXWhwaTpB6</c> 与 <c>XW5SkOhLDjnOZP7J</c> 两种），
    /// 而 localStorage 里的键名是 <c>captcha_&lt;clientId&gt;</c>。
    /// 以前这里写死 <see cref="XunleiConstants.ClientId"/>，
    /// aud 不是它的时候键名就错了 ⇒ 取到的要么是别的体系的 token、
    /// 要么根本取不到 ⇒ 服务端回 <c>captcha_invalid</c>。
    /// 现在由调用方把 <c>client_id</c> 传进来（<c>XunleiClient.ResolveClientId()</c>），
    /// 先按它取，取不到再退回默认键。
    /// </para>
    /// <para>
    /// <b>device_id 要两份：</b>网页把设备号同时写进 localStorage 的
    /// <c>deviceid</c>（形如 <c>wdi10.&lt;64 位 hex&gt;</c>）和 Cookie 的
    /// <c>deviceid</c>（形如 <c>div101.&lt;32 位 hex&gt;</c>），
    /// 两份**不是同一个值**。服务端判 <c>device not match</c> 时，
    /// 到底认哪一份取决于这个 captcha 是网页用哪份设备号签的。
    /// refs/YunX 用的是 Cookie 那份并按「点号后半段取前 32 位」归一。
    /// 所以这里把两份都返回，调用方先用一份，失败再换另一份重试。
    /// </para>
    /// </summary>
    internal static class XunleiCaptchaFetcher
    {
        private const string LoginUrl = "https://pan.xunlei.com/";
        private const int WaitMs = 12000;

        /// <summary>
        /// 「现在能不能开 WebView2」的判据（由 <c>MainWindow</c> 挂上）。
        ///
        /// <para>
        /// 🚨 这个取码器和「内嵌登录」窗口用的是**同一个 WebView2 用户数据目录**
        /// （<c>profile-xunlei</c>，见 <see cref="WebView2Runtime.CreateIsolatedDataFolder"/>）。
        /// 而 WebView2 对同一个用户数据目录**不允许两个环境同时打开** ——
        /// 用户正在内嵌登录页里操作时，这里再开一个只会静默失败，
        /// 表现为「刷新列表一直失败、日志里什么都没有」。
        /// </para>
        ///
        /// <para>
        /// 更不能给它换一个独立目录：迅雷的 deviceid 与 captcha 是**成对**签发的，
        /// 换目录就换了设备号，captcha 直接判 <c>client_id not match</c>。
        /// 所以正确做法是「登录窗口开着时不要抢」，等它关掉再取。
        /// </para>
        /// </summary>
        public static Func<bool> IsBlocked { get; set; }

        /// <summary>
        /// 打开一次隐藏的 WebView2，读出网页当前的 captcha 与设备号。
        /// <para>
        /// <paramref name="clientId"/>：当前 pan_token 声明的 client_id（token 的 aud）。
        /// 空的按网页默认值处理。
        /// </para>
        /// <para>
        /// 返回 <c>(captcha, deviceId, altDeviceId)</c>：
        /// captcha 是网页签发的验证码 token；
        /// deviceId 是首选设备号（localStorage 那份）；
        /// altDeviceId 是备用设备号（Cookie 那份，按 refs/YunX 的规则归一）；
        /// 都拿不到就返回 <c>(null, null, null)</c>，调用方按「没有可用 captcha」处理。
        /// </para>
        /// </summary>
        public static async Task<(string captcha, string deviceId, string altDeviceId)> FetchAsync(string clientId)
        {
            var app = Application.Current;
            if (app == null)
                return (null, null, null);

            // 内嵌登录窗口正开着 → 同一个 profile 抢不到，直接放弃。
            // 返回 null 让调用方走「暂时没有 captcha」的降级，而不是去开一个必然失败的窗口。
            try
            {
                var blocked = IsBlocked;
                if (blocked != null && blocked())
                    return (null, null, null);
            }
            catch
            {
                return (null, null, null);
            }

            try
            {
                var op = app.Dispatcher.InvokeAsync(() => FetchOnUiThreadAsync(clientId));
                return await op.Task.Unwrap().ConfigureAwait(false);
            }
            catch
            {
                return (null, null, null);
            }
        }

        private static async Task<(string captcha, string deviceId, string altDeviceId)> FetchOnUiThreadAsync(string clientId)
        {
            Window win = null;
            WebView2 web = null;
            try
            {
                win = new Window
                {
                    Width = 420,
                    Height = 320,
                    // 放到屏幕外 + 不抢焦点：用户点「刷新列表」时不该突然弹一个窗口
                    Left = -4000,
                    Top = -4000,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowActivated = false,
                    Title = "迅雷验证码",
                };

                web = new WebView2();
                win.Content = web;
                win.Show();

                string folder = WebView2Runtime.CreateIsolatedDataFolder(PanKind.Xunlei.ToString());
                var env = await CoreWebView2Environment.CreateAsync(null, folder).ConfigureAwait(true);
                await web.EnsureCoreWebView2Async(env).ConfigureAwait(true);

                try
                {
                    web.CoreWebView2.Settings.UserAgent = XunleiConstants.DesktopUa;
                }
                catch { }

                web.CoreWebView2.Navigate(LoginUrl);

                // 键名候选：先用 token 自己声明的 client_id，再退回网页默认值。
                // aud 不是默认值时键名就不同，写死默认值会取不到对应体系的 captcha。
                string cid = string.IsNullOrEmpty(clientId) ? XunleiConstants.ClientId : clientId.Trim();
                string[] captchaKeys = cid == XunleiConstants.ClientId
                    ? new[] { "captcha_" + cid }
                    : new[] { "captcha_" + cid, "captcha_" + XunleiConstants.ClientId };

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(WaitMs);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(700).ConfigureAwait(true);

                    string cap = null;
                    foreach (var key in captchaKeys)
                    {
                        cap = await ReadStorageAsync(web, key).ConfigureAwait(true);
                        if (!string.IsNullOrEmpty(cap))
                            break;
                    }
                    if (string.IsNullOrEmpty(cap))
                        continue;

                    string token = ExtractCaptchaToken(cap);
                    if (string.IsNullOrEmpty(token) || token.Length < 16)
                        continue;
                    if (IsExpired(cap))
                        continue;

                    // 设备号两份都取：localStorage 那份原样，Cookie 那份按 refs/YunX 归一。
                    string dev = await ReadStorageAsync(web, "deviceid").ConfigureAwait(true);
                    dev = string.IsNullOrEmpty(dev) ? null : dev.Trim();

                    string cookieDev = NormalizeCookieDeviceId(await ReadCookieDeviceIdAsync(web).ConfigureAwait(true));

                    // 首选 localStorage 那份（我们自己存的 XunleiWebDeviceId 也是它，保持一致），
                    // 备用 Cookie 那份（refs/YunX 的取法）。
                    string primary = !string.IsNullOrEmpty(dev) ? dev : cookieDev;
                    string alt = !string.IsNullOrEmpty(dev) && !string.IsNullOrEmpty(cookieDev) && cookieDev != dev
                        ? cookieDev
                        : null;

                    return (token, primary, alt);
                }

                return (null, null, null);
            }
            catch
            {
                return (null, null, null);
            }
            finally
            {
                try
                {
                    if (web != null)
                    {
                        web.Dispose();
                    }
                }
                catch { }
                try
                {
                    if (win != null)
                    {
                        win.Close();
                    }
                }
                catch { }
            }
        }

        private static async Task<string> ReadStorageAsync(WebView2 web, string key)
        {
            try
            {
                if (web == null || web.CoreWebView2 == null)
                    return null;
                string script = "(function(){try{var v=localStorage.getItem('" + key + "');"
                              + "return v===null?'':v;}catch(e){return '';}})()";
                string raw = await web.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
                return Unquote(raw);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读 Cookie 里的 <c>deviceid</c>（网页另有一份在 localStorage）。</summary>
        private static async Task<string> ReadCookieDeviceIdAsync(WebView2 web)
        {
            try
            {
                if (web == null || web.CoreWebView2 == null)
                    return null;
                string script = "(function(){try{var cs=String(document.cookie||'').split(';');"
                              + "for(var i=0;i<cs.length;i++){var d=cs[i].replace(/^\\s+/,'');"
                              + "if(d.indexOf('deviceid=')===0) return d.substring(9);}return '';}"
                              + "catch(e){return '';}})()";
                string raw = await web.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
                return Unquote(raw);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Cookie 里的设备号归一：<c>div101.&lt;64hex&gt;</c> → 取点号后半段的前 32 位。
        /// <para>口径来自 refs/YunX（<c>XunleiWebCredential.READ_SCRIPT</c>）：
        /// 服务端认的是那 32 位，不是整串。整串传上去会被判 <c>device not match</c>。</para>
        /// </summary>
        internal static string NormalizeCookieDeviceId(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;
            string d = raw.Trim();
            if (d.Length == 0)
                return null;
            int dot = d.IndexOf('.');
            if (dot > 0 && d.Length > 32)
                d = d.Substring(dot + 1);
            if (d.Length > 32)
                d = d.Substring(0, 32);
            return d.Length == 0 ? null : d;
        }

        /// <summary>从 <c>captcha_&lt;cid&gt;</c> 的 JSON 里取 <c>token</c>。</summary>
        private static string ExtractCaptchaToken(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            var m = Regex.Match(json, "\\x22token\\x22\\s*:\\s*\\x22([^\\x22]+)\\x22");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>判断 <c>expires_at</c> 是否已过期（解析不出来就当没过期）。</summary>
        private static bool IsExpired(string json)
        {
            if (string.IsNullOrEmpty(json))
                return true;
            var m = Regex.Match(json, "\\x22expires_at\\x22\\s*:\\s*\\x22([^\\x22]+)\\x22");
            if (!m.Success)
                return false;
            DateTimeOffset when;
            if (DateTimeOffset.TryParse(m.Groups[1].Value, out when))
                return when <= DateTimeOffset.UtcNow.AddSeconds(-5);
            return false;
        }

        /// <summary><c>ExecuteScriptAsync</c> 的结果是 JSON 编码过的字符串，去掉外层引号。</summary>
        private static string Unquote(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";
            string s = raw.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            {
                s = s.Substring(1, s.Length - 2);
                s = s.Replace("\\\"", "\"").Replace("\\\\", "\\")
                     .Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t")
                     .Replace("\\/", "/");
            }
            return s;
        }
    }
}
