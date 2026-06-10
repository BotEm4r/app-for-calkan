using System.Runtime.InteropServices;
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
            if (File.Exists(configPath))
            {
                foreach (string line in File.ReadAllLines(configPath))
                {
                    string trimmedLine = line.Trim();
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#")) continue;
                    if (!trimmedLine.Contains("=")) continue;
                    string[] kvParts = trimmedLine.Split('=', 2);
                    string key = kvParts[0].Trim().ToUpper();
                    string val = kvParts[1].Trim();
                    if (key.StartsWith("KULLANICI"))
                    {
                        string[] parts = val.Split(':', 2);
                        if (parts.Length == 2) Kullanicilar[parts[0].Trim()] = parts[1].Trim();
                    }
                    else if (key == "PORT") configPort = val;
                }
            }

            if (Kullanicilar.Count == 0)
            {
                Kullanicilar["calkanadmin"] = "fcalkan2626";
            }
        }

        public static void Main(string[] args)
        {
            ConfigYukle();
            TabloyuHazirla();
            StartServer(configPort);
        }

        private static void StartServer(string port)
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://*:{port}/");
            try
            {
                listener.Start();
                Console.WriteLine($"🚀 Linux Sunucusu Aktif! Port: {port}");
                while (true)
                {
                    HttpListenerContext context = listener.GetContext();
                    ThreadPool.QueueUserWorkItem((o) => HandleRequest(context));
                }
            }
            catch (Exception ex) { Console.WriteLine("❌ Sunucu Hatası: " + ex.Message); }
        }

        private static void HandleRequest(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            string ip = GetIP(request);

            if (IsRateLimited(ip)) { response.StatusCode = 429; response.Close(); return; }
            if (Interlocked.Increment(ref activeConnections) > MAX_CONNECTIONS) { response.StatusCode = 503; response.Close(); return; }

            try
            {
                string rawUrl = request.RawUrl;
                string method = request.HttpMethod;
                string html = "";
                string Footer = "</div></body></html>";

                bool isLoggedIn = false;
                var cookie = request.Cookies["session"];
                if (cookie != null && cookie.Value == SessionValue) isLoggedIn = true;

                if (!isLoggedIn && rawUrl != "/login")
                {
                    response.StatusCode = 302;
                    response.Headers.Add("Location", "/login");
                    response.OutputStream.Close();
                    return;
                }

                if (rawUrl == "/login")
                {
                    if (method == "POST")
                    {
                        if (IsLocked(ip)) { html = GetHeader("Giriş Engellendi") + "<div class='error'>Çok fazla deneme! 15 dk bekleyin.</div>" + Footer; }
                        else
                        {
                            var body = new StreamReader(request.InputStream).ReadToEnd();
                            var nv = HttpUtility.ParseQueryString(body);
                            string u = nv["u"], p = nv["p"];
                            if (Kullanicilar.TryGetValue(u ?? "", out string pass) && pass == p)
                            {
                                ResetFail(ip);
                                var sessCookie = new Cookie("session", SessionValue) { Path = "/", Expires = DateTime.Now.AddDays(30) };
                                response.AppendCookie(sessCookie);
                                response.StatusCode = 302; response.Headers.Add("Location", "/");
                                response.OutputStream.Close(); return;
                            }
                            else { RecordFail(ip); response.StatusCode = 302; response.Headers.Add("Location", "/login?hata=1"); response.OutputStream.Close(); return; }
                        }
                    }
                    else
                    {
                        html = GetHeader("Mağaza Oturumu") + @"
                        <div class='form-box'>
                            <form method='POST'>
                                <div class='form-field'><label>KULLANICI ADI</label><input type='text' name='u' class='form-input' required></div>
                                <div class='form-field'><label>ŞİFRE</label><div class='password-wrapper'><input type='password' name='p' id='passInput' class='form-input' required><button type='button' class='toggle-pass' onclick='togglePass()'><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z'></path><circle cx='12' cy='12' r='3'></circle></svg></button></div></div>
                                <label class='remember-me'><input type='checkbox' name='r' checked> Oturumu Açık Tut (30 Gün)</label>
                                <button type='submit' class='btn-kasa'>Sistemi Aç</button>
                            </form>
                        </div>" + Footer;
                    }
                }
                else if (rawUrl == "/")
                {
                    var stats = GetStats();
                    html = GetHeader("Mağaza Yönetim Tezgahı") + $@"
                    <div class='stats-grid'>
                        <div class='stat-card'><div class='stat-val'>{stats.toplam}</div><div class='stat-lbl'>TOPLAM KAYIT</div></div>
                        <div class='stat-card'><div class='stat-val'>{stats.aktif}</div><div class='stat-lbl'>AKTİF CİHAZ</div></div>
                        <div class='stat-card stat-kar'><div class='stat-val'>{stats.kar} ₺</div><div class='stat-lbl'>TOPLAM KÂR</div></div>
                    </div>
                    <div class='menu-layout'>
                        <a href='/tamir_ekle' class='menu-card'><div class='label'>Yeni Tamir Kaydı</div><div class='desc'>Müşteri bilgileri ve arıza durum kaydı oluşturun.</div></a>
                        <a href='/vitrin_ekle' class='menu-card'><div class='label'>Vitrine Ürün Ekle</div><div class='desc'>Satışa çıkarılacak yeni cihaz stok girişi yapın.</div></a>
                        <a href='/tamir_listele' class='menu-card'><div class='label'>Tamir Bekleyenler</div><div class='desc'>Servisteki veya teslime hazır cihaz listesi.</div></a>
                        <a href='/vitrin_listele' class='menu-card'><div class='label'>Vitrin Stok Listesi</div><div class='desc'>Şu an rafta satılmayı bekleyen güncel envanter.</div></a>
                        <a href='/kasa_defteri' class='menu-card menu-full' style='border-color:var(--accent); margin-top:10px;'><div class='label'>📔 Kasa Defteri</div><div class='desc'>Günlük devir, aksesuar satışı, ödeme çıkış ve tamir kâr takibi.</div></a>
                        <a href='/arsiv_panel' class='menu-card menu-full'><div class='label'>Geçmiş İşlemler Arşivi</div><div class='desc'>Tamamlanıp teslim edilmiş tamirler ve satılmış eski cihazların dökümü.</div></a>
                    </div>" + Footer;
                }
                else if (rawUrl == "/kasa_defteri")
                {
                    StringBuilder rows = new StringBuilder();
                    double tGelir = 0, tGider = 0;
                    using (var conn = new SqliteConnection(connStr)) {
                        conn.Open();
                        using (var cmd = new SqliteCommand("SELECT * FROM kasa_defteri ORDER BY id DESC LIMIT 100", conn)) {
                            using (var r = cmd.ExecuteReader()) {
                                while (r.Read()) {
                                    string t = r["tur"].ToString();
                                    double tutar = 0; double.TryParse(r["tutar"].ToString(), out tutar);
                                    double mal = 0; double.TryParse(r["maliyet"].ToString(), out mal);
                                    if (t == "GIDER") tGider += tutar; 
                                    else if (t != "DEVIR") tGelir += (tutar - mal);
                                    rows.Append($"<tr class='shop-row'><td>{r["tarih"]}</td><td><b>{t}</b></td><td>{r["aciklama"]}</td><td>{mal} ₺</td><td>{tutar} ₺</td></tr>");
                                }
                            }
                        }
                    }
                    html = GetHeader("Kasa Defteri", "/", "Ana Menü") + $@"
                    <div class='form-box' style='margin-bottom:20px;'>
                        <div class='defter-row'>
                            <div class='defter-card'><label>GÜN BAŞI DEVİR</label><form action='/kasa_kaydet' method='POST'><div class='input-pair'><input type='text' name='aciklama' value='Devir' readonly><input type='number' name='tutar' placeholder='Tutar' required></div><input type='hidden' name='tur' value='DEVIR'><button class='btn-kasa'>DEVİR KAYDET</button></form></div>
                            <div class='defter-card'><label>AKSESUAR / GİDER</label><form action='/kasa_kaydet' method='POST'><div class='input-pair'><input type='text' name='aciklama' placeholder='Ürün/Not' required><input type='number' name='tutar' placeholder='Tutar' required></div><select name='tur' class='form-input' style='margin-top:10px;'><option value='AKSESUAR'>Aksesuar Satış</option><option value='GIDER'>Ödeme Çıkış</option></select><button class='btn-kasa'>KAYDET</button></form></div>
                        </div>
                        <div class='defter-card' style='margin-top:20px;'><label>TAMİR GİRİŞİ (ÜÇLÜ KUTU)</label><form action='/kasa_kaydet' method='POST'><div class='input-triple'><input type='text' name='aciklama' placeholder='Yapılan İşlem' required><input type='number' name='maliyet' placeholder='Maliyet' required><input type='number' name='tutar' placeholder='Satış Fiyatı' required></div><input type='hidden' name='tur' value='TAMIR'><button class='btn-kasa'>TAMİRİ KAYDET</button></form></div>
                    </div>
                    <div class='stats-grid' style='grid-template-columns:1fr 1fr; gap:15px; margin-bottom:20px;'>
                        <div class='stat-card'><div class='stat-val' style='color:var(--green)'>{tGelir} ₺</div><div class='stat-lbl'>TOPLAM KÂR</div></div>
                        <div class='stat-card'><div class='stat-val' style='color:var(--accent)'>{tGelir - tGider} ₺</div><div class='stat-lbl'>KASADAKİ NET</div></div>
                    </div>
                    <table style='width:100%; color:white; border-collapse:collapse;'><thead><tr style='text-align:left; color:var(--muted);'><th>Tarih</th><th>Tür</th><th>Açıklama</th><th>Maliyet</th><th>Tutar</th></tr></thead><tbody>{rows}</tbody></table>" + Footer;
                }
                else if (rawUrl == "/kasa_kaydet" && method == "POST") {
                    var body = new StreamReader(request.InputStream).ReadToEnd();
                    var nv = HttpUtility.ParseQueryString(body);
                    KasaKaydet(nv["tur"], nv["aciklama"], nv["maliyet"] ?? "0", nv["tutar"]);
                    response.StatusCode = 302; response.Headers.Add("Location", "/kasa_defteri");
                    response.OutputStream.Close(); return;
                }
                // Diger route'lar (vitrin_ekle, tamir_ekle vb.) buraya gelecek...
                else { html = GetHeader("Sayfa Bulunamadı") + "<div class='empty-state'>Aradığınız modül henüz eklenmedi.</div>" + Footer; }

                byte[] buffer = Encoding.UTF8.GetBytes(html);
                response.ContentLength64 = buffer.Length;
                response.ContentType = "text/html; charset=utf-8";
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
            catch { try { response.Close(); } catch { } }
            finally { Interlocked.Decrement(ref activeConnections); }
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

        private static (int toplam, int aktif, int satilan, double kar) GetStats() {
            int t = 0, a = 0, s = 0; double k = 0;
            try {
                using (var conn = new SqliteConnection(connStr)) {
                    conn.Open();
                    using (var cmd = new SqliteCommand("SELECT fiyat, satis_fiyati, durum FROM vitrin", conn)) {
                        using (var reader = cmd.ExecuteReader()) {
                            while (reader.Read()) {
                                t++; string d = reader["durum"].ToString();
                                if (d == "VITRIN" || d == "TAMIRDE") a++;
                                else {
                                    s++; double alis = 0, satis = 0;
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

        private static void TabloyuHazirla() {
            try {
                using (var conn = new SqliteConnection(connStr)) {
                    conn.Open();
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS kasa_defteri (id INTEGER PRIMARY KEY AUTOINCREMENT, tarih TEXT, tur TEXT, aciklama TEXT, maliyet TEXT, tutar TEXT);", conn)) { cmd.ExecuteNonQuery(); }
                    using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS vitrin (id INTEGER PRIMARY KEY AUTOINCREMENT, marka TEXT, model TEXT, imei TEXT, alinma_tarihi TEXT, fiyat TEXT, satis_fiyati TEXT, durum TEXT, kutu_fatura TEXT, garanti TEXT, teslim_tarihi TEXT, notlar TEXT);", conn)) { cmd.ExecuteNonQuery(); }
                }
            } catch {}
        }

        private static string GetHeader(string title, string backUrl = "", string backLabel = "") {
            string backBtn = string.IsNullOrEmpty(backUrl) ? "" : $"<a href='{backUrl}' class='theme-toggle'>← {backLabel}</a>";
            return $@"<!DOCTYPE html><html lang='tr'><head><meta charset='UTF-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'><title>{title}</title>{GetCSS()}<script>function togglePass(){{var x=document.getElementById('passInput');if(x.type==='password')x.type='text';else x.type='password';}}</script></head><body><div class='wrap'><div class='shop-nav'><div class='shop-title'><div class='shop-badge'></div><div class='shop-name'>ÇALKAN GSM</div></div><div class='nav-right'>{backBtn}<div class='shop-status'>PANEL AKTİF</div></div></div><div class='view-heading'><h1 class='view-title'>{title}</h1></div>";
        }

        private static string GetCSS() => @"<style>:root{--bg:#0f172a;--surface:#1e293b;--border:#384152;--text:#f8fafc;--muted:#94a3b8;--accent:#38bdf8;--green:#4ade80;--primary:#38bdf8;--shadow:0 10px 25px -5px rgba(0,0,0,0.3);} body{font-family:'Plus Jakarta Sans',sans-serif;background:var(--bg);color:var(--text);margin:0;padding:0;} .wrap{max-width:750px;margin:0 auto;padding:50px 20px;} .shop-nav{display:flex;justify-content:space-between;align-items:center;background:var(--surface);padding:18px 24px;border-radius:16px;margin-bottom:40px;border:1px solid var(--border);} .shop-title{display:flex;align-items:center;gap:12px;} .shop-badge{width:12px;height:12px;background:var(--accent);border-radius:4px;} .shop-name{font-size:16px;font-weight:700;} .view-title{font-size:26px;font-weight:700;} .stats-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:15px;margin-bottom:30px;} .stat-card{background:var(--surface);padding:20px;border-radius:12px;text-align:center;border:1px solid var(--border);} .stat-val{font-size:24px;font-weight:800;} .stat-lbl{font-size:12px;color:var(--muted);margin-top:5px;} .menu-layout{display:grid;grid-template-columns:1fr 1fr;gap:20px;} .menu-card{background:var(--surface);padding:24px;border-radius:16px;color:var(--text);text-decoration:none;border:1px solid var(--border);display:flex;flex-direction:column;justify-content:space-between;height:140px;} .menu-card:hover{border-color:var(--accent);} .menu-full{grid-column:span 2;height:100px;} .form-box{background:var(--surface);padding:32px;border-radius:16px;border:1px solid var(--border);} .form-input{width:100%;padding:14px;background:var(--bg);border:1px solid var(--border);border-radius:10px;color:var(--text);margin-top:5px;} .btn-kasa{background:var(--primary);color:white;border:none;padding:12px;border-radius:10px;width:100%;cursor:pointer;font-weight:700;margin-top:20px;} .defter-row{display:grid;grid-template-columns:1fr 1fr;gap:15px;} .input-pair,.input-triple{display:flex;border:1px solid var(--border);border-radius:8px;overflow:hidden;background:#000;margin-top:10px;} .input-pair input,.input-triple input{background:transparent;border:none;color:white;padding:10px;width:100%;border-right:1px solid var(--border);} .input-triple input{width:33.33%;} .shop-row{background:var(--surface);border:1px solid var(--border);border-radius:14px;padding:15px;margin-bottom:10px;} .theme-toggle{text-decoration:none;color:var(--text);font-size:14px;}</style>";

        private static string GetIP(HttpListenerRequest req) { string f = req.Headers["X-Forwarded-For"]; return !string.IsNullOrEmpty(f) ? f.Split(',')[0].Trim() : req.RemoteEndPoint?.Address?.ToString() ?? "unknown"; }
        private static bool IsRateLimited(string ip) { return false; } // Sade sürüm için basitleştirildi
        private static bool IsLocked(string ip) { return false; }
        private static void RecordFail(string ip) { }
        private static void ResetFail(string ip) { }
    }
}
