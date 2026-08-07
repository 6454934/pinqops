# Türkçe Rehber

**`master`'a merge et → kapalı sunucun kendini güncellesin.** GitHub imajı
bulutta derler; sunucundaki küçük bir self-hosted runner imajı çeker ve
compose projesini yeniden başlatır. Yalnızca dışa bağlantı — açık port yok,
SSH yok, sunucuda git token'ı yok.

## Hızlı kurulum (Web UI ile)

```bash
sudo curl -fsSL -o /usr/local/bin/pinqops-ui \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops-ui
sudo chmod +x /usr/local/bin/pinqops-ui

sudo pinqops-ui install-service
sudo journalctl -u pinqops-ui | grep "setup code"
```

1. `http://<sunucu>:7467` adresini aç, konsoldaki **kurulum kodunu** gir,
   panel şifreni oluştur.
2. Kenar çubuğundaki **GitHub** menüsüne gir (bağlanana kadar kilit ikonu
   taşır). **GitHub ile giriş yap** — github.com'da kısa bir kodu onaylarsın
   (veya token yapıştır).
3. Depolarında ara, birini seç, **Kur**'a bas.

## Kurulum sihirbazı ne yapar?

| Adım | Açıklama |
|---|---|
| Depo bağlanıyor | Seçilen depo dağıtım hedefi olarak kaydedilir |
| Dockerfile | Sadece kontrol edilir — yoksa uyarı verir (uygulama kodunu sihirbaz yazamaz) |
| Deploy workflow | Eksikse `.github/workflows/deploy.yml` depoya commit'lenir |
| Compose projesi | Eksikse deponun imajı için `/opt/pinqops/docker-compose.yml` üretilir |
| Self-hosted runner | İndirilir, **bu depoya** kaydedilir, systemd servisi kurulur. Başka bir depoya kayıtlı eski bir runner varsa önce düzgünce sökülür |
| Doğrulama | Runner'ın GitHub'da gerçekten göründüğü teyit edilir |

Sihirbaz bittiğinde tek yapman gereken: bir PR'ı `master`'a merge etmek.
Gerisi otomatik.

## Dağıtım geçmişi ve geri alma

Her build artık `:latest` yanında değişmez bir `sha-<commit>` etiketi de
gönderir; dağıtım bu etiketi compose dizinindeki `.env` dosyasına sabitler.
**Dağıtımlar** görünümündeki *Dağıtım geçmişi* kartında hangi sürümün ne zaman
yayına alındığını, sağlık kontrolü sonucunu görürsün; başarılı bir satırdaki
**Geri al** düğmesi o sürümü tek tıkla geri getirir (CLI: `pinqops rollback`).
Dağıtım sonrası sağlık kontrolü başarısız olursa dağıtım "failed" kaydedilir ve
bildirim gider — otomatik geri alma yoktur, karar senindir.

## Bildirimler

**Ayarlar → Bildirimler**: dağıtım sonuçları webhook, Slack (Discord/Mattermost
uyumlu) veya Telegram'a gönderilir. Kanal başına **Dene** düğmesiyle test et.
Ayarlar compose projesinin yanındaki `.pinqops/notify.json` dosyasında durur;
runner'daki CLI dağıtımları da aynı ayarları kullanır.

## Uygulama kataloğu

**Uygulamalar** menüsünde ~50 hazır uygulama var (Redis, PostgreSQL,
Grafana, MinIO, n8n, …). **Kur**'a bas, istersen portları değiştir — kurulum
arka planda yürür ve "imaj çekiliyor → başlatılıyor" ilerlemesi sayfa
yenilemeden görünür. Veriler adlandırılmış volume'larda tutulur; **Kaldır**
konteyneri siler, veriyi korur.

Şifre gerektiren uygulamalar artık sabit şifreyle değil, kurulumda üretilen
rastgele şifreyle gelir. Kurulum sonunda gösterilir; sonradan uygulama
satırındaki 🔑 düğmesiyle (maskeli, göster/kopyala) erişirsin. Yeniden
kurulumda aynı şifre kullanılır, mevcut veri bozulmaz.

## Güncelleme

README'den komut kopyalamaya gerek yok — ikili dosya kendini yerinde günceller:

```bash
sudo pinqops update       # pinqops ikilisini en son sürümle değiştirir
sudo pinqops-ui update    # pinqops-ui'yi değiştirir ve servisini yeniden başlatır
```

Her ikisi de en son sürümü indirir, ikiliyi atomik olarak yerine koyar; panel
systemd servisi olarak çalışıyorsa yeni sürümün devralması için servisi yeniden
başlatır. Sürümü `pinqops version` ile doğrula.

## Loglar

Konteyner logları ayrı bir menü değil: **Konteynerler** görünümünde bir
konteynerin *loglar* düğmesine bas — panel aynı sayfada açılır (takip et /
indir / kapat).

## Sık sorunlar

- **Runner GitHub'da görünmüyor:** eski sürümlerde başka depoya kayıtlı
  runner "kurulu" sanılıyordu; artık sihirbaz uyuşmazlığı görür, eskisini
  söker ve doğrusunu kaydeder. Sihirbazı yeniden çalıştırman yeterli.
- **Runner kurulu ama çevrimdışı:** Runner sayfasından **Servisi başlat**,
  ya da `cd /opt/actions-runner && sudo ./svc.sh start`.
- **Runner listelenemiyor (token hatası):** token'da *Administration: read*
  yoksa panel o satırı "durum bilinmiyor" olarak gösterir; dağıtım yine
  çalışır.

Ayrıntılar (İngilizce):
[SETUP](https://github.com/pinqponq/pinqops/blob/master/docs/SETUP.md) ·
[TOKENS](https://github.com/pinqponq/pinqops/blob/master/docs/TOKENS.md) ·
[SECURITY](https://github.com/pinqponq/pinqops/blob/master/SECURITY.md)
