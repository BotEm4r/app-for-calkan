using System;
using System.IO;
using System.Text;
using System.Net;
using System.Web;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace CalkanGsmWeb
{
    class Program
    {
        private static string BaseDir = AppContext.BaseDirectory;
        private static string dbPath = Path.Combine(BaseDir, "data", "calkan_gsm.db");
        private static string connStr => $"Data Source={dbPath};";

        private static Dictionary<string, string> Kullanicilar = new Dictionary<string, string>();
        private static string SessionValue = Environment.GetEnvironmentVariable("SESSION_SECRET") ?? Guid.NewGuid().ToString("N");

        private static ConcurrentDictionary<string, (int count, DateTime lockUntil)> loginAttempts = new();
        private static ConcurrentDictionary<string, (int count, DateTime window)> rateLimit = new();
        private static int activeConnections = 0;
        private const int MAX_CONNECTIONS = 100;
        private const int MAX_RPS = 25;
        private const int MAX_BODY_BYTES = 10240;

        private static string configPort = "8080";

        private static void ConfigYukle()
        {
            bool railwayKullaniciBulundu = false;
            for (int i = 1; i <= 20; i++)
            {
                string envVal = Environment.GetEnvironmentVariable("KULLANICI" + i);
                if (string.IsNullOrEmpty(envVal)) continue;
                string[] parts = envVal.Split(':', 2);
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    Kullanicilar[parts[0].Trim()] = parts[1].Trim();
                    railwayKullaniciBulundu = true;
                }
            }

            string railwayPort = Environment.GetEnvironmentVariable("PORT");
            if (!string.IsNullOrEmpty(railwayPort))
                configPort = railwayPort;

            if (railwayKullaniciBulundu)
            {
                Console.WriteLine($"✅ {Kullanicilar.Count} kullanıcı Railway ortam değişkenlerinden yüklendi.");
                return;
            }

            string configPath = Path.Combine(BaseDir, "config.txt");
            if (!File.Exists(configPath))
            {
                File.WriteAllText(configPath,
                    "# Calkan GSM - Kullanici ve Port Ayarlari\n" +
                    "# Format: KULLANICIn=kullanici_adi:sifre\n\n" +
                    "KULLANICI1=admin:SifreniBurayaYaz\n" +
                    "PORT=8080\n");
                Console.WriteLine("📄 config.txt bulunamadı, varsayılan dosya oluşturuldu.");
            }

            foreach (string line in File.ReadAllLines(configPath))
            {
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#")) continue;

                int commentIdx = trimmedLine.IndexOf('#');
                string cleanLine = commentIdx >= 0 ? trimmedLine.Substring(0, commentIdx).Trim() : trimmedLine;

                if (!cleanLine.Contains("=")) continue;

                string[] kvParts = cleanLine.Split('=', 2);
                string key = kvParts[0].Trim().ToUpper();
                string val = kvParts[1].Trim();

                if (key.StartsWith("KULLANICI"))
                {
                    string[] parts = val.Split(':', 2);
                    if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        Kullanicilar[parts[0].Trim()] = parts[1].Trim();
                    }
                }
                else if (key == "PORT" && !string.IsNullOrWhiteSpace(val))
                {
                    configPort = val;
                }
            }

            if (Kullanicilar.Count == 0)
            {
                Console.WriteLine("⚠️ DİKKAT: Hiçbir kullanıcı tanımlanmadı! Panele giriş yapılamaz.");
            }
            else
            {
                Console.WriteLine($"✅ {Kullanicilar.Count} kullanıcı yüklendi. Port: {configPort}");
            }
        }

        private static bool IsRateLimited(string ip)
        {
            var now = DateTime.UtcNow;
            rateLimit.AddOrUpdate(ip, (1, now), (k, old) => {
                if ((now - old.window).TotalSeconds >= 1) return (1, now);
                return (old.count + 1, old.window);
            });
            return rateLimit.TryGetValue(ip, out var e) && e.count > MAX_RPS;
        }

        private static bool IsLocked(string ip)
        {
            if (loginAttempts.TryGetValue(ip, out var entry))
            {
                if (entry.lockUntil > DateTime.UtcNow) return true;
                if (entry.count >= 5)
                {
                    loginAttempts[ip] = (entry.count, DateTime.UtcNow.AddMinutes(15));
                    return true;
                }
            }
            return false;
        }

        private static void RecordFail(string ip)
        {
            loginAttempts.AddOrUpdate(ip, (1, DateTime.MinValue), (k, old) =>
                (old.count + 1, old.count + 1 >= 5 ? DateTime.UtcNow.AddMinutes(15) : old.lockUntil));
        }

        private static void ResetFail(string ip) => loginAttempts.TryRemove(ip, out _);

        private static string GetIP(HttpListenerRequest req)
        {
            string forwarded = req.Headers["X-Forwarded-For"];
            if (!string.IsNullOrEmpty(forwarded)) return forwarded.Split(',')[0].Trim();
            return req.RemoteEndPoint?.Address?.ToString() ?? "unknown";
        }

        private static string GetCSS() => @"
<style>
  @import url('https://fonts.googleapis.com/css2?family=Plus+Jakarta+Sans:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500;600&display=swap');
* {
  -webkit-tap-highlight-color: transparent;
  outline: none;
}
  input::-ms-reveal,
  input::-ms-clear { display: none; }

  :root {
    --bg:          #0f172a;
    --surface:     #1e293b;
    --surface-top: #334155;
    --border:      #384152;
    --text:        #f8fafc;
    --muted:       #94a3b8;
    --accent:      #38bdf8;
    --green:       #4ade80;
    --whatsapp:    #22c55e;
    --red:         #f87171;
    --shadow:      0 10px 25px -5px rgba(0,0,0,0.3), 0 8px 10px -6px rgba(0,0,0,0.3);
  }

  :root[data-theme='light'] {
    --bg:          #e2e8f0;
    --surface:     #ffffff;
    --surface-top: #cbd5e1;
    --border:      #94a3b8;
    --text:        #0f172a;
    --muted:       #475569;
    --accent: #0066cc;
    --green:       #16a34a;
    --whatsapp:    #16a34a;
    --red:         #dc2626;
    --shadow: 0 10px 25px -5px rgba(0,0,0,0.08), 0 8px 10px -6px rgba(0,0,0,0.08);
  }

  * { box-sizing: border-box; margin: 0; padding: 0; transition: background-color 0.2s, border-color 0.2s; }

  body {
    font-family: 'Plus Jakarta Sans', sans-serif;
    background: var(--bg);
    color: var(--text);
    min-height: 100vh;
    -webkit-font-smoothing: antialiased;
  }

  .wrap {
    max-width: 750px;
    margin: 0 auto;
    padding: 50px 20px 100px;
  }

  .shop-nav {
    display: flex;
    justify-content: space-between;
    align-items: center;
    background: var(--surface);
    border: 1px solid var(--border);
    padding: 18px 24px;
    border-radius: 16px;
    margin-bottom: 40px;
    box-shadow: var(--shadow);
  }
  .shop-title { display: flex; align-items: center; gap: 12px; }
  .shop-badge {
    width: 12px; height: 12px;
    background: var(--accent);
    border-radius: 4px;
    box-shadow: 0 0 10px var(--accent);
  }
  .shop-name { font-size: 16px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em; }

  .nav-right { display: flex; align-items: center; gap: 12px; }
  .theme-toggle {
    background: var(--bg);
    border: 1px solid var(--border);
    color: var(--text);
    padding: 6px 12px;
    border-radius: 20px;
    font-size: 12px;
    font-weight: 600;
    cursor: pointer;
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .theme-toggle:hover { border-color: var(--accent); }
  .shop-status {
    font-size: 12px;
    color: var(--green);
    font-weight: 600;
    background: rgba(74,222,128,0.1);
    padding: 6px 12px;
    border-radius: 20px;
  }

  .view-heading { margin-bottom: 30px; }
  .view-title { font-size: 26px; font-weight: 700; letter-spacing: -0.02em; }
  .view-sub { font-size: 14px; color: var(--muted); margin-top: 4px; }

  .menu-layout { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; }
  .menu-card {
    background: var(--surface);
    border: 1px solid var(--border);
    padding: 24px;
    border-radius: 16px;
    color: var(--text);
    text-decoration: none;
    box-shadow: var(--shadow);
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    height: 140px;
  }
  .menu-card:hover {
    border-color: var(--accent);
    transform: translateY(-2px);
    box-shadow: 0 12px 30px rgba(56,189,248,0.15);
  }
  .menu-card .label { font-size: 18px; font-weight: 700; }
  .menu-card .desc { font-size: 12px; color: var(--muted); line-height: 1.4; }
  .menu-full { grid-column: span 2; height: 100px; }

  .form-box {
    background: var(--surface);
    border: 1px solid var(--border);
    padding: 32px;
    border-radius: 16px;
    box-shadow: var(--shadow);
  }
  .form-field { margin-bottom: 22px; }
  .form-field label {
    display: block;
    font-size: 13px;
    font-weight: 600;
    color: var(--muted);
    margin-bottom: 8px;
    text-transform: uppercase;
    letter-spacing: 0.02em;
  }
  .form-input {
    width: 100%;
    padding: 14px;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: 10px;
    color: var(--text);
    font-family: inherit;
    font-size: 14px;
    outline: none;
  }
  .form-input:focus { border-color: var(--accent); }
  select.form-input { cursor: pointer; }

  .password-wrapper {
    position: relative;
    display: flex;
    align-items: center;
  }
  .password-wrapper .form-input {
    padding-right: 48px;
  }
  .toggle-pass {
    position: absolute;
    right: 12px;
    background: none;
    border: none;
    cursor: pointer;
    color: var(--muted);
    padding: 4px;
    display: flex;
    align-items: center;
    justify-content: center;
    transition: color 0.2s;
  }
  .toggle-pass:hover { color: var(--accent); }
  .toggle-pass svg { width: 20px; height: 20px; pointer-events: none; }

  .remember-me {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: -10px;
    margin-bottom: 20px;
    cursor: pointer;
    user-select: none;
    font-size: 14px;
    color: var(--muted);
  }
  .remember-me input {
    width: 16px;
    height: 16px;
    accent-color: var(--accent);
    cursor: pointer;
  }

  .search-container { position: relative; margin-bottom: 24px; }
  .search-bar {
    width: 100%;
    padding: 16px 20px;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 12px;
    color: var(--text);
    font-family: inherit;
    font-size: 14px;
    outline: none;
    box-shadow: var(--shadow);
  }
  .search-bar:focus { border-color: var(--accent); }

  .shop-row {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 14px;
    padding: 24px;
    margin-bottom: 16px;
    box-shadow: var(--shadow);
  }
  .row-header {
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    margin-bottom: 14px;
  }
  .row-title { font-size: 18px; font-weight: 700; letter-spacing: -0.01em; }
  .row-price { font-family: 'JetBrains Mono', monospace; font-size: 18px; font-weight: 700; color: var(--accent); }

  .tags { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 14px; }
  .tag {
    font-size: 12px;
    font-weight: 500;
    padding: 4px 12px;
    background: rgba(128,128,128,0.08);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--muted);
  }
  .tag.active { color: var(--text); border-color: var(--muted); background: rgba(128,128,128,0.12); }

  .row-notes {
    font-size: 13px;
    color: var(--muted);
    background: rgba(0,0,0,0.06);
    padding: 12px;
    border-radius: 8px;
    line-height: 1.5;
  }
  :root[data-theme='dark'] .row-notes { background: rgba(0,0,0,0.25); }

  .row-actions {
    margin-top: 18px;
    padding-top: 16px;
    border-top: 1px solid var(--border);
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 10px;
  }

  .action-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    padding: 12px 24px;
    border-radius: 10px;
    font-family: inherit;
    font-size: 14px;
    font-weight: 600;
    text-decoration: none;
    cursor: pointer;
    border: none;
  }
  .btn-submit { background: var(--accent); color: #fff; width: 100%; margin-top: 10px; }
  :root[data-theme='light'] .btn-submit { color: #fff; }
  .btn-submit:hover { opacity: 0.9; }
  .btn-secondary { background: transparent; border: 1px solid var(--border); color: var(--text); }
  .btn-secondary:hover { background: var(--border); }
  
  .btn-outline-blue { 
    background: transparent; 
    border: 2px solid #38bdf8; 
    color: #38bdf8; 
    font-weight: 700;
    transition: all 0.2s ease-in-out;
  }
  .btn-outline-blue:hover { 
    background: rgba(56, 189, 248, 0.15); 
    box-shadow: 0 0 15px rgba(56, 189, 248, 0.3);
  }
  :root[data-theme='light'] .btn-outline-blue {
    border-color: #0066cc;
    color: #0066cc;
  }
  :root[data-theme='light'] .btn-outline-blue:hover {
    background: rgba(0, 102, 204, 0.1);
  }
  
  .btn-success { background: var(--green); color: #fff; }
  .btn-success:hover { opacity: 0.9; }
  .btn-whatsapp { background: var(--whatsapp); color: #fff; gap: 6px; }
  .btn-whatsapp:hover { opacity: 0.9; }
  .btn-danger { background: var(--red); color: #fff; }
  .btn-danger:hover { opacity: 0.9; }
  .btn-close-shop { background: transparent; border: 1px solid rgba(248,113,113,0.3); color: var(--red); width: 100%; margin-top: 40px; }
  .btn-close-shop:hover { background: rgba(248,113,113,0.1); }

  .alert { padding: 16px; border-radius: 10px; font-size: 14px; margin-bottom: 24px; font-weight: 500; text-align: center; }
  .alert-ok { background: rgba(74,222,128,0.1); border: 1px solid var(--green); color: var(--green); }
  .alert-err { background: rgba(248,113,113,0.1); border: 1px solid var(--red); color: var(--red); }

  .empty-state {
    text-align: center;
    padding: 40px 20px;
    color: var(--muted);
    font-size: 13px;
    border: 2px dashed var(--border);
    border-radius: 14px;
  }

  .login-wrapper { min-height: 75vh; display: flex; align-items: center; justify-content: center; }
  .login-card {
    width: 100%;
    max-width: 360px;
    background: var(--surface);
    border: 1px solid var(--border);
    padding: 32px;
    border-radius: 16px;
    box-shadow: var(--shadow);
  }
  .login-head { font-size: 22px; font-weight: 800; text-align: center; margin-bottom: 24px; letter-spacing: -0.02em; }

  .divider-title {
    font-size: 12px;
    font-weight: 700;
    color: var(--accent);
    margin: 35px 0 15px;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    border-bottom: 1px solid var(--border);
    padding-bottom: 6px;
  }

  .back-link { font-size: 14px; color: var(--muted); text-decoration: none; margin-bottom: 24px; display: inline-block; }
  .back-link:hover { color: var(--accent); }
</style>

<script>
    function dukkanAra(inputID, rowClass) {
        var input = document.getElementById(inputID);
        var filter = input.value.toUpperCase();
        var rows = document.getElementsByClassName(rowClass);
        for (var i = 0; i < rows.length; i++) {
            var text = rows[i].textContent || rows[i].innerText;
            rows[i].style.display = text.toUpperCase().indexOf(filter) > -1 ? '' : 'none';
        }
    }

    function temaDegistir() {
        const mevcut = document.documentElement.getAttribute('data-theme') || 'dark';
        const yeni = mevcut === 'dark' ? 'light' : 'dark';
        document.documentElement.setAttribute('data-theme', yeni);
        localStorage.setItem('calkan_tema', yeni);
        document.getElementById('theme-lbl').innerText = yeni === 'dark' ? '🌙 Gece' : '☀️ Gündüz';
    }

    function sifreGoster(btn) {
        var input = btn.closest('.password-wrapper').querySelector('input');
        var svg = btn.querySelector('svg');
        if (input.type === 'password') {
            input.type = 'text';
            svg.innerHTML = '';
            var p = document.createElementNS('http://www.w3.org/2000/svg','path');
            p.setAttribute('stroke','currentColor'); p.setAttribute('stroke-width','2');
            p.setAttribute('stroke-linecap','round'); p.setAttribute('stroke-linejoin','round');
            p.setAttribute('d','M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24');
            svg.appendChild(p);
            var l = document.createElementNS('http://www.w3.org/2000/svg','line');
            l.setAttribute('stroke','currentColor'); l.setAttribute('stroke-width','2');
            l.setAttribute('stroke-linecap','round');
            l.setAttribute('x1','1'); l.setAttribute('y1','1'); l.setAttribute('x2','23'); l.setAttribute('y2','23');
            svg.appendChild(l);
        } else {
            input.type = 'password';
            svg.innerHTML = '';
            var p2 = document.createElementNS('http://www.w3.org/2000/svg','path');
            p2.setAttribute('stroke','currentColor'); p2.setAttribute('stroke-width','2');
            p2.setAttribute('stroke-linecap','round'); p2.setAttribute('stroke-linejoin','round');
            p2.setAttribute('d','M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z');
            svg.appendChild(p2);
            var c = document.createElementNS('http://www.w3.org/2000/svg','circle');
            c.setAttribute('stroke','currentColor'); c.setAttribute('stroke-width','2');
            c.setAttribute('cx','12'); c.setAttribute('cy','12'); r='3';
            svg.appendChild(c);
        }
    }

    document.addEventListener('DOMContentLoaded', () => {
        const kayitliTema = localStorage.getItem('calkan_tema') || 'dark';
        document.documentElement.setAttribute('data-theme', kayitliTema);
        const lbl = document.getElementById('theme-lbl');
        if (lbl) lbl.innerText = kayitliTema === 'dark' ? '🌙 Gece' : '☀️ Gündüz';
    });
</script>
";

        private static string GetHeader(string pageTitle = "", string pageBack = "", string backLabel = "Geri") =>
            "<!DOCTYPE html><html data-theme='dark'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'><title>Calkan GSM Mağaza</title>" +
            GetCSS() +
            "</head><body><div class='wrap'>" +
            "<div class='shop-nav'>" +
            "  <div class='shop-title'><div class='shop-badge'></div><div class='shop-name'>ÇALKAN GSM</div></div>" +
            "  <div class='nav-right'>" +
            "     <button class='theme-toggle' onclick='temaDegistir()'><span id='theme-lbl'>🌙 Gece</span></button>" +
            "     <div class='shop-status'>PANEL AKTİF</div>" +
            "  </div>" +
            "</div>" +
            (pageBack != "" ? $"<a href='{pageBack}' class='back-link'>← {backLabel}</a>" : "") +
            (pageTitle != "" ? $"<div class='view-heading'><div class='view-title'>{pageTitle}</div></div>" : "");

        private static string EyeIconHTML =>
            "<svg viewBox='0 0 24 24' fill='none' xmlns='http://www.w3.org/2000/svg'>" +
            "<path stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' d='M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z'/>" +
            "<circle stroke='currentColor' stroke-width='2' cx='12' cy='12' r='3'/>" +
            "</svg>";

        private static string Footer => "</div></body></html>";

        static void Main(string[] args)
        {
            ConfigYukle();
            
            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) 
                Directory.CreateDirectory(dir);
                
            TabloyuHazirla();

            Console.WriteLine($"🌐 Çalkan GSM Sunucusu Başlatılıyor...");
            StartServer(configPort);
            
            Thread.Sleep(Timeout.Infinite);
        }

        private static void StartServer(string port)
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://*:{port}/");

            try
            {
                listener.Start();
                Console.WriteLine($"🚀 Arka Plan Sunucusu Aktif! Port: {port}");
                Console.WriteLine($"👥 Aktif kullanıcı sayısı: {Kullanicilar.Count}");

                while (true)
                {
                    if (Interlocked.Increment(ref activeConnections) > MAX_CONNECTIONS)
                    {
                        Interlocked.Decrement(ref activeConnections);
                        continue;
                    }

                    HttpListenerContext context = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        HttpListenerRequest request = context.Request;
                        HttpListenerResponse response = context.Response;

                        try
                        {
                            string rawUrl = request.RawUrl ?? "/";
                            string method = request.HttpMethod;
                            string ip = GetIP(request);

                            if (IsRateLimited(ip))
                            {
                                response.StatusCode = 429;
                                byte[] limitMsg = Encoding.UTF8.GetBytes("Çok fazla istek gönderildi. Lütfen bekleyin.");
                                response.OutputStream.Write(limitMsg, 0, limitMsg.Length);
                                response.OutputStream.Close();
                                return;
                            }

                            if (method == "POST" && request.ContentLength64 > MAX_BODY_BYTES)
                            {
                                response.StatusCode = 413;
                                response.OutputStream.Close();
                                return;
                            }

                            if (rawUrl.Contains("favicon") || rawUrl.Contains(".ico") || rawUrl.Contains(".png"))
                            {
                                response.StatusCode = 404;
                                response.OutputStream.Close();
                                return;
                            }

                            bool oturumAcikMi = false;
                            string aktifKullanici = "";
                            Cookie? otuCookie = request.Cookies["calkan_session"];
                            if (otuCookie != null && otuCookie.Value == SessionValue)
                            {
                                oturumAcikMi = true;
                                Cookie? userCookie = request.Cookies["calkan_user"];
                                if (userCookie != null) aktifKullanici = userCookie.Value;
                            }

                            string html = "";

                            if (rawUrl == "/login" && method == "POST")
                            {
                                if (IsLocked(ip))
                                {
                                    html = "<!DOCTYPE html><html data-theme='dark'><head><meta charset='utf-8'>" + GetCSS() + "</head><body>" +
                                           "<div class='wrap'><div class='login-wrapper'><div class='login-card'>" +
                                           "<div class='login-head'>⛔ Erişim Engellendi</div>" +
                                           "<div class='alert alert-err'>Çok fazla hatalı deneme nedeniyle bu bilgisayar 15 dakika kilitlendi.</div>" +
                                           "</div></div></div></body></html>";
                                }
                                else
                                {
                                    string body = new StreamReader(request.InputStream, request.ContentEncoding).ReadToEnd();
                                    var nv = HttpUtility.ParseQueryString(body);
                                    string reqUser = nv["kullanici"] ?? "";
                                    string reqPass = nv["sifre"] ?? "";

                                    if (!string.IsNullOrEmpty(reqUser) && Kullanicilar.TryGetValue(reqUser, out var correctPass) && correctPass == reqPass)
                                    {
                                        ResetFail(ip);

                                        string expiresAttr = "";
                                        if (nv["hatirla"] == "on")
                                            expiresAttr = "; Expires=" + DateTime.Now.AddDays(30).ToString("R");

                                        response.Headers.Add("Set-Cookie", "calkan_session=" + SessionValue + "; Path=/" + expiresAttr + "; SameSite=Strict");
                                        response.Headers.Add("Set-Cookie", "calkan_user=" + reqUser + "; Path=/" + expiresAttr + "; SameSite=Strict");
                                        response.StatusCode = 302;
                                        response.Headers.Add("Location", "/");
                                        response.OutputStream.Close();
                                        return;
                                    }
                                    else
                                    {
                                        RecordFail(ip);
                                        html = "<!DOCTYPE html><html data-theme='dark'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>" + GetCSS() + "</head><body>" +
                                               "<div class='wrap'><div class='login-wrapper'><div class='login-card'>" +
                                               "<div class='login-head'>Hatalı Giriş</div>" +
                                               "<div class='alert alert-err'>Kullanıcı adı veya şifre yanlış.</div>" +
                                               "<a href='/' class='action-btn btn-secondary' style='width:100%;'>Tekrar Dene</a>" +
                                               "</div></div></div></body></html>";
                                    }
                                }
                            }
                            else if (rawUrl == "/logout")
                            {
                                response.Headers.Add("Set-Cookie", "calkan_session=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT");
                                response.Headers.Add("Set-Cookie", "calkan_user=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT");
                                response.StatusCode = 302;
                                response.Headers.Add("Location", "/");
                                response.OutputStream.Close();
                                return;
                            }
                            else if (!oturumAcikMi)
                            {
                                html = "<!DOCTYPE html><html data-theme='dark'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>" + GetCSS() + "</head><body>" +
                                       "<div class='wrap'><div class='login-wrapper'><div class='login-card'>" +
                                       "<div class='login-head'>Mağaza Oturumu</div>" +
                                       "<form action='/login' method='POST' autocomplete='off'>" +
                                       "<div class='form-field'><label>Kullanıcı Adı</label><input type='text' name='kullanici' class='form-input' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Şifre</label>" +
                                       "<div class='password-wrapper'>" +
                                       "<input type='password' name='sifre' id='sifre-input' class='form-input' required autocomplete='new-password'>" +
                                       "<button type='button' class='toggle-pass' onclick='sifreGoster(this)' title='Şifreyi göster/gizle'>" +
                                       EyeIconHTML +
                                       "</button>" +
                                       "</div></div>" +
                                       "<label class='remember-me'><input type='checkbox' name='hatirla'> Oturumu Açık Tut (30 Gün)</label>" +
                                       "<button type='submit' class='action-btn btn-submit'>Sistemi Aç</button>" +
                                       "</form></div></div></div></body></html>";
                            }
                            else if (rawUrl.StartsWith("/") && (rawUrl == "/" || rawUrl.StartsWith("/?")))
                            {
                                html = GetHeader() +
                                       "<div class='view-heading'><div class='view-title'>Mağaza Yönetim Tezgâhı</div><div class='view-sub'>Dükkan içi aktif tamirler ve vitrin envanter kontrolü.</div></div>" +
                                       
                                       "<a href='/kasa_defteri' class='action-btn btn-outline-blue' style='width:100%; margin-bottom:24px; justify-content:center; display:flex; font-size:15px; text-transform:uppercase; letter-spacing:0.03em; padding:15px;'>📒 Aksesuar Satış &amp; Kasa Defteri</a>" +

                                       "<div class='menu-layout'>" +
                                       "  <a href='/tamir_panel' class='menu-card'><span class='label'>Yeni Tamir Kaydı</span><span class='desc'>Müşteri bilgileri ve arıza durum kaydı oluşturun.</span></a>" +
                                       "  <a href='/vitrin_panel' class='menu-card'><span class='label'>Vitrine Ürün Ekle</span><span class='desc'>Satışa çıkarılacak yeni cihaz stok girişi yapın.</span></a>" +
                                       "  <a href='/tamir_listele' class='menu-card'><span class='label'>Tamir Bekleyenler</span><span class='desc'>Servisteki veya teslime hazır cihaz listesi.</span></a>" +
                                       "  <a href='/vitrin_listele' class='menu-card'><span class='label'>Vitrin Stok Listesi</span><span class='desc'>Şu an rafta satılmayı bekleyen güncel envanter.</span></a>" +
                                       "  <a href='/arsiv_panel' class='menu-card menu-full'><span class='label'>Geçmiş İşlemler Arşivi</span><span class='desc'>Tamamlanıp teslim edilmiş tamirler ve satılmış eski cihazların dökümü.</span></a>" +
                                       "</div>" +
                                       
                                       (aktifKullanici == "admin" ? "<a href='/yedek' class='action-btn btn-secondary' style='width:100%;margin-top:24px;justify-content:center;display:flex;'>💾 Veritabanı Yedeği İndir</a>" : "") +
                                       "<a href='/logout' class='action-btn btn-close-shop'>Güvenli Çıkış (Oturumu Kapat)</a>" +
                                       Footer;
                            }
                            
                            // ── SÜPER GÜVENLİ VE TAMAMEN AYRILMIŞ KASA DEFTERİ MODÜLÜ ──────────────────
                            else if (rawUrl.StartsWith("/kasa_defteri"))
                            {
                                var qs = HttpUtility.ParseQueryString(rawUrl.Contains("?") ? rawUrl.Substring(rawUrl.IndexOf('?') + 1) : "");
                                
                                string filtreTarihiInput = qs["tarih"] ?? DateTime.Now.ToString("yyyy-MM-dd");
                                DateTime seçiliFiltreGünü = DateTime.Now;
                                DateTime.TryParse(filtreTarihiInput, out seçiliFiltreGünü);
                                string aranacakSqlTarihKalıbı = seçiliFiltreGünü.ToString("dd.MM.yyyy");

                                var sb = new StringBuilder();
                                sb.Append(GetHeader("Kasa Defteri &amp; Aksesuar", "/", "Ana Menü"));
                                
                                // GÜN SEÇME ALANI
                                sb.Append("<div class='form-box' style='margin-bottom:24px; padding:20px; border-color:var(--accent);'>");
                                sb.Append("<form method='GET' action='/kasa_defteri' id='tarihForm'>");
                                sb.Append("<div class='form-field' style='margin-bottom:0; display:flex; align-items:center; gap:12px;'>");
                                sb.Append("<label style='margin-bottom:0; white-space:nowrap; font-size:14px; color:var(--text);'>📆 İncelemek İstediğiniz Gün:</label>");
                                sb.AppendFormat("<input type='date' name='tarih' value='{0}' class='form-input' style='padding:10px;' onchange='document.getElementById(\"tarihForm\").submit();'>", filtreTarihiInput);
                                sb.Append("</div>");
                                sb.Append("</form></div>");
                                
                                // İŞLEM EKLEME FORMU (DEVİR TAMAMEN GÜVENLİ)
                                sb.Append("<div class='form-box' style='margin-bottom:24px;'>");
                                sb.Append("<h3 style='margin-bottom:15px; font-size:16px; font-weight:700;'>➕ Yeni Kasa İşlemi Ekle</h3>");
                                sb.Append("<form action='/kasa_ekle' method='POST'>");
                                sb.AppendFormat("<input type='hidden' name='k_filtre_tarih' value='{0}'>", filtreTarihiInput);
                                
                                sb.Append("<div class='form-field'><label>Ürün / İşlem Açıklaması</label><input type='text' name='k_aciklama' class='form-input' placeholder='Örn: iPhone 11 Kılıf, Kırılmaz Cam, Sabah Kasası, Akşam Kapanış Devri' required autocomplete='off'></div>");
                                
                                sb.Append("<div class='form-field'><label>İşlem Tipi</label><select name='k_tip' class='form-input'>");
                                sb.Append("<option value='GELİR'>➕ Gelir (Satış / Nakit Girişi)</option>");
                                sb.Append("<option value='GİDER'>➖ Gider (Dükkan Masrafı / Ödeme)</option>");
                                sb.Append("<option value='DEVIR'>📒 Kasa Devri (Dünden Kalan / Yarına Aktarılan)</option>");
                                sb.Append("</select></div>");

                                sb.Append("<div class='form-field'><label>Ödeme Türü</label><select name='k_odeme_tipi' class='form-input'>");
                                sb.Append("<option value='NAKİT'>💵 Nakit</option>");
                                sb.Append("<option value='VİSA'>💳 Visa / Kredi Kartı</option>");
                                sb.Append("</select></div>");

                                sb.Append("<div class='form-field'><label>Tutar (TL)</label><input type='text' name='k_tutar' class='form-input' placeholder='Örn: 250' required autocomplete='off'></div>");
                                
                                sb.AppendFormat("<div class='form-field'><label>İşleme Yazılacak Tarih</label><input type='date' name='k_kayit_tarih' value='{0}' class='form-input'></div>", filtreTarihiInput);
                                
                                sb.Append("<button type='submit' class='action-btn btn-submit'>Kayıt İşle</button>");
                                sb.Append("</form></div>");

                                double nakitGelir = 0, visaGelir = 0;
                                double nakitGider = 0, visaGider = 0;
                                double nakitDevir = 0, visaDevir = 0;

                                var gelirSb = new StringBuilder();
                                var giderSb = new StringBuilder();
                                var devirSb = new StringBuilder();

                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();
                                        using (var cmd = new SqliteCommand("SELECT * FROM kasa WHERE tarih LIKE @gun ORDER BY id DESC;", connection))
                                        {
                                            cmd.Parameters.AddWithValue("@gun", aranacakSqlTarihKalıbı + "%");
                                            using (var r = cmd.ExecuteReader())
                                            {
                                                while (r.Read())
                                                {
                                                    string id = r["id"].ToString() ?? "";
                                                    string aciklama = r["aciklama"].ToString() ?? "";
                                                    string tip = r["tip"].ToString() ?? "GELİR";
                                                    string odemeTipi = r["odeme_tipi"].ToString() ?? "NAKİT";
                                                    string tutarStr = r["tutar"].ToString() ?? "0";
                                                    double.TryParse(tutarStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double tutar);
                                                    string tamTarih = r["tarih"].ToString() ?? "";
                                                    string saat = tamTarih.Length > 10 ? tamTarih.Substring(11) : "";

                                                    // Hesaplamalar
                                                    if (tip == "GİDER")
                                                    {
                                                        if (odemeTipi == "VİSA") visaGider += tutar; else nakitGider += tutar;
                                                    }
                                                    else if (tip == "DEVIR" || tip == "KASA DEVİR")
                                                    {
                                                        if (odemeTipi == "VİSA") visaDevir += tutar; else nakitDevir += tutar;
                                                    }
                                                    else 
                                                    {
                                                        if (odemeTipi == "VİSA") visaGelir += tutar; else nakitGelir += tutar;
                                                    }

                                                    // Row HTML Tasarımı
                                                    var itemRow = new StringBuilder();
                                                    string tipRenk = (tip == "GELİR") ? "var(--green)" : ((tip == "GİDER") ? "var(--red)" : "var(--accent)");
                                                    string isaret = tip == "GİDER" ? "-" : "+";
                                                    string odemeRozeti = odemeTipi == "VİSA" ? "💳 VİSA" : "💵 NAKİT";

                                                    itemRow.Append("<div class='shop-row' style='padding:14px; margin-bottom:10px;'>");
                                                    itemRow.Append("<div class='row-header' style='margin-bottom:0; align-items:center;'>");
                                                    itemRow.AppendFormat("<div><span class='tag' style='color:{0}; border-color:{0}; padding:2px 6px; font-size:10px; font-weight:700;'>{1}</span> ", tipRenk, tip == "DEVIR" ? "DEVİR" : tip);
                                                    itemRow.AppendFormat("<span class='tag' style='color:var(--muted); padding:2px 6px; font-size:10px;'>{0}</span>", odemeRozeti);
                                                    itemRow.AppendFormat("<br><span style='font-weight:600; font-size:14px; display:inline-block; margin-top:6px;'>{0}</span>", System.Web.HttpUtility.HtmlEncode(aciklama));
                                                    itemRow.AppendFormat("<br><span style='font-size:11px; color:var(--muted);'>🕒 Saat: {0}</span></div>", saat);
                                                    itemRow.AppendFormat("<div style='font-family:\"JetBrains Mono\",monospace; font-size:15px; font-weight:700; color:{0};'>{1}{2} TL</div>", tipRenk, isaret, tutar);
                                                    itemRow.Append("</div>");
                                                    
                                                    itemRow.Append("<div style='display:flex; justify-content:flex-end; margin-top:6px; border-top:1px dashed var(--border); padding-top:6px;'>");
                                                    itemRow.AppendFormat("<form action='/kasa_sil' method='POST' style='margin:0;' onsubmit=\"return confirm('{0} işlemini iptal etmek istiyor musunuz?');\">", System.Web.HttpUtility.HtmlEncode(aciklama));
                                                    itemRow.AppendFormat("<input type='hidden' name='id' value='{0}'>", id);
                                                    itemRow.AppendFormat("<input type='hidden' name='k_filtre_tarih' value='{0}'>", filtreTarihiInput);
                                                    itemRow.Append("<button type='submit' class='action-btn btn-danger' style='padding:3px 8px; font-size:10px; border-radius:5px;'>Sil</button>");
                                                    itemRow.Append("</form></div>");
                                                    itemRow.Append("</div>");

                                                    if (tip == "GİDER")
                                                        giderSb.Append(itemRow.ToString());
                                                    else if (tip == "DEVIR" || tip == "KASA DEVİR")
                                                        devirSb.Append(itemRow.ToString());
                                                    else
                                                        gelirSb.Append(itemRow.ToString());
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex) { sb.AppendFormat("<div class='alert alert-err'>Veritabanı Hatası: {0}</div>", ex.Message); }

                                // Matris Hesapları
                                double netNakitKasa = nakitGelir + nakitDevir - nakitGider;
                                double netVisaKasa = visaGelir + visaDevir - visaGider;
                                double genelNetKasa = netNakitKasa + netVisaKasa;

                                string nakitRenk = netNakitKasa >= 0 ? "var(--green)" : "var(--red)";
                                string visaRenk = netVisaKasa >= 0 ? "var(--accent)" : "var(--red)";
                                string genelRenk = genelNetKasa >= 0 ? "var(--green)" : "var(--red)";

                                // KASA ÖZET TABLOSU
                                sb.Append("<div style='display:grid; grid-template-columns:1fr 1fr; gap:12px; margin-bottom:15px;'>");
                                sb.AppendFormat("<div style='background:var(--surface); border:1px solid var(--border); padding:12px; border-radius:12px; text-align:center;'><span style='font-size:11px; color:var(--muted); font-weight:700;'>💵 NAKİT ÇEKMECESİ</span><br><strong style='color:{1}; font-family:\"JetBrains Mono\",monospace; font-size:16px;'>{0} TL</strong></div>", netNakitKasa, nakitRenk);
                                sb.AppendFormat("<div style='background:var(--surface); border:1px solid var(--border); padding:12px; border-radius:12px; text-align:center;'><span style='font-size:11px; color:var(--muted); font-weight:700;'>💳 VİSA (BANKA POS)</span><br><strong style='color:{1}; font-family:\"JetBrains Mono\",monospace; font-size:16px;'>{0} TL</strong></div>", netVisaKasa, visaRenk);
                                sb.Append("</div>");
                                
                                sb.AppendFormat("<div style='background:var(--surface); border:2px solid var(--border); padding:14px; border-radius:12px; text-align:center; margin-bottom:30px;'><span style='font-size:12px; color:var(--text); font-weight:700;'>📈 BU GÜNÜN GENEL NET DURUMU</span><br><strong style='color:{1}; font-family:\"JetBrains Mono\",monospace; font-size:20px;'>{0} TL</strong></div>", genelNetKasa, genelRenk);

                                // 3 AYRI LİSTE HALİNDE GÖSTERİM (TAMAMEN AYRILDI)
                                sb.Append("<div class='divider-title'>📒 GÜNLÜK KASA DEVİRLERİ LİSTESİ</div>");
                                if (devirSb.Length == 0)
                                    sb.Append("<div class='empty-state' style='margin-bottom:25px;'>Bu güne ait girilmiş veya yarına aktarılmış devir kaydı yok.</div>");
                                else
                                    sb.Append(devirSb.ToString());

                                sb.Append("<div class='divider-title'>🟢 AKSESUAR SATIŞLARI &amp; GELİRLER</div>");
                                if (gelirSb.Length == 0)
                                    sb.Append("<div class='empty-state' style='margin-bottom:25px;'>Bu güne ait kayıtlı aksesuar satışı veya gelir yok.</div>");
                                else
                                    sb.Append(gelirSb.ToString());

                                sb.Append("<div class='divider-title'>🔴 DÜKKAN MASRAFLARI &amp; GİDERLER</div>");
                                if (giderSb.Length == 0)
                                    sb.Append("<div class='empty-state'>Bu güne ait kayıtlı dükkan gideri bulunmuyor.</div>");
                                else
                                    sb.Append(giderSb.ToString());

                                sb.Append(Footer);
                                html = sb.ToString();
                            }
                            else if (rawUrl.StartsWith("/kasa_ekle") && method == "POST")
                            {
                                string body = new StreamReader(request.InputStream, request.ContentEncoding).ReadToEnd();
                                var nv = HttpUtility.ParseQueryString(body);
                                
                                string geriTarih = nv["k_filtre_tarih"] ?? DateTime.Now.ToString("yyyy-MM-dd");
                                string aciklama = nv["k_aciklama"] ?? "Aksesuar İşlemi";
                                string tip = nv["k_tip"] ?? "GELİR";
                                string odemeTipi = nv["k_odeme_tipi"] ?? "NAKİT";
                                string tutarStr = nv["k_tutar"] ?? "0";
                                double.TryParse(tutarStr.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double tutar);

                                string secilenTarihInput = nv["k_kayit_tarih"] ?? "";
                                string veritabanıTarihKayıt = "";
                                
                                if (DateTime.TryParse(secilenTarihInput, out DateTime cTarih))
                                {
                                    veritabanıTarihKayıt = cTarih.ToString("dd.MM.yyyy") + " " + DateTime.Now.ToString("HH:mm");
                                    geriTarih = secilenTarihInput;
                                }
                                else
                                {
                                    veritabanıTarihKayıt = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
                                }

                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();
                                        using (var cmd = new SqliteCommand("INSERT INTO kasa (aciklama, tip, tutar, tarih, odeme_tipi) VALUES (@aciklama, @tip, @tutar, @tarih, @odeme_tipi);", connection))
                                        {
                                            cmd.Parameters.AddWithValue("@aciklama", aciklama);
                                            cmd.Parameters.AddWithValue("@tip", tip);
                                            cmd.Parameters.AddWithValue("@tutar", tutar);
                                            cmd.Parameters.AddWithValue("@tarih", veritabanıTarihKayıt);
                                            cmd.Parameters.AddWithValue("@odeme_tipi", odemeTipi);
                                            cmd.ExecuteNonQuery();
                                        }
                                    }
                                    response.StatusCode = 302;
                                    response.Headers.Add("Location", "/kasa_defteri?tarih=" + geriTarih);
                                    response.OutputStream.Close();
                                    return;
                                }
                                catch (Exception ex)
                                {
                                    html = GetHeader("Kasa Hatası", "/kasa_defteri", "Geri") + $"<div class='alert alert-err'>{ex.Message}</div>" + Footer;
                                }
                            }
                            else if (rawUrl.StartsWith("/kasa_sil") && method == "POST")
                            {
                                string body = new StreamReader(request.InputStream).ReadToEnd();
                                var nv = HttpUtility.ParseQueryString(body);
                                string id = nv["id"];
                                string geriTarih = nv["k_filtre_tarih"] ?? DateTime.Now.ToString("yyyy-MM-dd");

                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();
                                        using (var cmd = new SqliteCommand("DELETE FROM kasa WHERE id = @id;", connection))
                                        {
                                            cmd.Parameters.AddWithValue("@id", id);
                                            cmd.ExecuteNonQuery();
                                        }
                                    }
                                }
                                catch { }

                                response.StatusCode = 302;
                                response.Headers.Add("Location", "/kasa_defteri?tarih=" + geriTarih);
                                response.OutputStream.Close();
                                return;
                            }
                            // ──────────────────────────────────────────────────────────────────────────────────
                            
                            else if (rawUrl == "/tamir_panel")
                            {
                                html = GetHeader("Yeni Tamir Kabulü", "/", "Ana Menü") +
                                       "<div class='form-box'>" +
                                       "<form action='/ekle' method='POST'>" +
                                       "<input type='hidden' name='tip' value='tamir'>" +
                                       "<div class='form-field'><label>Müşteri Adı Soyadı</label><input type='text' name='musteri' class='form-input' placeholder='Müşteri İsim Soyisim' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Müşteri Telefon No</label><input type='text' name='telefon' class='form-input' placeholder='05xx xxx xx xx' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Cihaz Markası</label><input type='text' name='c_marka' class='form-input' placeholder='Örn: Samsung, Apple' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Cihaz Modeli</label><input type='text' name='c_model' class='form-input' placeholder='Örn: Galaxy S23, iPhone 11' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Yapılacak Arıza İşlemi</label><input type='text' name='islem' class='form-input' placeholder='Örn: Batarya Değişimi, Şarj Soketi' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Alınacak Tahmini Ücret (TL)</label><input type='text' name='t_fiyat' class='form-input' placeholder='Müşteriye verilen fiyat' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Arıza / Kozmetik Notu</label><input type='text' name='ariza' class='form-input' placeholder='Ekranda çizik var, şifre alındı vs.' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Tamir Durumu</label><input type='text' name='tamir_durum' class='form-input' placeholder='Bekliyor, Tamirde, Hazır...' autocomplete='off'></div>" +
                                       "<button type='submit' class='action-btn btn-submit'>Kabulü Onayla ve Kaydet</button>" +
                                       "</form>" +
                                       "</div>" +
                                       Footer;
                            }
                            else if (rawUrl == "/vitrin_panel")
                            {
                                html = GetHeader("Vitrin Envanter Girişi", "/", "Ana Menü") +
                                       "<div class='form-box'>" +
                                       "<form action='/ekle' method='POST'>" +
                                       "<input type='hidden' name='tip' value='vitrin'>" +
                                       "<div class='form-field'><label>Cihaz Markası &amp; Modeli</label><input type='text' name='v_model' class='form-input' placeholder='Örn: iPhone 13 Pro' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Kapasite (GB)</label><input type='text' name='v_gb' class='form-input' placeholder='Örn: 128 GB' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Kasa Rengi</label><input type='text' name='v_renk' class='form-input' placeholder='Örn: Grafiti' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Pil Yüzdesi</label><input type='text' name='v_pil' class='form-input' placeholder='Örn: %85' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Garanti Durumu</label><input type='text' name='v_garanti' class='form-input' placeholder='Örn: 6 Ay Dükkan veya Yok' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>IMEI Numarası</label><input type='text' name='v_imei' class='form-input' placeholder='Opsiyonel' autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Dükkan Alış Fiyatı / Maliyet (TL)</label><input type='text' name='v_alis' class='form-input' placeholder='Maliyet (Sadece size görünür)' autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Vitrin Satış Fiyatı (TL)</label><input type='text' name='v_satis' class='form-input' placeholder='Etiket fiyatı' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Kutu / Fatura / Aksesuar Detayı</label><input type='text' name='v_kutu' class='form-input' placeholder='Kutu var, orijinal şarj aleti vs.' autocomplete='off'></div>" +
                                       "<button type='submit' class='action-btn btn-submit'>Cihazı Vitrine Koy (Stoğa Ekle)</button>" +
                                       "</form>" +
                                       "</div>" +
                                       Footer;
                            }
                            else if (rawUrl == "/ekle" && method == "POST")
                            {
                                string body = new StreamReader(request.InputStream, request.ContentEncoding).ReadToEnd();
                                var nv = HttpUtility.ParseQueryString(body);
                                string tip = nv["tip"] ?? "tamir";

                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();
                                        string query = "INSERT INTO vitrin (marka, model, imei, alinma_tarihi, fiyat, satis_fiyati, durum, kutu_fatura, garanti) VALUES (@marka, @model, @imei, @alinma, @fiyat, @satis, @durum, @kutu, @garanti);";
                                        using (var command = new SqliteCommand(query, connection))
                                        {
                                            if (tip == "tamir")
                                            {
                                                command.Parameters.AddWithValue("@marka", nv["musteri"] ?? "");
                                                command.Parameters.AddWithValue("@model", nv["telefon"] ?? "");
                                                command.Parameters.AddWithValue("@imei", (nv["c_marka"] + " " + nv["c_model"]).Trim());
                                                command.Parameters.AddWithValue("@alinma", DateTime.Now.ToString("dd.MM.yyyy"));
                                                command.Parameters.AddWithValue("@fiyat", nv["islem"] ?? "");
                                                command.Parameters.AddWithValue("@satis", nv["t_fiyat"] ?? "");
                                                command.Parameters.AddWithValue("@durum", "TAMIR");
                                                command.Parameters.AddWithValue("@kutu", nv["ariza"] ?? "");
                                                command.Parameters.AddWithValue("@garanti", nv["tamir_durum"] ?? "Bekliyor");
                                            }
                                            else
                                            {
                                                string birlesikOzellikler = string.Format("{0} | {1} | Pil: {2}", nv["v_gb"], nv["v_renk"], nv["v_pil"]);
                                                command.Parameters.AddWithValue("@marka", birlesikOzellikler);
                                                command.Parameters.AddWithValue("@model", nv["v_model"] ?? "");
                                                command.Parameters.AddWithValue("@imei", nv["v_imei"] ?? "");
                                                command.Parameters.AddWithValue("@alinma", DateTime.Now.ToString("dd.MM.yyyy"));
                                                command.Parameters.AddWithValue("@fiyat", nv["v_alis"] ?? "");
                                                command.Parameters.AddWithValue("@satis", nv["v_satis"] ?? "");
                                                command.Parameters.AddWithValue("@durum", "VITRIN");
                                                command.Parameters.AddWithValue("@kutu", nv["v_kutu"] ?? "");
                                                command.Parameters.AddWithValue("@garanti", nv["v_garanti"] ?? "");
                                            }
                                            command.ExecuteNonQuery();
                                        }
                                    }
                                    response.StatusCode = 302;
                                    response.Headers.Add("Location", tip == "tamir" ? "/tamir_listele" : "/vitrin_listele");
                                    response.OutputStream.Close();
                                    return;
                                }
                                catch (Exception ex)
                                {
                                    html = GetHeader("Kayıt Hatası", "/", "Ana Menü") + $"<div class='alert alert-err'>{ex.Message}</div>" + Footer;
                                }
                            }
                            else if (rawUrl == "/tamir_listele")
                            {
                                var sb = new StringBuilder();
                                sb.Append(GetHeader("Mevcut Tamir Kuyruğu", "/", "Ana Menü"));
                                sb.Append("<div class='search-container'><input type='text' id='araT' onkeyup=\"dukkanAra('araT', 'row-tamir')\" placeholder='Müşteri adı, cihaz modeli veya telefon ile dükkanda hızlı ara...' class='search-bar'></div>");

                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();
                                        using (var cmd = new SqliteCommand("SELECT * FROM vitrin WHERE durum='TAMIR' ORDER BY id DESC;", connection))
                                        using (var r = cmd.ExecuteReader())
                                        {
                                            if (!r.HasRows)
                                            {
                                                sb.Append("<div class='empty-state'>Şu an tezgahta bekleyen veya işlem gören tamir cihazı yok.</div>");
                                            }
                                            else
                                            {
                                                while (r.Read())
                                                {
                                                    string rawPhone = r["model"]?.ToString() ?? "";
                                                    string cleanPhone = rawPhone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");
                                                    if (cleanPhone.StartsWith("0")) cleanPhone = cleanPhone.Substring(1);
                                                    if (!cleanPhone.StartsWith("90") && cleanPhone.Length == 10) cleanPhone = "90" + cleanPhone;

                                                    string alinmaTarihi = r["alinma_tarihi"]?.ToString() ?? "";
                                                    string beklemeBadge = "";
                                                    if (DateTime.TryParseExact(alinmaTarihi, "dd.MM.yyyy", null, System.Globalization.DateTimeStyles.None, out DateTime alinmaD))
                                                    {
                                                        int gun = (DateTime.Now - alinmaD).Days;
                                                        string badgeColor = gun >= 7 ? "#f87171" : gun >= 3 ? "#fb923c" : "#4ade80";
                                                        beklemeBadge = $"<span class='tag' style='border-color:{badgeColor};color:{badgeColor};'>⏳ {gun} gündür bekliyor</span>";
                                                    }

                                                    string cihazAdi = r["imei"]?.ToString() ?? "";
                                                    string waHazir = HttpUtility.UrlEncode($"Merhaba, Çalkan GSM'den yazıyoruz. {cihazAdi} cihazınızın tamiri tamamlandı, teslim alabilirsiniz. İyi günler 🙂");
                                                    string waParca = HttpUtility.UrlEncode($"Merhaba, Çalkan GSM'den yazıyoruz. {cihazAdi} cihazınız için gerekli parça temin ediliyor, tahmini süre hakkında sizi bilgilendireceğiz.");
                                                    string waFiyat = HttpUtility.UrlEncode($"Merhaba, Çalkan GSM'den yazıyoruz. {cihazAdi} cihazınız incelendi. Tamir ücreti {r["satis_fiyati"]} TL olacaktır. Onayınız halinde işleme başlayabiliriz.");

                                                    sb.Append("<div class='shop-row row-tamir'>");
                                                    sb.Append("<div class='row-header'>");
                                                    sb.AppendFormat("<div class='row-title'>{0}</div>", r["marka"]);
                                                    sb.AppendFormat("<div class='row-price'>{0} TL</div>", r["satis_fiyati"]);
                                                    sb.Append("</div>");
                                                    sb.Append("<div class='tags'>");
                                                    sb.AppendFormat("<span class='tag active'>📱 {0}</span>", r["imei"]);
                                                    sb.AppendFormat("<span class='tag'>📅 Kabul: {0}</span>", alinmaTarihi);
                                                    sb.AppendFormat("<span class='tag'>📞 {0}</span>", rawPhone);
                                                    sb.AppendFormat("<span class='tag'>🛠️ {0}</span>", r["fiyat"]);
                                                    
                                                    string durumVal = r["garanti"]?.ToString() ?? "Bekliyor";
                                                    string durumColor = durumVal == "Hazır" ? "#4ade80" : durumVal == "Tamirde" ? "#38bdf8" : durumVal == "Parça Bekleniyor" ? "#fb923c" : "#94a3b8";
                                                    sb.AppendFormat("<span class='tag' style='border-color:{1};color:{1};font-weight:600;'>{0}</span>", durumVal, durumColor);
                                                    if (!string.IsNullOrEmpty(beklemeBadge)) sb.Append(beklemeBadge);
                                                    sb.Append("</div>");
                                                    sb.AppendFormat("<div class='row-notes'><strong>Arıza Notu:</strong> {0}</div>", r["kutu_fatura"]);

                                                    sb.Append("<div class='row-actions' style='flex-wrap:wrap; gap:8px;'>");
                                                    sb.AppendFormat("<a href='/duzenle?id={0}&tip=tamir' class='action-btn btn-secondary' style='padding:8px 14px; font-size:13px;'>✏️ Düzenle</a>", r["id"]);
                                                    sb.Append("<div style='display:flex; gap:6px; flex-wrap:wrap;'>");
                                                    sb.AppendFormat("<a href='https://wa.me/{0}?text={1}' target='_blank' class='action-btn btn-whatsapp' style='padding:8px 12px; font-size:12px;'>✅ Hazır</a>", cleanPhone, waHazir);
                                                    sb.AppendFormat("<a href='https://wa.me/{0}?text={1}' target='_blank' class='action-btn btn-secondary' style='padding:8px 12px; font-size:12px; border-color:#fb923c; color:#fb923c;'>📦 Parça</a>", cleanPhone, waParca);
                                                    sb.AppendFormat("<a href='https://wa.me/{0}?text={1}' target='_blank' class='action-btn btn-secondary' style='padding:8px 12px; font-size:12px;'>💰 Fiyat</a>", cleanPhone, waFiyat);
                                                    sb.Append("</div>");
                                                    sb.Append("<form action='/sil' method='POST' style='margin:0;'>");
                                                    sb.AppendFormat("<input type='hidden' name='id' value='{0}'>", r["id"]);
                                                    sb.Append("<input type='hidden' name='git' value='tamir'>");
                                                    sb.Append("<button type='submit' class='action-btn btn-success' style='padding:8px 14px; font-size:13px;'>📬 Teslim Et</button>");
                                                    sb.Append("</form></div></div>");
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    sb.AppendFormat("<div class='alert alert-err'>Hata: {0}</div>", ex.Message);
                                }
                                sb.Append(Footer);
                                html = sb.ToString();
                            }
                            else if (rawUrl == "/vitrin_listele")
                            {
                                var sb = new StringBuilder();
                                sb.Append(GetHeader("Vitrin Rafları Görüntüle", "/", "Ana Menü"));
                                sb.Append("<div class='search-container'><input type='text' id='araV' onkeyup=\"dukkanAra('araV', 'row-vitrin')\" placeholder='Model adı, hafıza veya özellik yazarak rafta ara...' class='search-bar'></div>");

                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();
                                        using (var cmd = new SqliteCommand("SELECT * FROM vitrin WHERE durum='VITRIN' ORDER BY id DESC;", connection))
                                        using (var r = cmd.ExecuteReader())
                                        {
                                            if (!r.HasRows)
                                            {
                                                sb.Append("<div class='empty-state'>Şu an dükkan vitrininde satılık cihaz bulunmuyor.</div>");
                                            }
                                            else
                                            {
                                                while (r.Read())
                                                {
                                                    sb.Append("<div class='shop-row row-vitrin'>");
                                                    sb.Append("<div class='row-header'>");
                                                    sb.AppendFormat("<div class='row-title'>{0}</div>", r["model"]);
                                                    sb.AppendFormat("<div class='row-price'>{0} TL</div>", r["satis_fiyati"]);
                                                    sb.Append("</div>");
                                                    sb.Append("<div class='tags'>");
                                                    sb.AppendFormat("<span class='tag active'>🎨 {0}</span>", r["marka"]);
                                                    sb.AppendFormat("<span class='tag'>🆔 IMEI: {0}</span>", r["imei"]);
                                                    if (!string.IsNullOrEmpty(r["alinma_tarihi"]?.ToString()))
                                                        sb.AppendFormat("<span class='tag'>📅 Eklendi: {0}</span>", r["alinma_tarihi"]);
                                                    sb.AppendFormat("<span class='tag'>🛡️ Garanti: {0}</span>", r["garanti"]);
                                                    sb.AppendFormat("<span class='tag'>📦 {0}</span>", r["kutu_fatura"]);
                                                    sb.Append("</div>");
                                                    sb.AppendFormat("<div style='font-size:12px; color:var(--muted); font-family:monospace; padding-left:4px;'>Dükkan Maliyeti: {0} TL</div>", r["fiyat"]);
                                                    sb.Append("<div class='row-actions'>");
                                                    sb.Append("<span style='font-size:12px; color:var(--accent); font-weight:600;'>DURUM: VİTRİNDE</span>");
                                                    sb.Append("<div style='display:flex; gap:8px;'>");
                                                    sb.AppendFormat("<a href='/duzenle?id={0}&tip=vitrin' class='action-btn btn-secondary' style='padding:8px 14px; font-size:13px;'>✏️ Düzenle</a>", r["id"]);
                                                    sb.Append("<form action='/sil' method='POST' style='margin:0;'>");
                                                    sb.AppendFormat("<input type='hidden' name='id' value='{0}'>", r["id"]);
                                                    sb.Append("<input type='hidden' name='git' value='vitrin'>");
                                                    sb.Append("<button type='submit' class='action-btn btn-submit' style='padding:8px 20px; width:auto; margin:0;'>Cihazı Sat</button>");
                                                    sb.Append("</form></div></div></div>");
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    sb.AppendFormat("<div class='alert alert-err'>Hata: {0}</div>", ex.Message);
                                }
                                sb.Append(Footer);
                                html = sb.ToString();
                            }
                            else if (rawUrl == "/arsiv_panel")
                            {
                                var sb = new StringBuilder();
                                sb.Append(GetHeader("Merkezi Mağaza Arşiv Kayıtları", "/", "Ana Menü"));

                                sb.Append("<div class='divider-title'>Teslim Edilen Onarımlar</div>");
                                sb.Append("<div class='search-container'><input type='text' id='araAT' onkeyup=\"dukkanAra('araAT', 'row-arc-t')\" placeholder='Geçmiş müşteri adı veya cihaz modeli...' class='search-bar'></div>");

                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();
                                        using (var cmd = new SqliteCommand("SELECT * FROM vitrin WHERE durum='TESLIM_EDILDI' ORDER BY id DESC;", connection))
                                        using (var r = cmd.ExecuteReader())
                                        {
                                            if (!r.HasRows)
                                            {
                                                sb.Append("<div class='empty-state' style='padding:25px;'>Teslimat geçmişi temiz, kayıt yok.</div>");
                                            }
                                            else
                                            {
                                                while (r.Read())
                                                {
                                                    sb.Append("<div class='shop-row row-arc-t' style='opacity: 0.85;'>");
                                                    sb.Append("<div class='row-header'>");
                                                    sb.AppendFormat("<div class='row-title'>{0}</div>", r["marka"]);
                                                    sb.AppendFormat("<div class='row-price' style='color:var(--green);'>{0} TL</div>", r["satis_fiyati"]);
                                                    sb.Append("</div>");
                                                    sb.Append("<div class='tags'>");
                                                    sb.AppendFormat("<span class='tag'>📱 {0}</span>", r["imei"]);
                                                    sb.AppendFormat("<span class='tag'>📅 Kabul: {0}</span>", r["alinma_tarihi"]);
                                                    sb.AppendFormat("<span class='tag'>🛠️ {0}</span>", r["fiyat"]);
                                                    if (!string.IsNullOrEmpty(r["teslim_tarihi"]?.ToString()))
                                                        sb.AppendFormat("<span class='tag' style='color:var(--green);border-color:var(--green);'>✅ Teslim: {0}</span>", r["teslim_tarihi"]);
                                                    sb.Append("</div>");
                                                    sb.Append("<div class='row-actions' style='margin-top:10px; padding-top:10px;'>");
                                                    sb.Append("<span style='font-size:12px; color:var(--green); font-weight:650;'>✓ TESLİM EDİLDİ</span>");
                                                    sb.Append("<div style='display:flex; gap:8px;'>");
                                                    sb.AppendFormat("<a href='/duzenle?id={0}&tip=arsiv_tamir' class='action-btn btn-secondary' style='padding:6px 14px; font-size:12px;'>Tarih Düzelt</a>", r["id"]);
                                                    sb.Append("<form action='/sil' method='POST' onsubmit=\"return confirm('Silmek istediginize emin misiniz?');\" style='margin:0;'>");
                                                    sb.AppendFormat("<input type='hidden' name='id' value='{0}'>", r["id"]);
                                                    sb.Append("<input type='hidden' name='git' value='arsiv'>");
                                                    sb.Append("<button type='submit' class='action-btn btn-danger' style='padding:6px 14px; font-size:12px;'>Kayıt Sil</button>");
                                                    sb.Append("</form></div></div></div>");
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex) { sb.AppendFormat("<div class='alert alert-err'>Hata: {0}</div>", ex.Message); }

                                sb.Append("<div class='divider-title'>Kasa Satış Geçmişi</div>");
                                sb.Append("<div class='search-container'><input type='text' id='araAV' onkeyup=\"dukkanAra('araAV', 'row-arc-v')\" placeholder='Satılmış eski cihaz modeli veya IMEI...' class='search-bar'></div>");

                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();
                                        using (var cmd = new SqliteCommand("SELECT * FROM vitrin WHERE durum='SATILDI' ORDER BY id DESC;", connection))
                                        using (var r = cmd.ExecuteReader())
                                        {
                                            if (!r.HasRows)
                                            {
                                                sb.Append("<div class='empty-state' style='padding:25px;'>Henüz dükkandan satışı yapılmış cihaz kaydı yok.</div>");
                                            }
                                            else
                                            {
                                                while (r.Read())
                                                {
                                                    sb.Append("<div class='shop-row row-arc-v' style='opacity: 0.85;'>");
                                                    sb.Append("<div class='row-header'>");
                                                    sb.AppendFormat("<div class='row-title'>{0}</div>", r["model"]);
                                                    sb.AppendFormat("<div class='row-price' style='color:var(--green);'>{0} TL</div>", r["satis_fiyati"]);
                                                    sb.Append("</div>");
                                                    sb.Append("<div class='tags'>");
                                                    sb.AppendFormat("<span class='tag'>{0}</span>", r["marka"]);
                                                    sb.AppendFormat("<span class='tag'>IMEI: {0}</span>", r["imei"]);
                                                    if (!string.IsNullOrEmpty(r["alinma_tarihi"]?.ToString()))
                                                        sb.AppendFormat("<span class='tag'>📅 Eklendi: {0}</span>", r["alinma_tarihi"]);
                                                    if (!string.IsNullOrEmpty(r["teslim_tarihi"]?.ToString()))
                                                        sb.AppendFormat("<span class='tag' style='color:var(--green);border-color:var(--green);'>💰 Satış: {0}</span>", r["teslim_tarihi"]);
                                                    sb.Append("</div>");
                                                    sb.Append("<div class='row-actions' style='margin-top:10px; padding-top:10px;'>");
                                                    sb.Append("<span style='font-size:12px; color:var(--accent); font-weight:650;'>💰 SATILDI</span>");
                                                    sb.Append("<form action='/sil' method='POST' onsubmit=\"return confirm('Bu satış kaydını arşivden tamamen silmek istediğinize emin misiniz?');\" style='margin:0;'>");
                                                    sb.AppendFormat("<input type='hidden' name='id' value='{0}'>", r["id"]);
                                                    sb.Append("<input type='hidden' name='git' value='arsiv'>");
                                                    sb.Append("<button type='submit' class='action-btn btn-danger' style='padding:6px 14px; font-size:12px;'>Kayıt Sil</button>");
                                                    sb.Append("</form></div>");
                                                    sb.Append("</div>");
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex) { sb.AppendFormat("<div class='alert alert-err'>Hata: {0}</div>", ex.Message); }

                                sb.Append(Footer);
                                html = sb.ToString();
                            }
                            else if (rawUrl == "/yedek" && aktifKullanici == "admin")
                            {
                                try
                                {
                                    byte[] dbBytes = File.ReadAllBytes(dbPath);
                                    string fileName = "calkan_gsm_yedek_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".db";
                                    response.ContentType = "application/octet-stream";
                                    response.Headers.Add("Content-Disposition", "attachment; filename=" + fileName);
                                    response.ContentLength64 = dbBytes.Length;
                                    response.OutputStream.Write(dbBytes, 0, dbBytes.Length);
                                    response.OutputStream.Close();
                                    return;
                                }
                                catch (Exception ex)
                                {
                                    html = GetHeader("Hata", "/", "Ana Menu") + "<div class='alert alert-err'>" + ex.Message + "</div>" + Footer;
                                }
                            }
                            else if (rawUrl.StartsWith("/duzenle") && method == "GET")
                            {
                                var qs = HttpUtility.ParseQueryString(rawUrl.Contains("?") ? rawUrl.Substring(rawUrl.IndexOf('?') + 1) : "");
                                string editId = qs["id"] ?? "";
                                string tip = qs["tip"] ?? "tamir";
                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();
                                        using (var cmd = new SqliteCommand("SELECT * FROM vitrin WHERE id=@id;", connection))
                                        {
                                            cmd.Parameters.AddWithValue("@id", editId);
                                            using (var r = cmd.ExecuteReader())
                                            {
                                                if (!r.Read())
                                                {
                                                    html = GetHeader("Kayit Bulunamadi", tip == "tamir" ? "/tamir_listele" : "/vitrin_listele", "Geri") +
                                                           "<div class='alert alert-err'>Kayit bulunamadi.</div>" + Footer;
                                                }
                                                else if (tip == "tamir")
                                                {
                                                    string imeiVal = r["imei"]?.ToString() ?? "";
                                                    int spIdx = imeiVal.IndexOf(' ');
                                                    string cMarka = spIdx > 0 ? imeiVal.Substring(0, spIdx) : imeiVal;
                                                    string cModel = spIdx > 0 ? imeiVal.Substring(spIdx + 1) : "";
                                                    html = GetHeader("Tamir Kaydini Duzenle", "/tamir_listele", "Tamir Listesi") +
                                                           "<div class='form-box'><form action='/duzenle_kaydet' method='POST'>" +
                                                           "<input type='hidden' name='id' value='" + editId + "'>" +
                                                           "<input type='hidden' name='tip' value='tamir'>" +
                                                           "<div class='form-field'><label>Musteri Adi Soyadi</label><input type='text' name='musteri' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["marka"]?.ToString() ?? "") + "' required autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Musteri Telefon</label><input type='text' name='telefon' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["model"]?.ToString() ?? "") + "' required autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Cihaz Markasi</label><input type='text' name='c_marka' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(cMarka) + "' required autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Cihaz Modeli</label><input type='text' name='c_model' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(cModel) + "' autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Yapilan Islem</label><input type='text' name='islem' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["fiyat"]?.ToString() ?? "") + "' required autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Tamir Ucreti (TL)</label><input type='text' name='t_fiyat' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["satis_fiyati"]?.ToString() ?? "") + "' required autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Ariza / Kozmetik Notu</label><input type='text' name='ariza' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["kutu_fatura"]?.ToString() ?? "") + "' required autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Tamir Durumu</label><input type='text' name='tamir_durum' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["garanti"]?.ToString() ?? "Bekliyor") + "' placeholder='Bekliyor, Tamirde, Hazır...' autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Kabul Tarihi</label><input type='text' name='kabul_tarihi' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["alinma_tarihi"]?.ToString() ?? DateTime.Now.ToString("dd.MM.yyyy")) + "' placeholder='GG.AA.YYYY' autocomplete='off'></div>" +
                                                           "<button type='submit' class='action-btn btn-submit'>Degisiklikleri Kaydet</button>" +
                                                           "</form></div>" + Footer;
                                                }
                                                else if (tip == "arsiv_tamir")
                                                {
                                                    html = GetHeader("Teslim Tarihini Duzenle", "/arsiv_panel", "Arsiv") +
                                                           "<div class='form-box'><form action='/duzenle_kaydet' method='POST'>" +
                                                           "<input type='hidden' name='id' value='" + editId + "'>" +
                                                           "<input type='hidden' name='tip' value='arsiv_tamir'>" +
                                                           "<div class='form-field'><label>Musteri</label><input type='text' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["marka"]?.ToString() ?? "") + "' disabled></div>" +
                                                           "<div class='form-field'><label>Cihaz</label><input type='text' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["imei"]?.ToString() ?? "") + "' disabled></div>" +
                                                           "<div class='form-field'><label>Teslim Tarihi</label><input type='text' name='teslim_tarihi' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["teslim_tarihi"]?.ToString() ?? DateTime.Now.ToString("dd.MM.yyyy")) + "' placeholder='GG.AA.YYYY' autocomplete='off'></div>" +
                                                           "<button type='submit' class='action-btn btn-submit'>Tarihi Kaydet</button>" +
                                                           "</form></div>" + Footer;
                                                }
                                                else
                                                {
                                                    string markaVal = r["marka"]?.ToString() ?? "";
                                                    string[] mp = markaVal.Split('|');
                                                    string vGb   = mp.Length > 0 ? mp[0].Trim() : "";
                                                    string vRenk = mp.Length > 1 ? mp[1].Trim() : "";
                                                    string vPil  = mp.Length > 2 ? mp[2].Replace("Pil:", "").Trim() : "";
                                                    html = GetHeader("Vitrin Kaydini Duzenle", "/vitrin_listele", "Vitrin Listesi") +
                                                           "<div class='form-box'><form action='/duzenle_kaydet' method='POST'>" +
                                                           "<input type='hidden' name='id' value='" + editId + "'>" +
                                                           "<input type='hidden' name='tip' value='vitrin'>" +
                                                           "<div class='form-field'><label>Cihaz Markasi &amp; Modeli</label><input type='text' name='v_model' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["model"]?.ToString() ?? "") + "' required autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Kapasite (GB)</label><input type='text' name='v_gb' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(vGb) + "' required autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Kasa Rengi</label><input type='text' name='v_renk' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(vRenk) + "' required autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Pil Yuzdesi</label><input type='text' name='v_pil' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(vPil) + "' required autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Garanti Durumu</label><input type='text' name='v_garanti' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["garanti"]?.ToString() ?? "") + "' required autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>IMEI Numarasi</label><input type='text' name='v_imei' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["imei"]?.ToString() ?? "") + "' autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Alis Fiyati / Maliyet (TL)</label><input type='text' name='v_alis' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["fiyat"]?.ToString() ?? "") + "' autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Satis Fiyati (TL)</label><input type='text' name='v_satis' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["satis_fiyati"]?.ToString() ?? "") + "' required autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Kutu / Fatura / Aksesuar</label><input type='text' name='v_kutu' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["kutu_fatura"]?.ToString() ?? "") + "' autocomplete='off'></div>" +
                                                           "<div class='form-field'><label>Eklenme Tarihi</label><input type='text' name='v_tarih' class='form-input' value='" + System.Web.HttpUtility.HtmlEncode(r["alinma_tarihi"]?.ToString() ?? DateTime.Now.ToString("dd.MM.yyyy")) + "' placeholder='GG.AA.YYYY' autocomplete='off'></div>" +
                                                           "<button type='submit' class='action-btn btn-submit'>Degisiklikleri Kaydet</button>" +
                                                           "</form></div>" + Footer;
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    html = GetHeader("Hata", "/", "Ana Menu") + "<div class='alert alert-err'>" + ex.Message + "</div>" + Footer;
                                }
                            }
                            else if (rawUrl == "/duzenle_kaydet" && method == "POST")
                            {
                                string body = new StreamReader(request.InputStream, request.ContentEncoding).ReadToEnd();
                                var nv = HttpUtility.ParseQueryString(body);
                                string editId = nv["id"] ?? "";
                                string tip = nv["tip"] ?? "tamir";
                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();
                                        if (tip == "tamir")
                                        {
                                            using (var cmd = new SqliteCommand("UPDATE vitrin SET marka=@marka, model=@model, imei=@imei, fiyat=@fiyat, satis_fiyati=@satis, kutu_fatura=@kutu, garanti=@garanti, alinma_tarihi=@alinma WHERE id=@id;", connection))
                                            {
                                                cmd.Parameters.AddWithValue("@marka", nv["musteri"] ?? "");
                                                cmd.Parameters.AddWithValue("@model", nv["telefon"] ?? "");
                                                cmd.Parameters.AddWithValue("@imei", ((nv["c_marka"] ?? "") + " " + (nv["c_model"] ?? "")).Trim());
                                                cmd.Parameters.AddWithValue("@fiyat", nv["islem"] ?? "");
                                                cmd.Parameters.AddWithValue("@satis", nv["t_fiyat"] ?? "");
                                                cmd.Parameters.AddWithValue("@kutu", nv["ariza"] ?? "");
                                                cmd.Parameters.AddWithValue("@garanti", nv["tamir_durum"] ?? "Bekliyor");
                                                string kabulTarihi = nv["kabul_tarihi"] ?? "";
                                                if (string.IsNullOrWhiteSpace(kabulTarihi)) kabulTarihi = DateTime.Now.ToString("dd.MM.yyyy");
                                                cmd.Parameters.AddWithValue("@alinma", kabulTarihi);
                                                cmd.Parameters.AddWithValue("@id", editId);
                                                cmd.ExecuteNonQuery();
                                            }
                                        }
                                        else if (tip == "arsiv_tamir")
                                        {
                                            string teslimGuncelle = nv["teslim_tarihi"] ?? DateTime.Now.ToString("dd.MM.yyyy");
                                            using (var cmd = new SqliteCommand("UPDATE vitrin SET teslim_tarihi=@teslim WHERE id=@id;", connection))
                                            {
                                                cmd.Parameters.AddWithValue("@teslim", teslimGuncelle);
                                                cmd.Parameters.AddWithValue("@id", editId);
                                                cmd.ExecuteNonQuery();
                                            }
                                        }
                                        else
                                        {
                                            string birlesik = string.Format("{0} | {1} | Pil: {2}", nv["v_gb"] ?? "", nv["v_renk"] ?? "", nv["v_pil"] ?? "");
                                            using (var cmd = new SqliteCommand("UPDATE vitrin SET model=@model, marka=@marka, imei=@imei, fiyat=@fiyat, satis_fiyati=@satis, kutu_fatura=@kutu, garanti=@garanti, alinma_tarihi=@alinma WHERE id=@id;", connection))
                                            {
                                                cmd.Parameters.AddWithValue("@model", nv["v_model"] ?? "");
                                                cmd.Parameters.AddWithValue("@marka", birlesik);
                                                cmd.Parameters.AddWithValue("@imei", nv["v_imei"] ?? "");
                                                cmd.Parameters.AddWithValue("@fiyat", nv["v_alis"] ?? "");
                                                cmd.Parameters.AddWithValue("@satis", nv["v_satis"] ?? "");
                                                cmd.Parameters.AddWithValue("@kutu", nv["v_kutu"] ?? "");
                                                cmd.Parameters.AddWithValue("@garanti", nv["v_garanti"] ?? "");
                                                string vTarih = nv["v_tarih"] ?? "";
                                                if (string.IsNullOrWhiteSpace(vTarih)) vTarih = DateTime.Now.ToString("dd.MM.yyyy");
                                                cmd.Parameters.AddWithValue("@alinma", vTarih);
                                                cmd.Parameters.AddWithValue("@id", editId);
                                                cmd.ExecuteNonQuery();
                                            }
                                        }
                                    }
                                    response.StatusCode = 302;
                                    string location = tip == "tamir" ? "/tamir_listele" : tip == "arsiv_tamir" ? "/arsiv_panel" : "/vitrin_listele";
                                    response.Headers.Add("Location", location);
                                    response.OutputStream.Close();
                                    return;
                                }
                                catch (Exception ex)
                                {
                                    html = GetHeader("Kaydetme Hatasi", "/", "Ana Menu") + "<div class='alert alert-err'>" + ex.Message + "</div>" + Footer;
                                }
                            }
                            else if (rawUrl.StartsWith("/sil") && method == "POST")
                            {
                                string body = new StreamReader(request.InputStream).ReadToEnd();
                                var nv = HttpUtility.ParseQueryString(body);
                                string id = nv["id"];
                                string git = nv["git"];

                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();

                                        if (git == "arsiv")
                                        {
                                            using (var command = new SqliteCommand("DELETE FROM vitrin WHERE id = @id;", connection))
                                            {
                                                command.Parameters.AddWithValue("@id", id);
                                                command.ExecuteNonQuery();
                                            }
                                        }
                                        else
                                        {
                                            string yeniDurum = (git == "tamir") ? "TESLIM_EDILDI" : "SATILDI";
                                            string teslimTarihi = DateTime.Now.ToString("dd.MM.yyyy");
                                            using (var command = new SqliteCommand("UPDATE vitrin SET durum = @durum, teslim_tarihi = @teslim WHERE id = @id;", connection))
                                            {
                                                command.Parameters.AddWithValue("@durum", yeniDurum);
                                                command.Parameters.AddWithValue("@teslim", teslimTarihi);
                                                command.Parameters.AddWithValue("@id", id);
                                                command.ExecuteNonQuery();
                                            }
                                        }
                                    }
                                }
                                catch { }

                                response.StatusCode = 302;
                                if (git == "arsiv")
                                    response.Headers.Add("Location", "/arsiv_panel");
                                else
                                    response.Headers.Add("Location", git == "vitrin" ? "/vitrin_listele" : "/tamir_listele");

                                response.OutputStream.Close();
                                return;
                            }
                            else
                            {
                                response.StatusCode = 404;
                                html = GetHeader("404 Sayfa Bulunamadı") + "<div class='empty-state'>Aradığınız dükkan modülü mevcut değil.</div>" + Footer;
                            }

                            byte[] buffer = Encoding.UTF8.GetBytes(html);
                            response.ContentLength64 = buffer.Length;
                            response.ContentType = "text/html; charset=utf-8";
                            response.OutputStream.Write(buffer, 0, buffer.Length);
                            response.OutputStream.Close();
                        }
                        catch { try { response.Close(); } catch { } }
                        finally { Interlocked.Decrement(ref activeConnections); }
                    });
                }
            }
            catch (Exception ex) { Console.WriteLine("❌ Sunucu Hatası: " + ex.Message); }
        }

        private static void TabloyuHazirla()
        {
            try
            {
                using (var connection = new SqliteConnection(connStr))
                {
                    connection.Open();
                    using (var command = new SqliteCommand("CREATE TABLE IF NOT EXISTS vitrin (id INTEGER PRIMARY KEY AUTOINCREMENT, marka TEXT, model TEXT, imei TEXT, alinma_tarihi TEXT, fiyat TEXT, satis_fiyati TEXT, durum TEXT, kutu_fatura TEXT, garanti TEXT, teslim_tarihi TEXT);", connection))
                    {
                        command.ExecuteNonQuery();
                    }
                    try {
                        using (var altCmd = new SqliteCommand("ALTER TABLE vitrin ADD COLUMN teslim_tarihi TEXT;", connection))
                            altCmd.ExecuteNonQuery();
                    } catch { }

                    using (var commandKasa = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa (id INTEGER PRIMARY KEY AUTOINCREMENT, aciklama TEXT, tip TEXT, tutar REAL, tarih TEXT, odeme_tipi TEXT);", connection))
                    {
                        commandKasa.ExecuteNonQuery();
                    }
                    try {
                        using (var altCmdKasa = new SqliteCommand("ALTER TABLE kasa ADD COLUMN odeme_tipi TEXT;", connection))
                            altCmdKasa.ExecuteNonQuery();
                    } catch { }
                }
            }
            catch (Exception ex) { Console.WriteLine("❌ Veritabanı Tablo Hatası: " + ex.Message); }
        }
    }
}
