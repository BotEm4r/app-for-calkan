using System;
using System.IO;
using System.Text;
using System.Net;
using System.Web;
using Microsoft.Data.Sqlite;
using System.Threading;

namespace CalkanGsmWeb
{
    class Program
    {
        private static string dbPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "calkan_gsm.db");
        private static string connStr => $"Data Source={dbPath};";

        private static System.Collections.Generic.Dictionary<string, string> Kullanicilar = new System.Collections.Generic.Dictionary<string, string>();
        private static string SessionValue = "calkan_oturum_" + Guid.NewGuid().ToString().Substring(0, 8);

        private static System.Collections.Concurrent.ConcurrentDictionary<string, (int count, DateTime lockUntil)> loginAttempts = new();
        private static System.Collections.Concurrent.ConcurrentDictionary<string, (int count, DateTime window)> rateLimit = new();
        private static int activeConnections = 0;
        private const int MAX_CONNECTIONS = 100;
        private const int MAX_RPS = 25; 
        private const int MAX_BODY_BYTES = 10240; 

        private static void ConfigYukle()
        {
            // ÖNCE RAILWAY ORTAM DEĞİŞKENLERİNE BAK (KULLANICI1=admin:sifre şeklinde girilebilir)
            string railwayUser = Environment.GetEnvironmentVariable("KULLANICI1");
            if (!string.IsNullOrEmpty(railwayUser))
            {
                string[] parts = railwayUser.Split(':', 2);
                if (parts.Length == 2) 
                {
                    Kullanicilar[parts[0].Trim()] = parts[1].Trim();
                    return; // Panelden veri geldiyse config.txt aramaya gerek yok
                }
            }

            // RAILWAY PANELİNDE TANIMLANMAMIŞSA LOCALDEKİ CONFIG.TXT'YE BAK
            string configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.txt");
            if (!File.Exists(configPath))
            {
                File.WriteAllText(configPath, "KULLANICI1=calkanadmin:fcalkan2626\nPORT=8080");
            }

            foreach (string line in File.ReadAllLines(configPath))
            {
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#")) continue;

                int commentIdx = trimmedLine.IndexOf('#');
                string cleanLine = commentIdx >= 0 ? trimmedLine.Substring(0, commentIdx).Trim() : trimmedLine;

                if (!cleanLine.Contains("=")) continue;

                string[] kvParts = cleanLine.Split('=', 2);
                string key = kvParts[0].Trim();
                string val = kvParts[1].Trim();

                if (key.StartsWith("KULLANICI"))
                {
                    string[] parts = val.Split(':', 2);
                    if (parts.Length == 2) 
                    {
                        Kullanicilar[parts[0].Trim()] = parts[1].Trim();
                    }
                }
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
  @import url('https://fonts.googleapis.com/css2?family=Plus+Jakarta+Sans:wght=400;500;600;700&family=JetBrains+Mono:wght=400;500;600&display=swap');

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
    --shadow:      0 10px 25px -5px rgba(0, 0, 0, 0.3), 0 8px 10px -6px rgba(0, 0, 0, 0.3);
  }

  :root[data-theme='light'] {
    --bg:          #e2e8f0; 
    --surface:     #ffffff; 
    --surface-top: #cbd5e1; 
    --border:      #94a3b8; 
    --text:        #0f172a; 
    --muted:       #475569; 
    --accent:      #0284c7; 
    --green:       #16a34a; 
    --whatsapp:    #16a34a;
    --red:         #dc2626; 
    --shadow: 0 10px 25px -5px rgba(0, 0, 0, 0.08), 0 8px 10px -6px rgba(0, 0, 0, 0.08);
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
  .shop-title {
    display: flex;
    align-items: center;
    gap: 12px;
  }
  .shop-badge {
    width: 12px; height: 12px;
    background: var(--accent);
    border-radius: 4px;
    box-shadow: 0 0 10px var(--accent);
  }
  .shop-name {
    font-size: 16px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  
  .nav-right {
    display: flex;
    align-items: center;
    gap: 12px;
  }
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
  .theme-toggle:hover {
    border-color: var(--accent);
  }
  .shop-status {
    font-size: 12px;
    color: var(--green);
    font-weight: 600;
    background: rgba(74, 222, 128, 0.1);
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
    box-shadow: 0 12px 30px rgba(56, 189, 248, 0.15);
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

  .btn-success { background: var(--green); color: #fff; }
  .btn-success:hover { opacity: 0.9; }

  .btn-whatsapp { background: var(--whatsapp); color: #fff; gap: 6px; }
  .btn-whatsapp:hover { opacity: 0.9; }

  .btn-danger { background: var(--red); color: #fff; }
  .btn-danger:hover { opacity: 0.9; }

  .btn-close-shop { background: transparent; border: 1px solid rgba(248,113,113,0.3); color: var(--red); width: 100%; margin-top: 40px; }
  .btn-close-shop:hover { background: rgba(248,113,113,0.1); }

  .alert { padding: 16px; border-radius: 10px; font-size: 14px; margin-bottom: 24px; font-weight: 500; text-align: center; }
  .alert-ok { background: rgba(74, 222, 128, 0.1); border: 1px solid var(--green); color: var(--green); }
  .alert-err { background: rgba(248, 113, 113, 0.1); border: 1px solid var(--red); color: var(--red); }

  .empty-state {
    text-align: center;
    padding: 60px 20px;
    color: var(--muted);
    font-size: 14px;
    border: 2px dashed var(--border);
    border-radius: 16px;
  }

  .login-wrapper { min-height: 75vh; display: flex; align-items: center; justify-content: center; }
  .login-card { width: 100%; max-width: 360px; background: var(--surface); border: 1px solid var(--border); padding: 32px; border-radius: 16px; box-shadow: var(--shadow); }
  .login-head { font-size: 22px; font-weight: 800; text-align: center; margin-bottom: 24px; letter-spacing: -0.02em; }

  .divider-title {
    font-size: 11px;
    font-weight: 700;
    color: var(--accent);
    margin: 35px 0 15px;
    text-transform: uppercase;
    letter-spacing: 0.08em;
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
            if (text.toUpperCase().indexOf(filter) > -1) {
                rows[i].style.display = '';
            } else {
                rows[i].style.display = 'none';
            }
        }
    }

    function temaDegistir() {
        const mevcut = document.documentElement.getAttribute('data-theme') || 'dark';
        const yeni = mevcut === 'dark' ? 'light' : 'dark';
        document.documentElement.setAttribute('data-theme', yeni);
        localStorage.setItem('calkan_tema', yeni);
        document.getElementById('theme-lbl').innerText = yeni === 'dark' ? '🌙 Gece' : '☀️ Gündüz';
    }

    document.addEventListener('DOMContentLoaded', () => {
        const kayitliTema = localStorage.getItem('calkan_tema') || 'dark';
        document.documentElement.setAttribute('data-theme', kayitliTema);
        const lbl = document.getElementById('theme-lbl');
        if(lbl) lbl.innerText = kayitliTema === 'dark' ? '🌙 Gece' : '☀️ Gündüz';
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

        private static string Footer => "</div></body></html>";

        static void Main(string[] args)
        {
            ConfigYukle(); 
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            TabloyuHazirla();
            
            string port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
            StartServer(port);
        }

      private static void StartServer(string port)
{
    HttpListener listener = new HttpListener();
    
    if (OperatingSystem.IsWindows())
    {
        // Kendi bilgisayarında admin yetkisi istememesi için localhost dinle
        listener.Prefixes.Add($"http://localhost:{port}/");
        Console.WriteLine($"\n[YERELE ÖZEL] Local test modu aktif: http://localhost:{port}/");
    }
    else
    {
        // Railway (Linux) ortamında dışarıdan gelecek tüm istekleri kabul etmesi için * dinle
        listener.Prefixes.Add($"http://*:{port}/");
        Console.WriteLine($"🚀 Sunucu bulutta başlatılıyor. Dinlenen Port: {port}");
    }
    
    try
    {
        listener.Start();
        
        while (true)
        {
            if (Interlocked.Increment(ref activeConnections) > MAX_CONNECTIONS)
            {
                Interlocked.Decrement(ref activeConnections);
                continue
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
                            Cookie? otuCookie = request.Cookies["calkan_session"];
                            if (otuCookie != null && otuCookie.Value == SessionValue)
                                oturumAcikMi = true;

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
                                        {
                                            expiresAttr = "; Expires=" + DateTime.Now.AddDays(30).ToString("R");
                                        }

                                        response.Headers.Add("Set-Cookie", "calkan_session=" + SessionValue + "; Path=/" + expiresAttr + "; SameSite=Strict");
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
                                       "<div class='form-field'><label>Şifre</label><input type='password' name='sifre' class='form-input' required autocomplete='new-password'></div>" +
                                       "<label class='remember-me'><input type='checkbox' name='hatirla'> Oturumu Açık Tut (30 Gün)</label>" +
                                       "<button type='submit' class='action-btn btn-submit'>Sistemi Aç</button>" +
                                       "</form></div></div></div></body></html>";
                            }
                            else if (rawUrl == "/" || rawUrl == "")
                            {
                                html = GetHeader() +
                                       "<div class='view-heading'><div class='view-title'>Mağaza Yönetim Tezgâhı</div><div class='view-sub'>Dükkan içi aktif tamirler ve vitrin envanter kontrolü.</div></div>" +
                                       "<div class='menu-layout'>" +
                                       "  <a href='/tamir_panel' class='menu-card'><span class='label'>Yeni Tamir Kaydı</span><span class='desc'>Müşteri bilgileri ve arıza durum kaydı oluşturun.</span></a>" +
                                       "  <a href='/vitrin_panel' class='menu-card'><span class='label'>Vitrine Ürün Ekle</span><span class='desc'>Satışa çıkarılacak yeni cihaz stok girişi yapın.</span></a>" +
                                       "  <a href='/tamir_listele' class='menu-card'><span class='label'>Tamir Bekleyenler</span><span class='desc'>Servisteki veya teslime hazır cihaz listesi.</span></a>" +
                                       "  <a href='/vitrin_listele' class='menu-card'><span class='label'>Vitrin Stok Listesi</span><span class='desc'>Şu an rafta satılmayı bekleyen güncel envanter.</span></a>" +
                                       "  <a href='/arsiv_panel' class='menu-card menu-full'><span class='label'>Geçmiş İşlemler Arşivi</span><span class='desc'>Tamamlanıp teslim edilmiş tamirler ve satılmış eski cihazların dökümü.</span></a>" +
                                       "</div>" +
                                       "<a href='/logout' class='action-btn btn-close-shop'>Güvenli Çıkış (Oturumu Kapat)</a>" +
                                       Footer;
                            }
                            else if (rawUrl == "/tamir_panel")
                            {
                                html = GetHeader("Yeni Tamir Kabulü", "/", "Ana Menü") +
                                       "<div class='form-box'>" +
                                       "<form action='/ekle' method='POST'>" +
                                       "<div class='form-field'><label>Müşteri Adı Soyadı</label><input type='text' name='musteri' class='form-input' placeholder='Müşteri İsim Soyisim' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Müşteri Telefon No</label><input type='text' name='telefon' class='form-input' placeholder='05xx xxx xx xx' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Cihaz Markası</label><input type='text' name='c_marka' class='form-input' placeholder='Örn: Samsung, Apple' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Cihaz Modeli</label><input type='text' name='c_model' class='form-input' placeholder='Örn: Galaxy S23, iPhone 11' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Yapılacak Arıza İşlemi</label><input type='text' name='islem' class='form-input' placeholder='Örn: Batarya Değişimi, Şarj Soketi' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Alınacak Tahmini Ücret (TL)</label><input type='text' name='t_fiyat' class='form-input' placeholder='Müşteriye verilen fiyat' required autocomplete='off'></div>" +
                                       "<div class='form-field'><label>Arıza / Kozmetik Notu</label><input type='text' name='ariza' class='form-input' placeholder='Ekranda çizik var, şifre alındı vs.' required autocomplete='off'></div>" +
                                       "<button type='submit' class='action-btn btn-submit'>Kabulü Onayla ve Kaydet</button>" +
                                       "</form>" +
                                       "</div>" +
                                       Footer;
                            }
                            else if (rawUrl == "/ekle" && method == "POST")
                            {
                                string body = new StreamReader(request.InputStream, request.ContentEncoding).ReadToEnd();
                                var nv = HttpUtility.ParseQueryString(body);
                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();
                                        string query = "INSERT INTO vitrin (marka, model, imei, alinma_tarihi, fiyat, satis_fiyati, durum, kutu_fatura, garanti) VALUES (@marka, @model, @imei, @alinma, @fiyat, @satis, @durum, @kutu, @garanti);";
                                        using (var command = new SqliteCommand(query, connection))
                                        {
                                            command.Parameters.AddWithValue("@marka", nv["musteri"]);
                                            command.Parameters.AddWithValue("@model", nv["telefon"]);
                                            command.Parameters.AddWithValue("@imei", nv["c_marka"]);
                                            command.Parameters.AddWithValue("@alinma", nv["c_model"]);
                                            command.Parameters.AddWithValue("@fiyat", nv["islem"]);
                                            command.Parameters.AddWithValue("@satis", nv["t_fiyat"]);
                                            command.Parameters.AddWithValue("@durum", "TAMIR");
                                            command.Parameters.AddWithValue("@kutu", nv["ariza"]);
                                            command.Parameters.AddWithValue("@garanti", "-");
                                            command.ExecuteNonQuery();
                                        }
                                    }
                                    html = GetHeader("Kayıt Başarılı", "/tamir_panel", "Yeni Form") +
                                           "<div class='alert alert-ok'>Tamir formu dükkan veritabanına başarıyla işlendi.</div>" +
                                           "<a href='/tamir_listele' class='action-btn btn-submit'>Aktif Tamir Kuyruğuna Git</a>" +
                                           Footer;
                                }
                                catch (Exception ex)
                                {
                                    html = GetHeader("Sistem Hatası", "/tamir_panel", "Geri") +
                                           "<div class='alert alert-err'>Veri tabanına yazılamadı: " + ex.Message + "</div>" +
                                           Footer;
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

                                                    string mesajMetni = $"Merhaba, Çalkan GSM'den yazıyoruz. {r["imei"]} {r["alinma_tarihi"]} cihazınızın teknik servis işlemleri başarıyla tamamlanmıştır. Cihazınızı dükkanımızdan teslim alabilirsiniz.";
                                                    string encodedMsg = HttpUtility.UrlEncode(mesajMetni);
                                                    string waLink = $"https://wa.me/{cleanPhone}?text={encodedMsg}";

                                                    sb.Append("<div class='shop-row row-tamir'>");
                                                    sb.Append("<div class='row-header'>");
                                                    sb.AppendFormat("<div class='row-title'>{0}</div>", r["marka"]);
                                                    sb.AppendFormat("<div class='row-price'>{0} TL</div>", r["satis_fiyati"]);
                                                    sb.Append("</div>");
                                                    sb.Append("<div class='tags'>");
                                                    sb.AppendFormat("<span class='tag active'>📱 {0} {1}</span>", r["imei"], r["alinma_tarihi"]);
                                                    sb.AppendFormat("<span class='tag'>📞 {0}</span>", rawPhone);
                                                    sb.AppendFormat("<span class='tag'>🛠️ İşlem: {0}</span>", r["fiyat"]);
                                                    sb.Append("</div>");
                                                    sb.AppendFormat("<div class='row-notes'><strong>Arıza Durum Notu:</strong> {0}</div>", r["kutu_fatura"]);
                                                    
                                                    sb.Append("<div class='row-actions'>");
                                                    sb.AppendFormat("<a href='{0}' target='_blank' class='action-btn btn-whatsapp'>💬 WhatsApp Bildir</a>", waLink);
                                                    
                                                    sb.Append("<form action='/sil' method='POST' style='margin:0;'>");
                                                    sb.AppendFormat("<input type='hidden' name='id' value='{0}'>", r["id"]);
                                                    sb.Append("<input type='hidden' name='git' value='tamir'>");
                                                    sb.Append("<button type='submit' class='action-btn btn-success'>Teslim Et ve Arşivle</button>");
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
                            else if (rawUrl == "/vitrin_panel")
                            {
                                html = GetHeader("Vitrin Envanter Girişi", "/", "Ana Menü") +
                                       "<div class='form-box'>" +
                                       "<form action='/ekle_vitrin' method='POST'>" +
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
                            else if (rawUrl == "/ekle_vitrin" && method == "POST")
                            {
                                string body = new StreamReader(request.InputStream, request.ContentEncoding).ReadToEnd();
                                var nv = HttpUtility.ParseQueryString(body);
                                try
                                {
                                    using (var connection = new SqliteConnection(connStr))
                                    {
                                        connection.Open();
                                        string birlesikOzellikler = string.Format("{0} | {1} | Pil: {2}", nv["v_gb"], nv["v_renk"], nv["v_pil"]);
                                        string query = "INSERT INTO vitrin (marka, model, imei, alinma_tarihi, fiyat, satis_fiyati, durum, kutu_fatura, garanti) VALUES (@marka, @model, @imei, @alinma, @fiyat, @satis, @durum, @kutu, @garanti);";
                                        using (var command = new SqliteCommand(query, connection))
                                        {
                                            command.Parameters.AddWithValue("@marka", birlesikOzellikler);
                                            command.Parameters.AddWithValue("@model", nv["v_model"]);
                                            command.Parameters.AddWithValue("@imei", nv["v_imei"]);
                                            command.Parameters.AddWithValue("@alinma", DateTime.Now.ToString("dd.MM.yyyy"));
                                            command.Parameters.AddWithValue("@fiyat", nv["v_alis"]);
                                            command.Parameters.AddWithValue("@satis", nv["v_satis"]);
                                            command.Parameters.AddWithValue("@durum", "VITRIN");
                                            command.Parameters.AddWithValue("@kutu", nv["v_kutu"]);
                                            command.Parameters.AddWithValue("@garanti", nv["v_garanti"]);
                                            command.ExecuteNonQuery();
                                        }
                                    }
                                    html = GetHeader("Ürün Eklendi", "/vitrin_panel", "Yeni Ürün Girdisi") +
                                           "<div class='alert alert-ok'>Ürün dükkan vitrin envanterine başarıyla dahil edildi.</div>" +
                                           "<a href='/vitrin_listele' class='action-btn btn-submit'>Vitrin Raflarını Gör</a>" +
                                           Footer;
                                }
                                catch (Exception ex)
                                {
                                    html = GetHeader("Sistem Hatası", "/vitrin_panel", "Geri") +
                                           "<div class='alert alert-err'>Stok veritabanına eklenemedi: " + ex.Message + "</div>" +
                                           Footer;
                                }
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
                                                    sb.AppendFormat("<span class='tag'>🛡️ Garanti: {0}</span>", r["garanti"]);
                                                    sb.AppendFormat("<span class='tag'>📦 {0}</span>", r["kutu_fatura"]);
                                                    sb.Append("</div>");
                                                    sb.AppendFormat("<div style='font-size:12px; color:var(--muted); font-family:monospace; padding-left:4px;'>Dükkan Maliyeti: {0} TL</div>", r["fiyat"]);
                                                    sb.Append("<div class='row-actions'>");
                                                    sb.Append("<span style='font-size:12px; color:var(--accent); font-weight:600;'>DURUM: VİTRİNDE</span>");
                                                    sb.Append("<form action='/sil' method='POST'>");
                                                    sb.AppendFormat("<input type='hidden' name='id' value='{0}'>", r["id"]);
                                                    sb.Append("<input type='hidden' name='git' value='vitrin'>");
                                                    sb.Append("<button type='submit' class='action-btn btn-submit' style='padding:8px 20px; width:auto; margin:0;'>Cihazı Sat</button>");
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
                                                    sb.AppendFormat("<span class='tag'>{0} {1}</span>", r["imei"], r["alinma_tarihi"]);
                                                    sb.AppendFormat("<span class='tag'>🛠️ {0}</span>", r["fiyat"]);
                                                    sb.Append("</div>");
                                                    sb.Append("<div class='row-actions' style='margin-top:10px; padding-top:10px;'>");
                                                    sb.Append("<span style='font-size:12px; color:var(--green); font-weight:650;'>✓ TESLİM EDİLDİ</span>");
                                                    sb.Append("<form action='/sil' method='POST' onsubmit=\"return confirm('Bu arşiv kaydını veritabanından tamamen silmek istediğinize emin misiniz?');\">");
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
                                                    sb.Append("</div>");
                                                    sb.Append("<div class='row-actions' style='margin-top:10px; padding-top:10px;'>");
                                                    sb.Append("<span style='font-size:12px; color:var(--accent); font-weight:650;'>💰 SATILDI</span>");
                                                    sb.Append("<form action='/sil' method='POST' onsubmit=\"return confirm('Bu satış kaydını arşivden tamamen silmek istediğinize emin misiniz?');\">");
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
                                            using (var command = new SqliteCommand("UPDATE vitrin SET durum = @durum WHERE id = @id;", connection))
                                            {
                                                command.Parameters.AddWithValue("@durum", yeniDurum);
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
                        catch { try { response.Close(); } catch {} }
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
                    using (var command = new SqliteCommand("CREATE TABLE IF NOT EXISTS vitrin (id INTEGER PRIMARY KEY AUTOINCREMENT, marka TEXT, model TEXT, imei TEXT, alinma_tarihi TEXT, fiyat TEXT, satis_fiyati TEXT, durum TEXT, kutu_fatura TEXT, garanti TEXT);", connection))
                    {
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("❌ Veritabanı Tablo Hatası: " + ex.Message); }
        }
    }
}
