from pathlib import Path

src = Path('/home/ubuntu/calkan_gsm_exe/Program.cs')
text = src.read_text(encoding='utf-8')

old_config = '''                    "KULLANICI1=calkanadmin:fcalkan2626\\n" +
                    "KULLANICI2=teknisyen:calkan1234\\n" +
                    "PORT=8080\\n");'''

new_config = '''                    "KULLANICI1=admin:emir2626\\n" +
                    "KULLANICI2=calkanadmin:fcalkan2626\\n" +
                    "PORT=2626\\n");'''

text = text.replace(old_config, new_config)

# Ayrica varsayilan configPort degiskenini de 2626 yapalim ki config yoksa bile oyle baslasin
text = text.replace('private static string configPort = "8080";', 'private static string configPort = "2626";')

src.write_text(text, encoding='utf-8')
