using System.Runtime.InteropServices;
#nullable disable
using System;
using System.IO;
using System.Text;
using System.Net;
using System.Web;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace CalkanGsmWeb
{
    class Program
    {
        private static string BaseDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName) ?? AppContext.BaseDirectory;
        private static string dbPath = Path.Combine(BaseDir, "data", "calkan_gsm.db");
        private static string connStr => $"Data Source={dbPath};";

        private static System.Collections.Generic.Dictionary<string, string> Kullanicilar = new System.Collections.Generic.Dictionary<string, string>();
        private static string SessionValue = "calkan_oturum_kalici_v1";

        private static System.Collections.Concurrent.ConcurrentDictionary<string, (int count, DateTime lockUntil)> loginAttempts = new();
        private static System.Collections.Concurrent.ConcurrentDictionary<string, (int count, DateTime window)> rateLimit = new();
        private static int activeConnections = 0;
        private const int MAX_CONNECTIONS = 100;
        private const int MAX_RPS = 25;
        private const int MAX_BODY_BYTES = 10240;

        // config.txt veya Railway env'den okunan port (varsayılan 8080)
        private static string configPort = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "2626" : "8080";

        private static void ConfigYukle()
        {
            // ── 1. ADIM: RAILWAY ORTAM DEĞİŞKENLERİNE BAK ──────────────────────────
            // KULLANICI1=admin:sifre, KULLANICI2=teknisyen:pass123 ... şeklinde birden fazla tanımlanabilir
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

            // Railway'de PORT env değişkeni varsa onu da al
            string railwayPort = Environment.GetEnvironmentVariable("PORT");
            if (!string.IsNullOrEmpty(railwayPort))
                configPort = railwayPort;

            // Railway'de kullanıcı tanımlandıysa config.txt'ye gerek yok
            if (railwayKullaniciBulundu)
            {
                Console.WriteLine($"✅ {Kullanicilar.Count} kullanıcı Railway ortam değişkenlerinden yüklendi.");
                return;
            }

            // ── 2. ADIM: LOCALDEKİ CONFIG.TXT'YE BAK ────────────────────────────────
            string configPath = Path.Combine(BaseDir, "config.txt");
            if (!File.Exists(configPath))
            {
                // Örnek config.txt oluştur
                File.WriteAllText(configPath,
                    "# Calkan GSM - Kullanici ve Port Ayarlari\n" +
                    "# Kullanici eklemek icin KULLANICI1, KULLANICI2 ... seklinde devam ettirin\n" +
                    "# Format: KULLANICIn=kullanici_adi:sifre\n\n" +
                    "KULLANICI1=admin:emir2626\n" +
                    "KULLANICI2=calkanadmin:fcalkan2626\n" +
                    "PORT=2626\n");
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
                // Config.txt'de hiç kullanıcı yoksa güvenli fallback
                Kullanicilar["calkanadmin"] = "fcalkan2626";
                Console.WriteLine("⚠️ config.txt'de kullanıcı bulunamadı, varsayılan hesap kullanılıyor.");
            }
            else
            {
                Console.WriteLine($"✅ {Kullanicilar.Count} kullanıcı config.txt'den yüklendi. Port: {configPort}");
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
  
  .stats-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 15px; margin-bottom: 30px; }
  .stat-card { background: var(--surface); border: 1px solid var(--border); padding: 20px; border-radius: 12px; text-align: center; box-shadow: var(--shadow); }
  .stat-val { font-size: 24px; font-weight: 800; color: var(--text); }
  .stat-lbl { font-size: 12px; color: var(--muted); text-transform: uppercase; margin-top: 5px; font-weight: 600; }
  .stat-kar .stat-val { color: var(--green); }

  
  .defter-row { display: grid; grid-template-columns: 1fr 1fr; gap: 15px; margin-bottom: 20px; }
  .defter-card { background: var(--surface); border: 1px solid var(--border); padding: 20px; border-radius: 12px; }
  .input-pair { display: flex; border: 1px solid var(--border); border-radius: 8px; overflow: hidden; background: #000; }
  .input-pair input { background: transparent; border: none; color: white; padding: 10px; width: 50%; border-right: 1px solid var(--border); outline: none; }
  .input-pair input:last-child { border-right: none; }
  .input-triple { display: flex; border: 1px solid var(--border); border-radius: 8px; overflow: hidden; background: #000; }
  .input-triple input { background: transparent; border: none; color: white; padding: 10px; width: 33.33%; border-right: 1px solid var(--border); outline: none; }
  .input-triple input:last-child { border-right: none; }
  .btn-kasa { background: var(--primary); color: white; border: none; padding: 10px; border-radius: 8px; width: 100%; margin-top: 10px; font-weight: 700; cursor: pointer; }

  /* Tarayici varsayilan sifre goster ikonunu gizle */
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
    --accent:      #0284c7;
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

  /* Şifre alanı wrapper */
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
    padding: 60px 20px;
    color: var(--muted);
    font-size: 14px;
    border: 2px dashed var(--border);
    border-radius: 16px;
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
            c.setAttribute('cx','12'); c.setAttribute('cy','12'); c.setAttribute('r','3');
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

        // Şifre göster/gizle butonu SVG'si (başlangıçta kapalı göz = şifre gizli)
        private static string EyeIconHTML =>
            "<svg viewBox='0 0 24 24' fill='none' xmlns='http://www.w3.org/2000/svg'>" +
            "<path stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' d='M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z'/>" +
            "<circle stroke='currentColor' stroke-width='2' cx='12' cy='12' r='3'/>" +
            "</svg>";

        private static string Footer => "</div></body></html>";

        [STAThread]
        static void Main(string[] args)
        {
            ConfigYukle();
            
            // Veritabanı klasörünü oluştur
            string? dirName = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dirName)) Directory.CreateDirectory(dirName);
            
            TabloyuHazirla();

            string port = configPort;
            
            // Eger Linux (Railway) uzerindeysek sadece sunucuyu baslat ve bekle
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine("🌐 Linux/Railway Modu Aktif. Sunucu baslatiliyor...");
                StartServer(port);
                // Sunucu sonsuz dongude oldugu icin buraya ulasilmaz ama guvenlik icin:
                Thread.Sleep(Timeout.Infinite);
                return;
            }

            // Eger Windows uzerindeysek EXE/Görsel modda calis
            string siteUrl = $"http://localhost:{port}/";

            Thread serverThread = new Thread(() => StartServer(port));
            serverThread.IsBackground = true;
            serverThread.Start();

            Thread.Sleep(500);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Form mainForm = new Form
            {
                Text = "Çalkan GSM Mağaza Yönetim Paneli",
                Width = 850,
                Height = 750,
                WindowState = FormWindowState.Maximized,
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = System.Drawing.Color.FromArgb(15, 23, 42)
            };

            WebView2 webView = new WebView2
            {
                Dock = DockStyle.Fill
            };

            mainForm.Controls.Add(webView);

            mainForm.Load += async (s, e) =>
            {
                try
                {
                    await webView.EnsureCoreWebView2Async(null);
                    webView.CoreWebView2.Navigate(siteUrl);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("WebView2 motoru yüklenemedi: " + ex.Message);
                }
            };

            Application.Run(mainForm);
        }

        private static void StartServer(string port)
        {
            HttpListener listener = new HttpListener();
            string prefix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"http://localhost:{port}/" : $"http://*:{port}/";
            listener.Prefixes.Add(prefix);

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
                                // Login formu — şifre göster/gizle butonu dahil
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
                                       (aktifKullanici == "admin" ? "<a href='/yedek' class='action-btn btn-secondary' style='width:100%;margin-top:16px;justify-content:center;display:flex;'>💾 Veritabanı Yedeği İndir</a>" : "") +
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
                                       "<div class='form-field'><label>Tamir Durumu</label><input type='text' name='tamir_durum' class='form-input' placeholder='Bekliyor, Tamirde, Hazır...' autocomplete='off'></div>" +
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
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa_defteri (id INTEGER PRIMARY KEY AUTOINCREMENT, tarih TEXT, tur TEXT, aciklama TEXT, maliyet TEXT, tutar TEXT);", connection)) { cmd.ExecuteNonQuery(); }
                    
                                        string query = "INSERT INTO vitrin (marka, model, imei, alinma_tarihi, fiyat, satis_fiyati, durum, kutu_fatura, garanti) VALUES (@marka, @model, @imei, @alinma, @fiyat, @satis, @durum, @kutu, @garanti);";
                                        using (var command = new SqliteCommand(query, connection))
                                        {
                                            command.Parameters.AddWithValue("@marka", nv["musteri"]);
                                            command.Parameters.AddWithValue("@model", nv["telefon"]);
                                            command.Parameters.AddWithValue("@imei", nv["c_marka"] + " " + nv["c_model"]);
                                            command.Parameters.AddWithValue("@alinma", DateTime.Now.ToString("dd.MM.yyyy"));
                                            command.Parameters.AddWithValue("@fiyat", nv["islem"]);
                                            command.Parameters.AddWithValue("@satis", nv["t_fiyat"]);
                                            command.Parameters.AddWithValue("@durum", "TAMIR");
                                            command.Parameters.AddWithValue("@kutu", nv["ariza"]);
                                            command.Parameters.AddWithValue("@garanti", nv["tamir_durum"] ?? "Bekliyor");
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
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa_defteri (id INTEGER PRIMARY KEY AUTOINCREMENT, tarih TEXT, tur TEXT, aciklama TEXT, maliyet TEXT, tutar TEXT);", connection)) { cmd.ExecuteNonQuery(); }
                    
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

                                                    // Bekleme günü hesapla
                                                    string alinmaTarihi = r["alinma_tarihi"]?.ToString() ?? "";
                                                    string beklemeBadge = "";
                                                    if (DateTime.TryParseExact(alinmaTarihi, "dd.MM.yyyy", null, System.Globalization.DateTimeStyles.None, out DateTime alinmaD))
                                                    {
                                                        int gun = (DateTime.Now - alinmaD).Days;
                                                        string badgeColor = gun >= 7 ? "#f87171" : gun >= 3 ? "#fb923c" : "#4ade80";
                                                        beklemeBadge = $"<span class='tag' style='border-color:{badgeColor};color:{badgeColor};'>⏳ {gun} gündür bekliyor</span>";
                                                    }

                                                    // WA şablonları
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
                                                    // Durum badge
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
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa_defteri (id INTEGER PRIMARY KEY AUTOINCREMENT, tarih TEXT, tur TEXT, aciklama TEXT, maliyet TEXT, tutar TEXT);", connection)) { cmd.ExecuteNonQuery(); }
                    
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
                            
                            else if (rawUrl == "/kasa_defteri")
                            {
                                StringBuilder rows = new StringBuilder();
                                double toplamGelir = 0; double toplamGider = 0;
                                try {
                                    using (var conn = new SqliteConnection(connStr)) {
                                        conn.Open();
                                        using (var cmd = new SqliteCommand("SELECT * FROM kasa_defteri ORDER BY id DESC LIMIT 30", conn)) {
                                            using (var r = cmd.ExecuteReader()) {
                                                while (r.Read()) {
                                                    string t = r["tur"].ToString();
                                                    double tutar = 0; double.TryParse(r["tutar"].ToString(), out tutar);
                                                    double maliyet = 0; double.TryParse(r["maliyet"].ToString(), out maliyet);
                                                    if (t == "GIDER") toplamGider += tutar; else toplamGelir += (tutar - maliyet);
                                                    rows.Append($"<tr class='shop-row'><td>{r["tarih"]}</td><td><b>{t}</b></td><td>{r["aciklama"]}</td><td>{maliyet} ₺</td><td>{tutar} ₺</td></tr>");
                                                }
                                            }
                                        }
                                    }
                                } catch {}

                                html = GetHeader("Kasa Defteri", "/", "Ana Menu") + 
                                    "<div class='defter-row'>" +
                                    "  <div class='defter-card'><label>GÜN BAŞI DEVİR</label><form action='/kasa_kaydet' method='POST'><div class='input-pair'><input type='text' name='aciklama' value='Devir' readonly><input type='number' name='tutar' placeholder='Tutar' required></div><input type='hidden' name='tur' value='DEVIR'><button class='btn-kasa'>KAYDET</button></form></div>" +
                                    "  <div class='defter-card'><label>AKSESUAR / GİDER</label><form action='/kasa_kaydet' method='POST'><div class='input-pair'><input type='text' name='aciklama' placeholder='Ürün/Not' required><input type='number' name='tutar' placeholder='Tutar' required></div><select name='tur' class='form-input' style='margin-top:10px;'><option value='AKSESUAR'>Aksesuar Satış</option><option value='GIDER'>Ödeme Çıkış</option></select><button class='btn-kasa'>KAYDET</button></form></div>" +
                                    "</div>" +
                                    "<div class='defter-card' style='margin-bottom:20px;'><label>TAMİR GİRİŞİ (ÜÇLÜ KUTU)</label><form action='/kasa_kaydet' method='POST'><div class='input-triple'><input type='text' name='aciklama' placeholder='Yapılan İşlem' required><input type='number' name='maliyet' placeholder='Maliyet' required><input type='number' name='tutar' placeholder='Satış Fiyatı' required></div><input type='hidden' name='tur' value='TAMIR'><button class='btn-kasa'>TAMİRİ KAYDET</button></form></div>" +
                                    "<div class='stats-grid' style='grid-template-columns:1fr 1fr; gap:15px; margin-bottom:20px;'>" +
                                    $"<div class='stat-card'><div class='stat-val' style='color:var(--green)'>{toplamGelir} ₺</div><div class='stat-lbl'>NET KÂR</div></div>" +
                                    $"<div class='stat-card'><div class='stat-val' style='color:var(--primary)'>{toplamGelir - toplamGider} ₺</div><div class='stat-lbl'>KASADAKİ NET</div></div>" +
                                    "</div>" +
                                    "<table style='width:100%; color:white;'><thead><tr style='text-align:left; color:var(--muted);'><th>Tarih</th><th>Tür</th><th>Açıklama</th><th>Maliyet</th><th>Tutar</th></tr></thead><tbody>" + rows.ToString() + "</tbody></table>" + Footer;
                            }
                            else if (rawUrl == "/kasa_kaydet" && method == "POST") {
                                var body = new StreamReader(request.InputStream).ReadToEnd();
                                var nv = HttpUtility.ParseQueryString(body);
                                KasaKaydet(nv["tur"], nv["aciklama"], nv["maliyet"] ?? "0", nv["tutar"]);
                                response.StatusCode = 302; response.Headers.Add("Location", "/kasa_defteri");
                                response.OutputStream.Close(); return;
                            }

                            
                            else if (rawUrl == "/kasa_defteri")
                            {
                                StringBuilder rows = new StringBuilder();
                                double toplamGelir = 0; double toplamGider = 0;
                                try {
                                    using (var conn = new SqliteConnection(connStr)) {
                                        conn.Open();
                                        using (var cmd = new SqliteCommand("SELECT * FROM kasa_defteri ORDER BY id DESC LIMIT 50", conn)) {
                                            using (var r = cmd.ExecuteReader()) {
                                                while (r.Read()) {
                                                    string t = r["tur"].ToString();
                                                    double tutar = 0; double.TryParse(r["tutar"].ToString(), out tutar);
                                                    double maliyet = 0; double.TryParse(r["maliyet"].ToString(), out maliyet);
                                                    if (t == "GIDER") toplamGider += tutar; else toplamGelir += (tutar - maliyet);
                                                    rows.Append($"<tr class='shop-row'><td>{r["tarih"]}</td><td><b>{t}</b></td><td>{r["aciklama"]}</td><td>{maliyet} ₺</td><td>{tutar} ₺</td></tr>");
                                                }
                                            }
                                        }
                                    }
                                } catch {}

                                html = GetHeader("Kasa Defteri", "/", "Ana Menu") + 
                                    "<div class='form-box' style='margin-bottom:20px;'>" +
                                    "  <div class='defter-row'>" +
                                    "    <div class='defter-card'><label>GÜN BAŞI DEVİR</label><form action='/kasa_kaydet' method='POST'><div class='input-pair'><input type='text' name='aciklama' value='Devir' readonly><input type='number' name='tutar' placeholder='Tutar' required></div><input type='hidden' name='tur' value='DEVIR'><button class='btn-kasa'>DEVİR KAYDET</button></form></div>" +
                                    "    <div class='defter-card'><label>AKSESUAR / GİDER</label><form action='/kasa_kaydet' method='POST'><div class='input-pair'><input type='text' name='aciklama' placeholder='Ürün/Not' required><input type='number' name='tutar' placeholder='Tutar' required></div><select name='tur' class='form-input' style='margin-top:10px;'><option value='AKSESUAR'>Aksesuar Satış</option><option value='GIDER'>Ödeme Çıkış</option></select><button class='btn-kasa'>KAYDET</button></form></div>" +
                                    "  </div>" +
                                    "  <div class='defter-card' style='margin-top:20px;'><label>TAMİR GİRİŞİ (ÜÇLÜ KUTU)</label><form action='/kasa_kaydet' method='POST'><div class='input-triple'><input type='text' name='aciklama' placeholder='Yapılan İşlem' required><input type='number' name='maliyet' placeholder='Maliyet' required><input type='number' name='tutar' placeholder='Satış Fiyatı' required></div><input type='hidden' name='tur' value='TAMIR'><button class='btn-kasa'>TAMİRİ KAYDET</button></form></div>" +
                                    "</div>" +
                                    "<div class='stats-grid' style='grid-template-columns:1fr 1fr; gap:15px; margin-bottom:20px;'>" +
                                    $"<div class='stat-card'><div class='stat-val' style='color:var(--green)'>{toplamGelir} ₺</div><div class='stat-lbl'>NET KÂR</div></div>" +
                                    $"<div class='stat-card'><div class='stat-val' style='color:var(--accent)'>{toplamGelir - toplamGider} ₺</div><div class='stat-lbl'>KASADAKİ NET</div></div>" +
                                    "</div>" +
                                    "<table style='width:100%; color:white; border-collapse:collapse;'><thead><tr style='text-align:left; color:var(--muted);'><th>Tarih</th><th>Tür</th><th>Açıklama</th><th>Maliyet</th><th>Tutar</th></tr></thead><tbody>" + rows.ToString() + "</tbody></table>" + Footer;
                            }
                            else if (rawUrl == "/kasa_kaydet" && method == "POST") {
                                var bodyReader = new StreamReader(request.InputStream).ReadToEnd();
                                var nv = HttpUtility.ParseQueryString(bodyReader);
                                KasaKaydet(nv["tur"], nv["aciklama"], nv["maliyet"] ?? "0", nv["tutar"]);
                                response.StatusCode = 302; response.Headers.Add("Location", "/kasa_defteri");
                                response.OutputStream.Close(); return;
                            }

                            
                            else if (rawUrl == "/kasa_defteri")
                            {
                                StringBuilder rows = new StringBuilder();
                                double toplamGelir = 0; double toplamGider = 0;
                                try {
                                    using (var conn = new SqliteConnection(connStr)) {
                                        conn.Open();
                                        using (var cmd = new SqliteCommand("SELECT * FROM kasa_defteri ORDER BY id DESC LIMIT 100", conn)) {
                                            using (var r = cmd.ExecuteReader()) {
                                                while (r.Read()) {
                                                    string t = r["tur"].ToString();
                                                    double tutar = 0; double.TryParse(r["tutar"].ToString(), out tutar);
                                                    double maliyet = 0; double.TryParse(r["maliyet"].ToString(), out maliyet);
                                                    if (t == "GIDER") toplamGider += tutar; 
                                                    else if (t == "DEVIR") { /* Devir kâr degildir */ }
                                                    else toplamGelir += (tutar - maliyet);
                                                    rows.Append($"<tr class='shop-row'><td>{r["tarih"]}</td><td><b>{t}</b></td><td>{r["aciklama"]}</td><td>{maliyet} ₺</td><td>{tutar} ₺</td></tr>");
                                                }
                                            }
                                        }
                                    }
                                } catch {}

                                html = GetHeader("Kasa Defteri", "/", "Ana Menü") + 
                                    "<div class='form-box' style='margin-bottom:20px;'>" +
                                    "  <div class='defter-row'>" +
                                    "    <div class='defter-card'><label>GÜN BAŞI DEVİR</label><form action='/kasa_kaydet' method='POST'><div class='input-pair'><input type='text' name='aciklama' value='Devir' readonly><input type='number' name='tutar' placeholder='Tutar' required></div><input type='hidden' name='tur' value='DEVIR'><button class='btn-kasa'>DEVİR KAYDET</button></form></div>" +
                                    "    <div class='defter-card'><label>AKSESUAR / GİDER</label><form action='/kasa_kaydet' method='POST'><div class='input-pair'><input type='text' name='aciklama' placeholder='Ürün/Not' required><input type='number' name='tutar' placeholder='Tutar' required></div><select name='tur' class='form-input' style='margin-top:10px;'><option value='AKSESUAR'>Aksesuar Satış</option><option value='GIDER'>Ödeme Çıkış</option></select><button class='btn-kasa'>KAYDET</button></form></div>" +
                                    "  </div>" +
                                    "  <div class='defter-card' style='margin-top:20px;'><label>TAMİR GİRİŞİ (ÜÇLÜ KUTU)</label><form action='/kasa_kaydet' method='POST'><div class='input-triple'><input type='text' name='aciklama' placeholder='Yapılan İşlem' required><input type='number' name='maliyet' placeholder='Maliyet' required><input type='number' name='tutar' placeholder='Satış Fiyatı' required></div><input type='hidden' name='tur' value='TAMIR'><button class='btn-kasa'>TAMİRİ KAYDET</button></form></div>" +
                                    "</div>" +
                                    "<div class='stats-grid' style='grid-template-columns:1fr 1fr; gap:15px; margin-bottom:20px;'>" +
                                    $"<div class='stat-card'><div class='stat-val' style='color:var(--green)'>{toplamGelir} ₺</div><div class='stat-lbl'>TOPLAM KÂR</div></div>" +
                                    $"<div class='stat-card'><div class='stat-val' style='color:var(--accent)'>{toplamGelir - toplamGider} ₺</div><div class='stat-lbl'>KASADAKİ NET</div></div>" +
                                    "</div>" +
                                    "<table style='width:100%; color:white; border-collapse:collapse;'><thead><tr style='text-align:left; color:var(--muted);'><th>Tarih</th><th>Tür</th><th>Açıklama</th><th>Maliyet</th><th>Tutar</th></tr></thead><tbody>" + rows.ToString() + "</tbody></table>" + Footer;
                            }
                            else if (rawUrl == "/kasa_kaydet" && method == "POST") {
                                var bodyReader = new StreamReader(request.InputStream).ReadToEnd();
                                var nv = HttpUtility.ParseQueryString(bodyReader);
                                KasaKaydet(nv["tur"], nv["aciklama"], nv["maliyet"] ?? "0", nv["tutar"]);
                                response.StatusCode = 302; response.Headers.Add("Location", "/kasa_defteri");
                                response.OutputStream.Close(); return;
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
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa_defteri (id INTEGER PRIMARY KEY AUTOINCREMENT, tarih TEXT, tur TEXT, aciklama TEXT, maliyet TEXT, tutar TEXT);", connection)) { cmd.ExecuteNonQuery(); }
                    
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
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa_defteri (id INTEGER PRIMARY KEY AUTOINCREMENT, tarih TEXT, tur TEXT, aciklama TEXT, maliyet TEXT, tutar TEXT);", connection)) { cmd.ExecuteNonQuery(); }
                    
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
                                                    sb.Append("<form action='/sil' method='POST' onsubmit=\"return confirm('Silmek istediginize emin misiniz?');\">");
                                                    sb.AppendFormat("<input type='hidden' name='id' value='{0}'>", r["id"]);
                                                    sb.Append("<input type='hidden' name='git' value='arsiv'>");
                                                    sb.Append("<button type='submit' class='action-btn btn-danger' style='padding:6px 14px; font-size:12px;'>Kayıt Sil</button>");
                                                    sb.Append("</form></div></div>");
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
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa_defteri (id INTEGER PRIMARY KEY AUTOINCREMENT, tarih TEXT, tur TEXT, aciklama TEXT, maliyet TEXT, tutar TEXT);", connection)) { cmd.ExecuteNonQuery(); }
                    
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
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa_defteri (id INTEGER PRIMARY KEY AUTOINCREMENT, tarih TEXT, tur TEXT, aciklama TEXT, maliyet TEXT, tutar TEXT);", connection)) { cmd.ExecuteNonQuery(); }
                    
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
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa_defteri (id INTEGER PRIMARY KEY AUTOINCREMENT, tarih TEXT, tur TEXT, aciklama TEXT, maliyet TEXT, tutar TEXT);", connection)) { cmd.ExecuteNonQuery(); }
                    
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
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa_defteri (id INTEGER PRIMARY KEY AUTOINCREMENT, tarih TEXT, tur TEXT, aciklama TEXT, maliyet TEXT, tutar TEXT);", connection)) { cmd.ExecuteNonQuery(); }
                    

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

        
        private static (int toplam, int aktif, int satilan, double kar) GetStats()
        {
            int t = 0, a = 0, s = 0;
            double k = 0;
            try {
                using (var connection = new SqliteConnection(connStr)) {
                    connection.Open();
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa_defteri (id INTEGER PRIMARY KEY AUTOINCREMENT, tarih TEXT, tur TEXT, aciklama TEXT, maliyet TEXT, tutar TEXT);", connection)) { cmd.ExecuteNonQuery(); }
                    
                    using (var cmd = new SqliteCommand("SELECT fiyat, satis_fiyati, durum FROM vitrin", connection)) {
                        using (var reader = cmd.ExecuteReader()) {
                            while (reader.Read()) {
                                t++;
                                string d = reader["durum"].ToString();
                                if (d == "VITRIN" || d == "TAMIRDE") a++;
                                else {
                                    s++;
                                    double alis = 0, satis = 0;
                                    double.TryParse(reader["fiyat"].ToString(), out alis);
                                    double.TryParse(reader["satis_fiyati"].ToString(), out satis);
                                    k += (satis - alis);
                                }
                            }
                        }
                    }
                }
            } catch {}
            return (t, a, s, k);
        }



        
        private static void KasaKaydet(string tur, string aciklama, string maliyet, string tutar) {
            try {
                using (var conn = new SqliteConnection(connStr)) {
                    conn.Open();
                    using (var cmd = new SqliteCommand("INSERT INTO kasa_defteri (tarih, tur, aciklama, maliyet, tutar) VALUES (@tarih, @tur, @aciklama, @maliyet, @tutar)", conn)) {
                        cmd.Parameters.AddWithValue("@tarih", DateTime.Now.ToString("dd.MM.yyyy HH:mm"));
                        cmd.Parameters.AddWithValue("@tur", tur);
                        cmd.Parameters.AddWithValue("@aciklama", aciklama);
                        cmd.Parameters.AddWithValue("@maliyet", string.IsNullOrEmpty(maliyet) ? "0" : maliyet);
                        cmd.Parameters.AddWithValue("@tutar", string.IsNullOrEmpty(tutar) ? "0" : tutar);
                        cmd.ExecuteNonQuery();
                    }
                }
            } catch {}
        }

        private static void TabloyuHazirla() {
            try
            {
                using (var connection = new SqliteConnection(connStr))
                {
                    connection.Open();
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa_defteri (id INTEGER PRIMARY KEY AUTOINCREMENT, tarih TEXT, tur TEXT, aciklama TEXT, maliyet TEXT, tutar TEXT);", connection)) { cmd.ExecuteNonQuery(); }
                    
                    using (var command = new SqliteCommand("CREATE TABLE IF NOT EXISTS vitrin (id INTEGER PRIMARY KEY AUTOINCREMENT, marka TEXT, model TEXT, imei TEXT, alinma_tarihi TEXT, fiyat TEXT, satis_fiyati TEXT, durum TEXT, kutu_fatura TEXT, garanti TEXT, teslim_tarihi TEXT, notlar TEXT);", connection))
                    {
                        command.ExecuteNonQuery();
                    }
                    // Eski DB'de kolon yoksa ekle (migration)
                    try {
                        using (var altCmd = new SqliteCommand("ALTER TABLE vitrin ADD COLUMN teslim_tarihi TEXT;", connection))
                            altCmd.ExecuteNonQuery();
                    } catch { /* zaten varsa hata yok */ }
                }
            }
            catch (Exception ex) { Console.WriteLine("❌ Veritabanı Tablo Hatası: " + ex.Message); }
        }
    }
}

