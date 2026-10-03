# Canlıya alma: Ubuntu + Docker + Tailscale Funnel

Sunucuda hiçbir port açılmaz. Web ve API kendi Tailscale düğümleriyle (sidecar container) yayınlanır;
Funnel bunları otomatik HTTPS (`*.ts.net` sertifikası) ile internete açar. Sunucudaki diğer servislerin
Tailscale/Funnel ayarlarına dokunulmaz.

```
İnternet ──HTTPS──▶ Funnel ─▶ wfm-ts-web  (https://wfm.TAILNET.ts.net)     ─▶ wfm-web:8080
                           └▶ wfm-ts-api  (https://wfm-api.TAILNET.ts.net) ─▶ wfm-api:8080 ─▶ wfm-mssql
```

Gereksinimler: Ubuntu x86_64 (SQL Server imajı ARM'de çalışmaz), en az 2 GB boş RAM, Docker.

## 1. Tailscale auth key

Admin panel → **Settings → Keys → Generate auth key**: **Reusable** seçin (iki düğüm aynı anahtarı kullanır).
Tailnet'te MagicDNS ve HTTPS Certificates açık, ACL politikasında Funnel izni verilmiş olmalı.

## 2. Kodu sunucuya alın ve ayarları girin

```bash
git clone <repo-adresi> ~/apps/wfm && cd ~/apps/wfm/deploy
cp .env.example .env && chmod 600 .env
nano .env    # TS_AUTHKEY, TS_TAILNET, DB_PASSWORD, JWT_KEY, ADMIN_PASSWORD
```

Rastgele anahtar üretmek için `openssl rand -base64 48` kullanabilirsiniz.

## 3. Başlatın

```bash
docker compose up -d --build
docker compose logs -f api     # "Now listening on" görünce Ctrl+C
```

İlk açılışta veritabanı ve tablolar otomatik oluşturulur. Tailscale düğümleri ilk açılışta tailnet'e katılır ve
sertifika alır (ilk HTTPS isteği birkaç saniye sürebilir).

Kontrol: `https://wfm.TAILNET.ts.net` giriş sayfasını, `https://wfm-api.TAILNET.ts.net` ise `WFM API`
yazısını göstermeli. Funnel durumu:

```bash
docker exec wfm-ts-web tailscale funnel status
```

## 4. Mobil uygulama

Telefonlar API'ye `https://wfm-api.TAILNET.ts.net` üzerinden bağlanır. Uygulamada
`Preferences["api_base_url"]` bu adres olmalı, ya da yayın derlemesi için `MauiProgram.ApiBaseUrl()` içindeki
varsayılanı değiştirin.

## Güncelleme

```bash
cd ~/apps/wfm && git pull
cd deploy && docker compose up -d --build
```

## Yedekleme

Veriler Docker volume'larında durur: `wfm_db-data` (veritabanı), `wfm_uploads` (fotoğraf/imza),
`wfm_web-keys` (oturum anahtarları), `wfm_ts-*-state` (Tailscale düğüm kimlikleri). Veritabanı yedeği:

```bash
source .env
docker compose exec db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$DB_PASSWORD" \
  -Q "BACKUP DATABASE Wfm TO DISK='/var/opt/mssql/wfm.bak' WITH INIT"
docker compose cp db:/var/opt/mssql/wfm.bak ./wfm-$(date +%F).bak
```

## Güvenlik notları

- `admin@wfm.local` parolası `ADMIN_PASSWORD` ile yalnızca **ilk kurulumda** belirlenir. Sonradan `.env`'i
  değiştirmek mevcut parolayı değiştirmez.
- `DEMO_DATA=true` demo şirketleri ve `Demo123!` parolalı hesapları oluşturur. Sunucu herkese açık olduğu
  için bunu yalnızca sunum süresince kullanın. Kapatmak yeni demo kaydı oluşturmayı durdurur ama mevcut demo
  hesapları silmez. Gerçek kullanıma geçmeden önce veritabanını sıfırlayın
  (`docker compose down -v`, **tüm veriyi siler**) ya da demo şirketleri pasifleştirin.
