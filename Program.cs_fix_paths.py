from pathlib import Path

src = Path('/home/ubuntu/calkan_gsm_exe/Program.cs')
text = src.read_text(encoding='utf-8')

# Tek EXE modunda AppContext.BaseDirectory bazen yanlis olabilir. 
# En saglam yol ProcessPath kullanmaktir.
old_db = 'private static string dbPath = Path.Combine(AppContext.BaseDirectory, "data", "calkan_gsm.db");'
new_db = 'private static string BaseDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName) ?? AppContext.BaseDirectory;\n        private static string dbPath = Path.Combine(BaseDir, "data", "calkan_gsm.db");'

text = text.replace(old_db, new_db)
text = text.replace('string configPath = Path.Combine(AppContext.BaseDirectory, "config.txt");',
                    'string configPath = Path.Combine(BaseDir, "config.txt");')

src.write_text(text, encoding='utf-8')
