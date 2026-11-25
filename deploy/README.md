# Canlıya alma: Ubuntu + Docker + Caddy

Her şey tek sunucuda çalışır: SQL Server, API, web ve Caddy. İnternete yalnızca Caddy açıktır (80/443);
Let's Encrypt sertifikasını kendisi alır ve yeniler. Veritabanı, API ve web Docker ağı içinde kalır.

```
İnternet ──HTTPS──▶ wfm-caddy ─▶ WEB_HOST ─▶ wfm-web:8080
                              └▶ API_HOST ─▶ wfm-api:8080 ─▶ wfm-mssql
```

Gereksinimler: Ubuntu 24.04 x86_64 (SQL Server imajı ARM'de çalışmaz), en az 4 GB RAM.

Alan adı yoksa `sslip.io` adresleri kullanılır (`wfm.31-186-24-89.sslip.io` doğrudan IP'ye çözülür).
Kendi alan adınıza geçmek için iki A kaydını (ör. `wfm.ornek.com`, `wfm-api.ornek.com`) sunucu IP'sine
yönlendirin, `.env` içindeki `WEB_HOST`/`API_HOST` değerlerini değiştirin ve `docker compose up -d` çalıştırın.
Mobil uygulamada `MauiProgram.ProductionApiUrl` da yeni API adresi olmalı.

## 1. Sunucuyu hazırlayın (bir kez)

```bash
git clone <repo-adresi> ~/apps/wfm
sudo bash ~/apps/wfm/deploy/server-setup.sh   # Docker, ufw (22/80/443), swap, saat dilimi
exit                                          # docker grubu için yeniden bağlanın
```

## 2. Ayarları girin

```bash
cd ~/apps/wfm/deploy
cp .env.example .env && chmod 600 .env
nano .env    # WEB_HOST, API_HOST, DB_PASSWORD, JWT_KEY, ADMIN_PASSWORD
```

Rastgele anahtar üretmek için `openssl rand -base64 48` kullanabilirsiniz.

## 3. Başlatın

```bash
docker compose up -d --build
docker compose logs -f api     # "Now listening on" görünce Ctrl+C
```

İlk açılışta veritabanı ve tablolar otomatik oluşturulur. Caddy ilk istekte sertifika alır (birkaç saniye).

Kontrol: `https://WEB_HOST` giriş sayfasını, `https://API_HOST` ise `WFM API` yazısını göstermeli.
Sertifika sorunu olursa: `docker compose logs caddy`.

## 4. Mobil uygulama

Telefonlar API'ye `https://API_HOST` üzerinden bağlanır. Release derlemesi `MauiProgram.ProductionApiUrl`
adresini kullanır; `Preferences["api_base_url"]` ayarlanmışsa her zaman o kullanılır.

## iPhone push bildirimi (APNs)

Telefon kilitliyken ya da uygulama kapalıyken iPhone'a bildirim Apple'ın push servisiyle gider. Ayar yoksa push kapalı
kalır; bildirimler uygulama içinde ve uygulama açıkken çalışmaya devam eder.

1. Apple Developer hesabında bir APNs anahtarı (`AuthKey_XXXX.p8`) oluşturun. Anahtarı **repoya veya mesaja koymayın**,
   doğrudan sunucuya kopyalayın:

   ```bash
   # Kendi bilgisayarınızdan:
   scp AuthKey_XXXX.p8 ilkeradm@31.186.24.89:~/apps/wfm/deploy/secrets/apns.p8
   # Sunucuda: API konteyneri "app" kullanıcısıyla (uid 1654) çalışır; dosyayı okuyabilmeli ama klasörü listeleyememeli.
   chmod 711 ~/apps/wfm/deploy/secrets
   chmod 604 ~/apps/wfm/deploy/secrets/apns.p8
   ```

2. `.env` içine anahtar kimliği, ekip kimliği ve uygulamanın Bundle ID'sini yazın (bunlar gizli değildir):

   ```bash
   APNS_ENABLED=true
   APNS_KEY_ID=XXXXXXXXXX
   APNS_TEAM_ID=YYYYYYYYYY
   APNS_BUNDLE_ID=com.wfm.saha
   APNS_SANDBOX=true   # Xcode/geliştirme imzalı sürüm; TestFlight/App Store için false
   ```

3. `docker compose up -d api` ile API'yi yeniden başlatın. Kayıtta `APNs hazır (sandbox, ...)` satırı görünmeli:
   `docker compose logs api | grep APNs`.

iPhone sürümü, Push Notifications yetkisi açık bir profille imzalanmalıdır. `Platforms/iOS/Entitlements.plist` içindeki
`aps-environment` geliştirme için `development`, TestFlight/App Store için `production` olmalı ve `APNS_SANDBOX` ile uyumlu olmalıdır.

## Güncelleme

```bash
cd ~/apps/wfm && git pull
cd deploy && docker compose up -d --build
```

## Yedekleme

Veriler Docker volume'larında durur: `wfm_db-data` (veritabanı), `wfm_uploads` (fotoğraf/imza),
`wfm_web-keys` (oturum anahtarları), `wfm_caddy-data` (sertifikalar). Veritabanı yedeği:

```bash
source .env
docker compose exec db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$DB_PASSWORD" \
  -Q "BACKUP DATABASE Wfm TO DISK='/var/opt/mssql/wfm.bak' WITH INIT"
docker compose cp db:/var/opt/mssql/wfm.bak ./wfm-$(date +%F).bak
```

Yedekleri sunucu dışında da saklayın; sunucu kaybolursa volume'lar da gider.

## Güvenlik notları

- Dışarıya yalnızca 22, 80, 443 açıktır (`server-setup.sh` içindeki ufw). SQL Server portu yayınlanmaz.
- `admin@wfm.local` parolası `ADMIN_PASSWORD` ile yalnızca **ilk kurulumda** belirlenir. Sonradan `.env`'i
  değiştirmek mevcut parolayı değiştirmez.
- `DEMO_DATA=true` demo şirketleri ve `Demo123!` parolalı hesapları oluşturur. Sunucu herkese açık olduğu
  için bunu yalnızca sunum süresince kullanın. Kapatmak yeni demo kaydı oluşturmayı durdurur ama mevcut demo
  hesapları silmez. Gerçek kullanıma geçmeden önce veritabanını sıfırlayın
  (`docker compose down -v`, **tüm veriyi siler**) ya da demo şirketleri pasifleştirin.


## Araç Takip (telefon konumuyla)

API ve Web birlikte yeniden derlenir. API açılırken `VehicleTracking` migration'ı uygulanır;
rol seed'i `VehicleObserver` (Araç Takip İzleyicisi) rolünü ekler. Önce veritabanı yedeği alın.
Mevcut kullanıcılar varsayılan olarak araç takibine **kapalıdır** ve hiçbir denetçi izni kendiliğinden açılmaz.

1. Kullanıcılar ve ekipler → saha çalışanını düzenle → **Araç takibinde göster** → plaka ve isteğe bağlı araç adı.
2. Çalışan telefonda mesaiye başlar. Yeni bir mobil paket gerekmez; mevcut konum gönderimi kullanılır.
   Eski Android/iOS uygulamasının arka plan konum izinlerinin çalıştığı gerçek telefonda doğrulanmalıdır.
3. `/vehicles` sayfası 15 saniyede güncellenir. Araç seçilince Türkiye saatine göre günlük rota, en az 5 dk
   duraklamalar ve yaklaşık GPS mesafesi gösterilir. Beş dakikayı aşan veri boşlukları ve imkânsız
   sıçramalar çizgiyle birleştirilmez. Hız m/sn'den km/sa'ye çevrilir; veri yokluğu duruş sayılmaz.
4. Dış denetçi için **Araç Takip İzleyicisi** rolünde ayrı hesap oluşturun. Araç Takip → **Denetçi erişimleri**
   üzerinden erişimi açın; tüm araçlar (gelecekte eklenenler dahil) veya seçili araçları ve bitiş zamanını belirleyin.
   Yeni erişimde arayüz bir hafta önerir. Süresiz erişim ayrıca seçilir. Araç seçilmezse hiçbir araç gösterilmez.
5. Denetçi web'de girişten sonra yalnızca `/vehicles` ekranına yönlendirilir. Kapsam ve süre her veri
   isteğinde sunucuda kontrol edilir; diğer API'ler ve SignalR hub'ları rol sınırı tarafından reddedilir.
   Hesap pasifleştirme ve rol değişiklikleri yeni HTTP isteklerinde tekrar doğrulanır.

Konum kayıtları yalnızca özellik açıldıktan sonra ve mesai aralığı içindeyken araç kaydı olarak işaretlenir.
Önceki personel konumları geriye dönük açılmaz. Takibi kapatmak veya plakayı değiştirmek mevcut araç
geçmişinin görünmesini sonlandırır; bu ilk sürümde araç kimliği kullanıcı hesabına bağlıdır, sürücüler
arasında kalıcı araç aktarımı yoktur. Konum saklama süresi mevcut şirket ayarı ve temizleme servisiyle aynıdır.

Konumlar telefondan gelir; telefon araçtan ayrılırsa gerçek araç konumunu göstermez. Kontak/yakıt/OBD
ölçümü, 24 saat mesai dışı takip, otomatik hız/bölge bildirimleri ve dışa aktarım bu ilk sürümde yoktur.
Harita yenileme aralığı telefonun konum gönderme aralığı değildir. Canlı mesafe/rota sonuçları gerçek
cihazla mesai başlatma, bitirme, çevrimdışı kayıtların sonradan gönderimi ve izin iptali senaryolarında
sahada doğrulanmadan kesin ölçüm olarak kullanılmamalıdır.

Doğrulama: izole InMemory veritabanı üstünde gerçek HTTP uç noktaları için erişim/şirket izolasyonu,
iptal/süre bitimi, mesai kaydı; SQL Server sorgu çevirisi; rota boşlukları ve mevcut UI/fotoğraf birim testleri.
InMemory testleri SQL Server üstünde migration uygulanmasının yerine geçmez. Canlı sistem değiştirilmez.
