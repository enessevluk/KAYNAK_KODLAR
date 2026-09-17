RAVEN MAP PANEL - RELEASE CENTER
================================

YENI LISANS MIMARISI (v0.7 License Center)
------------------------------------------

Raven Map Panel lisansi artik dogrudan mevcut PHP License Center uzerinden
kontrol edilir: https://license.ravenrusttr.com.tr

Map lisansi icin Raven.Server veya ayri bir VDS kurmak gerekmez. Uygulama ilk
aktivasyonda RVN- lisans anahtarini ister; her acilista ve her harita uretimi
oncesinde cevrimici kontrol yapar. Internet veya gecerli lisans yoksa harita
uretmez; cevrimdisi token ya da grace ile uretime izin vermez.

Musteri EXE/ZIP olusturmadan once RAVEN.bat > A AYARLAR ekraninda License
Center, Shopier satin alma ve destek URL'lerini kontrol et. 3 ve 4 numarali
Raven.Server/VDS paketleri legacy/opsiyoneldir; Map lisansi icin kullanilmaz.

KUR VE UNUT - VDS ILK KURULUM (v1.1.0+)
-----------------------------------------

1. RAVEN.bat icinden 4 numarali ILK SERVER KURULUMU - OWNER ONLY
   paketini olustur.
2. Olusan SERVER_INSTALL_OWNER_ONLY klasorunu yalniz kendi VDS'ine kopyala.
   Bu paket license private key icerdigi icin musteriye veya internete verme.
3. Paketteki CLOUDFLARE_DOMAIN_REHBERI.txt adimlariyla A kaydini VDS IP'sine
   yonlendir.
4. VDS'de KUR_RAVEN_SERVER.bat dosyasini cift tikla; UAC iznini ver, domaini
   ve en az 12 karakterli admin sifresini gir, verilen TOTP kodunu Authenticator
   uygulamana kaydet.
5. Kurucu RavenMap ve Caddy'yi Windows servisi yapar, 80/443 firewall
   kurallarini acar, otomatik TLS kurar ve 5 dakikalik health monitor ekler.

Kurulumdan sonra VDS yeniden baslasa da servisler otomatik acilir. Normal
baslatma icin tekrar BAT calistirmak gerekmez. Servis kaldirma gerekiyorsa
KALDIR_RAVEN_SERVER.bat kullanilir; veri, config, secret ve yedekler otomatik
silinmez.

Cloudflare API tokeni gerekmez. 5088 portunu internete acma; Raven.Server bu
surumde yalniz 127.0.0.1:5088 uzerinden Caddy ile haberlesir.

Giris noktasi:
  RAVEN.bat

Raven Release Center yalniz dort paket uretir:

  1 MUSTERI CLIENT ZIP
    OUTPUT\vX.Y.Z\CLIENT_INSTALL\

  2 CLIENT GUNCELLEMESI
    OUTPUT\vX.Y.Z\CLIENT_UPDATE\

  3 SERVER GUNCELLEMESI
    OUTPUT\vX.Y.Z\SERVER_UPDATE\

  4 ILK SERVER KURULUMU - OWNER ONLY
    OUTPUT\vX.Y.Z\SERVER_INSTALL_OWNER_ONLY\

Yeni yayin oncesinde S secenegi ile yeni X.Y.Z surumunu belirle.
HTTPS API veya Rust yolu degisecekse A secenegini kullan.

URETIM GUVENCESI
----------------

Her uretimde Raven otomatik olarak:
  - kaynakta private key sizintisi olmadigini kontrol eder,
  - ilgili testleri calistirir,
  - gercek HTTPS /health adresini kontrol eder,
  - client ve auth domainlerinin ayni oldugunu dogrular,
  - paketi gecici klasorde hazirlar,
  - SHA-256 hesaplar,
  - update paketlerini RSA ile imzalar,
  - yalniz tum adimlar basariliysa OUTPUT klasorune teslim eder.

Basarisiz islem OUTPUT klasorunde yarim paket birakmaz.

SERVER UPDATE GUVENLI UYGULAMA
------------------------------

SERVER_UPDATE klasorundeki ZIP, ZIP.sig ve ZIP.sha256 dosyalarini VDS Raven
server kokune birlikte kopyala ve RAVEN_SERVER.bat > 6 secenegini calistir.
Updater yeni serveri arka planda baslatir ve yerel /health sonucunda paket
manifestindeki surumu bekler. Server acilmazsa veya yanlis surum bildirirse
eski App ve prefab katalogu otomatik geri yuklenir ve eski server baslatilir.
Yedekler yalniz yeni server saglik kontrolunden gectikten sonra temizlenir.

HTTPS
-----

Yayin paketlerinde HTTP, localhost ve 127.0.0.1 kabul edilmez.
Client config her zaman gercek HTTPS domainini kullanir ve
AllowInsecureHttpForTesting=false olarak uretilir.

VDS icinde Raven.Server http://0.0.0.0:5088 dinleyebilir. Bu yalniz reverse
proxy ile Raven arasindaki yerel baglantidir. Musteri trafigi IIS, Nginx veya
Caddy uzerinden https://api.ravenrusttr.com.tr adresine gelir.

Production Raven.Server dogrudan gelen HTTP isteklerini kanonik HTTPS domaine
yonlendirir ve HSTS kullanir.

ANAHTARLAR
----------

Private keyler yalniz gizli RAVENMAP\.raven\OWNER_SECRETS sistem alanindadir.
Musteri client ZIP, client update ve server update paketlerine private key
eklenmez.

SERVER_INSTALL_OWNER_ONLY paketi ilk VDS kurulumu icin license private key
icerir. Bu paket kesinlikle musteriye verilmez.

CIKTILARI TANIMA
----------------

Her paket klasorunde artifact-info.json bulunur. Burada:
  - paket turu,
  - surum,
  - Build ID,
  - HTTPS API adresi,
  - uretim zamani
yazar.

Her ana paket yaninda .sha256 dosyasi bulunur. Imzali update paketlerinde
ayrica .sig dosyasi vardir. Surum klasorundeki release-summary.json tum
ciktilari tek listede toplar.

TEK CIKTI KURALI
----------------

Client ve server derlemeleri once Windows gecici klasorunde hazirlanir,
dogrulanir ve yalniz basariliysa OUTPUT\vX.Y.Z altina teslim edilir.
.raven\CLIENT, .raven\SERVER veya .raven\ARCHIVE calisma kopyalari artik
olusturulmaz ve paketleme icin kullanilmaz.

Eski kurulumlardan kalan bu klasorler yalniz gecmis kalintisidir. Yeni bir
paketi dogruladiktan sonra elle silinebilir; kaynak kod ve owner anahtarlari
.raven\KAYNAK_KODLAR ile .raven\OWNER_SECRETS icinde kalir.
