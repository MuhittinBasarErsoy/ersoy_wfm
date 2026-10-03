# WFM – Saha Ekibi Yönetimi

Şirketlerin saha çalışanlarına **generic görevler** (çiçek teslimatı, arıza onarımı, montaj…) atayıp harita üzerinden
takip ettiği, çok kiracılı (multi-tenant) bir iş gücü yönetimi sistemi.

- **Web dashboard** (yönetici / dispeçer): canlı harita, görev yönetimi, görev tipleri, kullanıcılar, raporlar
- **Mobil uygulama** (iOS + Android, .NET MAUI Blazor Hybrid): saha çalışanının görevleri, durum akışı,
  fotoğraf + imza ile teslim kanıtı, offline çalışma, mesai süresince arka planda konum paylaşımı

Web ve mobil **aynı Razor bileşenlerini** (`Wfm.Shared.UI`) kullanır; platforma özel işler (GPS, kamera, offline depo)
arayüzlerle soyutlanmıştır.

## Proje yapısı

| Proje | Açıklama |
|---|---|
| `src/Wfm.Domain` | Entity'ler, görev durum akışı (state machine), roller ve politikalar |
| `src/Wfm.Application` | API sözleşmeleri (DTO), dinamik alan / tamamlama kuralı doğrulaması |
| `src/Wfm.Infrastructure` | EF Core (SQL Server), ASP.NET Identity, kiracı filtresi, migration, demo veri |
| `src/Wfm.Api` | Minimal API + JWT, SignalR hub'ları (`/hubs/tracking`, `/hubs/notifications`) |
| `src/Wfm.Client` | Tip güvenli API istemcisi, token yenileme, SignalR istemcisi (web + mobil ortak) |
| `src/Wfm.Shared.UI` | Ortak Blazor arayüzü (MudBlazor + Leaflet/OpenStreetMap) |
| `src/Wfm.Web` | Blazor Server host (dashboard) |
| `src/Wfm.Mobile` | .NET MAUI Blazor Hybrid (Android, iOS; geliştirme için Windows) |
| `tests/Wfm.Api.Tests` | Entegrasyon testleri (kiracı izolasyonu, roller, durum akışı, kanıt, konum) |

## Çalıştırma

Gereksinimler: .NET 10 SDK, SQL Server LocalDB (Visual Studio ile gelir), mobil için MAUI iş yükleri.

```bash
dotnet run --project src/Wfm.Api --launch-profile http
```

```bash
dotnet run --project src/Wfm.Web --launch-profile http
```

API ilk açılışta veritabanını (`WfmDev`) oluşturur ve demo verisini yükler. Dashboard: http://localhost:5277

Mobil (Android emülatörü API'ye `10.0.2.2:5211` üzerinden erişir):

```bash
dotnet build src/Wfm.Mobile -t:Run -f net10.0-android
```

iOS derlemesi için Visual Studio'dan bir Mac build host'a bağlanmak gerekir. Gerçek cihazda API adresi
`Preferences["api_base_url"]` ile ya da `MauiProgram.ApiBaseUrl()` içinden değiştirilir.

Testler:

```bash
dotnet test
```

Canlıya alma (Ubuntu + Docker + Tailscale Funnel): [deploy/README.md](deploy/README.md)

### Demo hesaplar (parola: `Demo123!`)

| Şirket | E-posta | Rol |
|---|---|---|
| — | admin@wfm.local | Sistem yöneticisi (şirket ekler) |
| Çiçek Dünyası | yonetici@cicek.local | Şirket yöneticisi |
| Çiçek Dünyası | dispecer@cicek.local | Dispeçer |
| Çiçek Dünyası | kurye1@cicek.local, kurye2@cicek.local | Saha çalışanı |
| Çiçek Dünyası | izleyici@cicek.local | İzleyici |
| Hızlı Tamir | yonetici@tamir.local, dispecer@tamir.local | Yönetici, dispeçer |
| Hızlı Tamir | teknisyen1@tamir.local, teknisyen2@tamir.local | Saha çalışanı |

## Roller

| Rol | Yetki |
|---|---|
| SuperAdmin | Şirket (kiracı) oluşturma / pasifleştirme |
| TenantAdmin | Her şey: kullanıcılar, ekipler, görev tipleri, görevler, raporlar |
| Dispatcher | Görev oluşturma, atama, iptal; canlı harita; raporlar |
| FieldWorker | Yalnızca kendisine atanan görevler; durum değiştirme, kanıt yükleme; konum gönderme |
| Viewer | Harita, görevler ve raporları salt okunur görüntüleme |

Politika → rol eşlemesi tek yerde (`Policies.Map`, `src/Wfm.Domain/Roles.cs`) tanımlıdır; API ve UI aynı tabloyu kullanır.

## Generic görev modeli

Her şirket kendi **görev tiplerini** tanımlar (`Görev tipleri` sayfası):

- **Özel alanlar**: metin, sayı, seçim listesi, tarih, onay kutusu, uzun metin, telefon. Her alan ya görev
  oluşturulurken (dispeçer) ya da tamamlanırken (saha çalışanı) doldurulur.
- **Tamamlama kuralları**: müşteri imzası, en az N fotoğraf, not zorunluluğu, görev konumuna maksimum mesafe.

Kurallar `TaskRules` ile hem sunucuda hem cihazda (offline'da da) doğrulanır.

Durum akışı: `Taslak → Atandı → Kabul edildi → Yolda → Yerinde → Tamamlandı` (+ Reddedildi / Yapılamadı / İptal).

## Konum takibi ve bildirimler

- Saha çalışanı **mesaiye başladığında** konum paylaşımı başlar, mesai bitince durur.
  - Android: kalıcı bildirimli Foreground Service (`Platforms/Android/LocationForegroundService.cs`)
  - iOS: `CLLocationManager` arka plan konum modu + significant-change (`Platforms/iOS/IosLocationManager.cs`)
- Konumlar bağlantı varsa SignalR ile anlık, yoksa cihazdaki kuyruğa yazılıp toplu gönderilir.
- Eski konum kayıtları kiracının saklama süresine göre (varsayılan 30 gün) her gün temizlenir.
- **Bildirimler SignalR ile** iletilir (Firebase kullanılmaz). Uygulama açıkken uygulama içi, arka plandayken yerel
  sistem bildirimi gösterilir.

> **Bilinen sınır:** SignalR, uygulama tamamen kapatıldığında (özellikle iOS'ta) bildirim ulaştıramaz. Android'de mesai
> boyunca foreground service bağlantıyı canlı tutar. Uygulama her açıldığında `/api/sync` ile kaçırılan görevler ve
> bildirimler çekilir. Kapalı uygulamaya garantili bildirim gerekirse ileride APNs/FCM eklenebilir.

## Offline çalışma (mobil)

- Görevler ve görev tipleri SQLite'ta önbelleklenir.
- Durum değişiklikleri, fotoğraflar ve imzalar önce cihazda uygulanır, **outbox** kuyruğuna yazılır ve bağlantı
  gelince sırayla gönderilir. Sunucu tekrar gönderimleri idempotent işler (`clientId`, aynı duruma geçiş).
- Sunucu bir işlemi reddederse (ör. görev bu arada iptal edildi) işlem atlanır, kullanıcıya gösterilir ve sunucu
  durumu yeniden çekilir.

## Üretime alırken

- `Jwt:Key`, bağlantı dizesi ve `Cors:Origins` değerlerini ortam değişkenleri / gizli depo ile verin
  (`appsettings.Development.json` yalnızca geliştirme içindir).
- HTTPS kullanın; `AndroidManifest.xml` içindeki `usesCleartextTraffic` ve iOS `NSAllowsLocalNetworking` ayarlarını kaldırın.
- Ekler varsayılan olarak API altında `uploads/` klasörüne yazılır; `IFileStorage` ile Blob depolamaya geçilebilir.
- OpenStreetMap'in genel tile/Nominatim sunucuları yoğun kullanım için uygun değildir; ticari kullanımda kendi
  sunucunuzu veya ücretli bir sağlayıcı kullanın.
