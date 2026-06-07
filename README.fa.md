# سیستم چت صوتی کم‌تأخیر — Unity + .NET

یک سیستم چت صوتی حرفه‌ای و بهینه برای پلتفرم‌های Windows و Android با قابلیت‌های زیر:
- **رمزگذاری صدا با Opus** (از طریق DLL/SO بومی)
- **ضبط صدای بومی** (WASAPI در ویندوز، `AudioRecord` از طریق JNI در اندروید)
- **سرور مبتنی بر UDP با .NET** و مسیریابی بسته با استفاده از تمام هسته‌های CPU
- **پنل آمار و تشخیص تأخیر در لحظه** در یونیتی
- **شبیه‌ساز MockClient** برای آزمون مقیاس‌پذیری

---

## معماری سیستم

```
[کلاینت Unity]                       [سرور .NET]
 IAudioRecorder (بومی)
   ├── WasapiRecorder (ویندوز)         UdpVoiceServer
   └── AndroidRecorderBridge             ├── حلقه دریافت (۱ رشته)
         └── AndroidVoiceRecorder        └── استخر کارگر (CPU × رشته)
                                               └── PacketRouter
 OpusEncoder (PCM → بایت)                       ├── Handshake → تأیید RTT
 UdpVoiceClient (ارسال/دریافت UDP)              ├── RoomJoin → تأیید
 OpusDecoder (بایت → PCM)                       └── Audio → پخش گروهی
 AudioPlaybackManager                      RoomManager / VoiceRoom
 DiagnosticsCollector                  SessionPruningService
 DiagnosticsUI (کلید F1)
```

### فرمت بسته شبکه (۲۸ بایت هدر + محتوای Opus)

| آفست | اندازه | فیلد |
|------|--------|-------|
| ۰ | ۴ | نوع بسته (۱=Handshake، ۲=RoomJoin، ۳=Audio) |
| ۴ | ۴ | شناسه اتاق |
| ۸ | ۴ | شناسه کلاینت |
| ۱۲ | ۴ | شماره ترتیب |
| ۱۶ | ۸ | زمان ارسال (میلی‌ثانیه) |
| ۲۴ | ۴ | طول محتوا |
| ۲۸+ | N | محتوای صوتی کدشده با Opus |

---

## پروژه‌ها

| پروژه | توضیح |
|-------|-------|
| `Server/` | سرور UDP ناهمزمان .NET 8 |
| `Shared/` | هدر بسته و سریال‌سازی (مشترک بین سرور و MockClient) |
| `MockClient/` | شبیه‌ساز بار چند-کلاینتی با اندازه‌گیری تأخیر |
| `Tests/` | تست‌های یکپارچه‌سازی xUnit |
| `Client/Assets/Scripts/` | اسکریپت‌های C# یونیتی (صدا، کدک، شبکه، آمار) |
| `Client/Assets/Plugins/Windows/` | کدهای DLL بومی (WASAPI + پوشش Opus) |
| `Client/Assets/Plugins/Android/` | پلاگین Java برای ضبط صدا |

---

## شروع سریع

### ۱. اجرای سرور
```powershell
dotnet run --project Server -c Release -- --port 50005
```

### ۲. اجرای شبیه‌ساز بار
```powershell
# ۱۰ کلاینت، ۲ اتاق، ۶۰ ثانیه
dotnet run --project MockClient -c Release -- --server 127.0.0.1 --port 50005 --clients 10 --rooms 2 --duration 60
```

### ۳. اجرای کلاینت تست لوپ‌بک (پخش اکو)
کلاینت لوپ‌بک وارد یک اتاق خاص می‌شود، بسته‌های صوتی کاربران دیگر را دریافت می‌کند و فوراً آن‌ها را به همان اتاق بازمی‌فرستد. این کار به شما امکان می‌دهد فرآیند ضبط صدا از میکروفون و پخش بلندگو را به صورت مستقیم بررسی کنید.

این کلاینت از پروتکل‌های UDP، TCP و WebSocket پشتیبانی می‌کند (پورت‌های پیش‌فرض به‌صورت خودکار در صورت عدم تعیین رزولوش خواهند شد):
```powershell
# اجرای لوپ‌بک UDP در اتاق ۱۰۰
dotnet run --project MockClient -c Release -- --loopback --protocol udp --room 100 --clientid 9999

# اجرای لوپ‌بک TCP در اتاق ۱۰۰
dotnet run --project MockClient -c Release -- --loopback --protocol tcp --room 100 --clientid 9999

# اجرای لوپ‌بک WebSocket در اتاق ۱۰۰
dotnet run --project MockClient -c Release -- --loopback --protocol ws --room 100 --clientid 9999
```

### ۳. ساخت پلاگین‌های بومی

#### DLL ضبط صدای ویندوز (`VoiceCapture.dll`)
```powershell
cd Client/Assets/Plugins/Windows
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
# فایل VoiceCapture.dll را به مسیر Client/Assets/Plugins/Windows/ کپی کنید
```

#### پوشش Opus برای یونیتی (`UnityOpus.dll` / `libunityopus.so`)

**روش اول — vcpkg (پیشنهادی):**
```powershell
cmake -B build-opus -DUSE_VCPKG=ON -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake" OpusCMakeLists.txt
cmake --build build-opus --config Release
```

**روش دوم — از کد منبع Opus:**
```powershell
# دانلود https://opus-codec.org/release/stable/opus-1.5.2.tar.gz و استخراج
cmake -B build-opus -DOPUS_SOURCE_DIR=./opus-1.5.2 OpusCMakeLists.txt
cmake --build build-opus --config Release
```

#### کتابخانه `.so` برای اندروید (کامپایل متقاطع با NDK)
```bash
cmake -B build-android -DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake \
      -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-26 \
      -DOPUS_SOURCE_DIR=./opus-1.5.2 OpusCMakeLists.txt
cmake --build build-android
# فایل libunityopus.so را به Client/Assets/Plugins/Android/libs/arm64-v8a/ منتقل کنید
```

---

## راه‌اندازی در یونیتی

1. پوشه `Client/` را به عنوان پروژه یونیتی باز کنید (یونیتی ۲۰۲۲.۳ یا بالاتر توصیه می‌شود)
2. یک GameObject دائمی با نام `VoiceSystem` بسازید
3. این MonoBehaviourها را اضافه کنید:
   - `UnityMainThreadDispatcher`
   - `VoiceNetworkManager` (تنظیمات: `ServerHost`، `ServerPort`، `ClientId`، `RoomId`)
   - `DiagnosticsUI` (مرجع `VoiceNetworkManager` را تنظیم کنید)
4. در حین بازی، کلید **F1** را برای نمایش یا مخفی کردن پنل آمار فشار دهید
5. در **Player Settings → Android**، مجوز `INTERNET` را فعال کنید

---

## پنل تشخیص (کلید F1)

| شاخص | توضیح | کدگذاری رنگ |
|------|-------|-------------|
| **State** | وضعیت اتصال | سبز = InRoom |
| **RTT** | زمان رفت و برگشت بسته | سبز <50ms / زرد <150ms / قرمز >150ms |
| **Jitter** | نوسان زمان دریافت بسته‌ها | سبز <20ms / زرد <50ms / قرمز >50ms |
| **Packet Loss** | درصد بسته‌های گم‌شده | سبز <1% / زرد <5% / قرمز >5% |
| **Encode Avg** | میانگین زمان رمزگذاری Opus | — |
| **TX/RX** | پهنای باند ارسال/دریافت (kbps) | آبی |
| **Mic** | نوار سطح صدای میکروفون (RMS) | — |

---

## گزینه‌های MockClient

```
  --server   / -s      آدرس IP یا نام سرور                  [پیش‌فرض: 127.0.0.1]
  --port     / -p      پورت سرور (برای UDP/TCP/WS)          [پیش‌فرض: 50005]
  --clients  / -c      تعداد کلاینت‌های شبیه‌سازی‌شده        [پیش‌فرض: 10]
  --rooms    / -r      تعداد اتاق‌ها                         [پیش‌فرض: 2]
  --duration / -d      مدت زمان آزمون (ثانیه)                [پیش‌فرض: 60]
  --packet-interval    فاصله ارسال بسته‌های صوتی (ms)       [پیش‌فرض: 20]
  --payload-size       اندازه محتوای بسته (بایت)             [پیش‌فرض: 120]
  --stagger            تأخیر بین اتصال کلاینت‌ها (ms)        [پیش‌فرض: 10]
  --loopback           فعال‌سازی حالت کلاینت لوپ‌بک (اکو)
  --protocol / -proto  پروتکل اتصال: udp, tcp, ws          [پیش‌فرض: udp]
  --room               شناسه اتاق برای لوپ‌بک               [پیش‌فرض: 100]
  --clientid           شناسه کلاینت لوپ‌بک                   [پیش‌فرض: 9999]
```

---

## بودجه زمانی کانال صوتی (هدف: زیر ۱۵۰ms)

| مرحله | هدف |
|-------|-----|
| ضبط صدا (WASAPI / AudioRecord) | ~۵ تا ۱۰ میلی‌ثانیه |
| رمزگذاری Opus (فریم ۲۰ms) | ~۱ تا ۲ میلی‌ثانیه |
| ارسال UDP | کمتر از ۱ میلی‌ثانیه |
| انتقال شبکه (LAN) | کمتر از ۵ میلی‌ثانیه |
| پخش گروهی سرور | کمتر از ۱ میلی‌ثانیه |
| دریافت UDP | کمتر از ۱ میلی‌ثانیه |
| رمزگشایی Opus | ~۱ میلی‌ثانیه |
| بافر پخش صدا | ~۲۰ تا ۴۰ میلی‌ثانیه |
| **جمع (در شبکه محلی)** | **~۳۵ تا ۶۰ میلی‌ثانیه** |

---

## اسکریپت ساخت (`build.ps1`)

```powershell
# ساخت پروژه‌های .NET
.\build.ps1

# ساخت شامل DLLهای بومی
.\build.ps1 -BuildNative

# ساخت + اجرای تست‌های xUnit
.\build.ps1 -RunTests

# ساخت + راه‌اندازی سرور
.\build.ps1 -LaunchServer

# ساخت + راه‌اندازی سرور + آزمون بار با MockClient
.\build.ps1 -RunMock -LaunchServer -Clients 20 -Rooms 4 -Duration 120
```

---

## مجوز
MIT
