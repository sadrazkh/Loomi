# Loomi — فضای شخصی ساخت و ویرایش تصویر

وب‌اپ شخصی ASP.NET Core 10 / C#، Vue 3، EF Core و SQLite، SignalR و Playwright for .NET. ظاهر بر اساس فایل مرجع Loomi/Industry پیاده‌سازی شده است: سطوح تیره، خطوط باریک، گوشه‌های چهارگوش، رنگ آبی فولادی، فونت Barlow و Vazirmatn و پشتیبانی کامل فارسی/انگلیسی و RTL/LTR. فونت‌ها، آیکن‌ها و کتابخانه‌های رابط از خود سرور ارائه می‌شوند.

این برنامه **OpenAI API و endpoint خصوصی ChatGPT را فراخوانی نمی‌کند**. یک Chromium واقعی با پروفایل پایدار باز می‌شود و کاربر خودش از طریق noVNC وارد ChatGPT می‌شود. کلید ورود Loomi مستقل از حساب ChatGPT است؛ هیچ رمز ChatGPT در مدل‌ها یا دیتابیس برنامه ذخیره نمی‌شود. کوکی‌های نشست در پروفایل خصوصی Chromium باقی می‌مانند.

## وضعیت و محدوده

- پروژه، صف پایدار، Generate / Edit / Branch، ذخیره تصویر، تاریخچه و درخت والد/فرزند، گالری، اتصال/بازنشانی مرورگر، امنیت برنامه و پیکربندی استقرار پیاده‌سازی شده‌اند.
- کد automation بر DOM قابل‌تنظیم متکی است. selectorهای پیش‌فرض نقطه شروع هستند و روی حساب واقعی شما تأیید نشده‌اند؛ تغییر UI، زبان سایت، محدودیت حساب، چالش ورود و پاسخ صرفاً متنی ممکن است عملیات را متوقف کند. هیچ دورزدن CAPTCHA یا ورود خودکار با رمز پیاده‌سازی نشده است.
- آزمون‌های مرورگر از صفحه کنترل‌شده با Chromium واقعی استفاده می‌کنند؛ موفقیت آن‌ها به معنی تأیید تولید تصویر در سایت زنده ChatGPT نیست.
- Docker و noVNC باید روی میزبان Linux دارای Docker آزموده شوند. Docker در محیط ساخت فعلی نصب نبود.
- نسخه اول تک‌کاربره، تک‌نمونه و دارای یک مرورگر مشترک است. چند replica با یک profile/database اجرا نکنید.

## ساختار

```text
src/Loomi/
  BrowserAutomation/     Chromium launcher, serialized session service, selectors
  Controllers/           Session, ChatGPT connection, projects, generations
  Data/Migrations/       SQLite context, initial migration and model snapshot
  DTOs/                  Validated requests and public responses
  Models/                Project / Generation / operation states
  Repositories/          Project queries
  Services/              Durable queue, generation orchestration, storage, SignalR
  Client/components/     Vue components, translations and Industry styles
  wwwroot/dist/          Generated local frontend bundle
  Storage/               Runtime-only private data (gitignored)
tests/Loomi.Tests/        API/security/storage/lineage and browser-fixture tests
scripts/                 Local run and UI smoke checks
deploy/                  Xvfb/noVNC entrypoint and Nginx example
docs/ARCHITECTURE.md      Architecture and lifecycle decisions
design-reference/        Original supplied design reference, not executable app code
```

## اجرای محلی Windows

پیش‌نیاز: .NET SDK 10، Node.js 22 و PowerShell 7. دستورات را از ریشه مخزن اجرا کنید.

```powershell
dotnet restore --configfile NuGet.Config
npm ci --prefix src/Loomi/Client
npm run build --prefix src/Loomi/Client
pwsh -File scripts/dev.ps1
```

اسکریپت خودش build می‌کند و Chromium مطابق نسخه Playwright را نصب می‌کند؛ اگر از قبل نصب باشد این مرحله سریع رد می‌شود. برنامه در [localhost:5080](http://localhost:5080) در دسترس است و Migration هنگام راه‌اندازی اعمال می‌شود. کلید ورود لازم نیست: محیط Development کلید داخلی زیر را برمی‌دارد و در لاگ چاپ می‌کند. اگر کلید خودتان را می‌خواهید، پیش از اجرا `Security__AccessKey` را تنظیم کنید.

### کلید پیش‌فرض حالت توسعه

برای اینکه اجرای ساده (`dotnet run`، F5 در Visual Studio) بدون هیچ تنظیمی کار کند، محیط Development یک کلید داخلی دارد:

```text
loomi-development-access-key-change-me
```

این کلید در `appsettings.Development.json` است و اگر آن مقدار هم نباشد، خود برنامه در Development همین را جایگزین می‌کند و در استارتاپ یک هشدار لاگ می‌کند. در همین حالت، فرم ورود کلید را از پیش پر می‌کند و کنار آن نشان می‌دهد، پس فقط کافی است **Unlock** را بزنید.

محافظ‌ها: خارج از Development استفاده از این کلید **رد می‌شود** و برنامه با خطای صریح بالا نمی‌آید؛ اگر کلید تنظیم‌شده چیزی جز همین کلید داخلی باشد، `GET /api/session` آن را فاش نمی‌کند. Docker با `ASPNETCORE_ENVIRONMENT=Production` اجرا می‌شود و همچنان `LOOMI_ACCESS_KEY` واقعی می‌خواهد. برای هر اجرای غیرمحلی `Security__AccessKey` خودتان را تنظیم کنید.

در اجرای مستقیم Windows، **Connect ChatGPT** یک پنجره Chromium محلی باز می‌کند؛ داخل همان پنجره وارد شوید. noVNC به‌صورت خودکار فقط در Docker آماده می‌شود. برنامه بررسی می‌کند که چیزی روی پورت noVNC پاسخ می‌دهد یا نه؛ اگر ندهد `desktopUrl` برابر `null` برمی‌گردد و به‌جای دکمه‌ی «مرورگر امن» پیامی نمایش داده می‌شود که مرورگر روی همین دسکتاپ باز است. (پیش از این، آن دکمه ۵۰۲ می‌داد.) برای آزمایش کامل مسیر مرورگر راه‌دور از Docker/Linux استفاده کنید.

برای اجرای مستقیم Linux با دسکتاپ و متغیر `DISPLAY`، وابستگی‌های سیستمی Chromium را نصب و `bash scripts/dev.sh` را اجرا کنید؛ این اسکریپت هم اگر `pwsh` موجود باشد خود مرورگر را نصب می‌کند. روی Linux بدون دسکتاپ، روش Docker زیر توصیه می‌شود.

## استقرار Linux / Docker

```bash
cp .env.example .env
openssl rand -hex 32
# مقدار خروجی را در LOOMI_ACCESS_KEY در فایل .env قرار دهید.
# LOOMI_HOSTS و LOOMI_PROXY_IP را متناسب با سرور تنظیم کنید.
chmod 600 .env
docker compose up -d --build
docker compose ps
docker compose logs --tail=100 loomi
```

کانتینر .NET 10، Chromium مطابق نسخه Playwright، Xvfb، fluxbox، x11vnc، websockify و noVNC را شامل می‌شود. Node فقط برای build رابط استفاده می‌شود. فرایندهای runtime با کاربر غیر root اجرا می‌شوند. Dockerfile فعلی برای Linux x86_64 نوشته شده است.

پورت برنامه فقط روی `127.0.0.1:5080` میزبان منتشر می‌شود. `deploy/nginx.conf` نمونه reverse proxy با HTTPS و WebSocket است. دامنه و مسیر گواهی را عوض کنید. **Production به HTTPS نیاز دارد** چون کوکی نشست Secure است. برای آزمایش صرفاً محلی HTTP، محیط را موقتاً Development کنید؛ این حالت را روی اینترنت قرار ندهید.

`LOOMI_PROXY_IP` باید IP واقعی proxy باشد که برنامه می‌بیند؛ مقدار نمونه را بررسی کنید، خصوصاً چون gateway شبکه Compose ممکن است با bridge پیش‌فرض متفاوت باشد. فقط proxy مورد اعتماد باید بتواند `X-Forwarded-Proto` و `X-Forwarded-For` معتبر ارسال کند. `AllowedHosts` را به دامنه واقعی محدود کنید.

پورت‌های 5900 و 6080 فقط روی loopback **داخل کانتینر** فعال‌اند و نباید منتشر شوند. مرورگر از مسیر احراز هویت‌شده `/desktop/` در همان دامنه ارائه می‌شود؛ WebSocket آن هم به نشست Loomi و Origin همان سایت نیاز دارد. Cookie برنامه هنگام proxy شدن به noVNC حذف می‌شود.

Volumeها:

| Volume | مسیر | محتوا |
|---|---|---|
| database | `/data` | `loomi.db`، فایل‌های WAL و کلیدهای Data Protection |
| browser-profiles | `/data/profiles` | پروفایل Chromium و نشست ChatGPT |
| images | `/data/images` | `projects/{projectId}/{generationId}/image.png|jpg|webp` |

کل volumeها داده خصوصی هستند. قبل از ارتقا backup بگیرید؛ برای backup سازگار، سرویس را متوقف کنید یا از SQLite backup API استفاده کنید. فایل DB را هنگام فعال بودن WAL به‌تنهایی کپی نکنید. برای backup کامل، هر سه volume را نگه دارید. حذف volumeها اطلاعات را پاک می‌کند.

## اولین استفاده

1. با کلید دسترسی سرور وارد Loomi شوید.
2. **Connect ChatGPT** و سپس **Open secure browser** را بزنید.
3. شخصاً داخل Chromium وارد ChatGPT شوید. همه مراحل احراز هویت در همان مرورگر انجام می‌شود.
4. به Loomi برگردید؛ وضعیت هر ۵ ثانیه بررسی می‌شود. در صورت نیاز **Check connection** را بزنید.
5. **New image project** را بزنید و عنوان بدهید. برنامه صفحه یک چت تازه را باز می‌کند. لینک دائمی چت پس از اولین ارسال توسط ChatGPT ایجاد می‌شود.
6. در prompt صریحاً درخواست ساخت تصویر کنید. عملیات در صف ذخیره می‌شود و وضعیت با SignalR نمایش داده می‌شود؛ polling در قطع ارتباط وضعیت را بازیابی می‌کند.
7. **Edit** تصویر والد را دوباره به همان گفتگوی والد پیوست می‌کند و درخواست تغییر را می‌فرستد. این کار ویرایش نسخه‌های قدیمی را مشخص می‌کند؛ زمینه سایر پیام‌های همان چت همچنان وجود دارد.
8. **Branch** چت تازه می‌سازد، تصویر والد را بارگذاری و درخواست را ارسال می‌کند. درخت، رابطه دقیق والد/فرزند را نشان می‌دهد.

هر Generate مستقل، یک چت تازه می‌سازد. برای ادامه همان چت از Edit استفاده کنید. لینک هر generation مستقل ذخیره می‌شود؛ Project آخرین گفتگوی موفق را نگه می‌دارد. حذف Project فقط داده محلی را حذف می‌کند، نه چت‌های حساب ChatGPT.

هنگام کار صف، در مرورگر راه‌دور تایپ یا جابه‌جا نشوید؛ همان صفحه تحت کنترل automation است. Reset و تغییر صفحه از API در هنگام استفاده مرورگر رد می‌شوند. Reset پروفایل محلی را پاک می‌کند، اما جایگزین ابطال نشست‌های دیگر از تنظیمات حساب ChatGPT نیست.

## پیکربندی

فایل `src/Loomi/appsettings.json` پیش‌فرض‌ها را دارد؛ environment variables با `__` قابل override هستند.

| متغیر | پیش‌فرض / توضیح |
|---|---|
| `Security__AccessKey` | اجباری، کلید تصادفی حداقل ۳۲ نویسه؛ فقط در Development پیش‌فرض دارد |
| `Security__KnownProxies__0` | IP صریح reverse proxy مورد اعتماد |
| `AllowedHosts` | `localhost;127.0.0.1`؛ در سرور دامنه را اضافه کنید |
| `Storage__Root` | `Storage` در اجرای محلی، `/data` در Docker |
| `Browser__Headless` | `false`؛ برای ورود دستی باید false بماند |
| `Browser__ExecutablePath` | اختیاری، مسیر Chromium دلخواه؛ معمولاً خالی بماند. اگر مقدار بگیرد، fallback کانال‌ها غیرفعال می‌شود |
| `Browser__Channels__0` | `chrome` و سپس `msedge`؛ مرورگر نصب‌شده‌ای که اگر build خود Playwright روی میزبان بالا نیاید جایگزین می‌شود. برای غیرفعال‌کردن، آرایه را خالی بگذارید |
| `Browser__IgnoreDefaultArgs__0` | `--enable-automation`؛ این سوییچ باعث می‌شود سایت ورود دستی خودِ شما را ربات تشخیص دهد |
| `Browser__Args__0` | `--disable-dev-shm-usage` و `--disable-blink-features=AutomationControlled` |
| `Browser__DesktopPort` | `6080`؛ پورت loopback سرویس noVNC. برنامه در دسترس بودنش را بررسی می‌کند |
| `Browser__NavigationTimeoutMs` | 60000 |
| `Browser__GenerationTimeoutSeconds` | 600 |
| `Browser__StableSeconds` | 8؛ پایداری تصویر پس از پایان دکمه Stop |
| `Browser__Selectors__Composer` و سایر selectorها | مطابق بخش بعد |

کلید دسترسی در کد و git ذخیره نمی‌شود. تغییر آن ورودهای جدید را عوض می‌کند؛ کوکی‌های از قبل صادرشده تا پایان اعتبار باقی می‌مانند. برای ابطال فوری همه نشست‌های Loomi، سرویس را متوقف کنید، کلید را عوض کنید و کلیدهای Data Protection در `/data/keys` را پس از backup پاک کنید.

## نگهداری selectorها و محدودیت automation

مرورگر به این ترتیب انتخاب می‌شود: اول build خود Playwright، و اگر روی آن میزبان بالا نیامد به‌ترتیب `Browser:Channels` (پیش‌فرض `chrome` سپس `msedge`). هر تلاش ناموفق یک warning لاگ می‌کند و اگر همه شکست بخورند خطای اولین تلاش بالا می‌آید. روی بعضی نصب‌های ویندوز، `chrome.exe` مربوط به Chrome for Testing با خطای «side-by-side configuration is incorrect» اجرا نمی‌شود (رویداد SideBySide با کد ۳۳ در Application event log) در حالی که headless shell سالم کار می‌کند؛ همین fallback آن حالت را پوشش می‌دهد. اگر مرورگر مشخصی می‌خواهید، `Browser__ExecutablePath` را تنظیم کنید.

تمام selectorها در `BrowserAutomation/BrowserOptions.cs` و بخش `Browser:Selectors` پیکربندی متمرکز هستند. برای تنظیم با نسخه سایت خود، DOM قابل‌مشاهده را در Chromium بررسی کنید. هیچ selectorی از API داخلی سایت ساخته نمی‌شود.

| Selector | نقش |
|---|---|
| Composer | کادر متن قابل‌ویرایش |
| LoggedIn / LoggedOut | کنترل حساب واردشده و دکمه ورود؛ وجود composer به‌تنهایی معیار ورود نیست |
| Challenge | صفحه تأیید انسان بودن؛ پیش از بقیه بررسی می‌شود تا با «نیاز به ورود» اشتباه گرفته نشود |
| Send / Stop | ارسال و وضعیت تولید پاسخ |
| Assistant | ظرف پیام‌های دستیار، برای جداکردن پاسخ جدید از تاریخچه |
| GeneratedImage | تصویر تولیدشده داخل آخرین پیام دستیار |
| FileInput / AttachmentMenu | بارگذاری تصویر والد |
| UploadReady / UploadBusy | حضور پیوست و پایان بارگذاری |

تشخیص تصویر: پاسخ دستیار باید جدید باشد، تصویر حداقل ۲۵۶×۲۵۶ و کامل باشد، دکمه توقف مخفی شود و منبع تصویر برای مدت تعیین‌شده ثابت بماند. سپس فقط همان منبع تصویر نمایش‌داده‌شده در context مرورگر خوانده می‌شود. PNG/JPEG/WebP تا ۴۰ مگابایت پذیرفته می‌شوند. تصاویر پروفایل، thumbnailهای کوچک و SVG پذیرفته نمی‌شوند. اگر ChatGPT چند تصویر تولید کند، آخرین تصویر مطابق selector ذخیره می‌شود. تصویر ذخیره‌شده نسخه‌ای است که UI در عنصر تصویر ارائه می‌کند؛ الزاماً فایل «original download» با بیشترین وضوح نیست. اگر سایت منبع تصویر را با CORS غیرقابل‌خواندن کند، دریافت شکست می‌خورد و adapter باید با جریان رسمی دانلود UI به‌روزرسانی شود.

Navigation حداکثر سه بار تلاش می‌کند. **ارسال prompt دوباره تلاش نمی‌شود**؛ بعد از timeout ممکن است سایت درخواست را پذیرفته باشد. عملیات ناتمام پس از restart با `Interrupted` شکست می‌خورد و خودکار ارسال مجدد نمی‌شود؛ queuedها باقی می‌مانند. خطاها در DB با کد امن ذخیره و در UI ترجمه می‌شوند. لاگ‌ها متن exception مرورگر، prompt، کوکی یا token را چاپ نمی‌کنند.

`GenerationTimeout` معمولاً به selector قدیمی، پاسخ بدون تصویر، محدودیت حساب یا طولانی‌شدن تولید مربوط است. `LoginRequired` نیاز به ورود دستی یا اصلاح selector حساب دارد. `VerificationRequired` یعنی سایت صفحه تأیید انسان بودن را نشان می‌دهد؛ فقط خودتان می‌توانید آن را در همان پنجره مرورگر رد کنید و برنامه هیچ تلاشی برای دورزدنش نمی‌کند. `AutomationFailed` نیازمند بررسی مرورگر و تنظیمات DOM است. قبل از ارسال مجدد، چت را ببینید.

## API و امنیت

`GET /api/session` وضعیت ورود و antiforgery token را می‌دهد. برای هر POST/DELETE در `/api` باید cookie و هدر `X-CSRF-TOKEN` ارسال شوند. پس از login توکن را دوباره بگیرید. Login به ۶ تلاش در دقیقه برای هر IP محدود است. SignalR و تصاویر و noVNC به همان cookie وابسته‌اند. پوشه Storage هیچ static-file mapping ندارد.

| Method | Route | نتیجه |
|---|---|---|
| GET | `/api/session` | وضعیت ورود Loomi و CSRF token |
| POST | `/api/session/login` | `{ "accessKey": "..." }` |
| POST | `/api/session/logout` | خروج از Loomi |
| POST | `/api/auth/connect` | ایجاد/بازیابی Chromium و desktop URL |
| GET | `/api/auth/status` | Connected / LoginRequired / Disconnected و Busy |
| POST | `/api/auth/reset` | پاک‌کردن پروفایل محلی |
| GET / POST | `/api/projects` | فهرست / ساخت با `{ "title": "..." }` |
| GET / DELETE | `/api/projects/{id}` | جزئیات / حذف پروژه بدون عملیات فعال |
| POST | `/api/projects/{id}/generate` | صف‌کردن با `{ "prompt": "..." }` و پاسخ 202 |
| GET | `/api/projects/{id}/generations` | تاریخچه |
| GET | `/api/generations/{id}` | نتیجه یا وضعیت |
| POST | `/api/generations/{id}/edit` | `{ "prompt": "..." }` |
| POST | `/api/generations/{id}/branch` | `{ "prompt": "..." }` |
| GET | `/api/generations/{id}/image` | فایل خصوصی تصویر |
| SignalR | `/hubs/status` | رویداد `GenerationUpdated` با GenerationDto |
| GET | `/health` | سلامت فرایند HTTP، نه تضمین اتصال ChatGPT |

حداکثر عنوان ۱۶۰ نویسه، prompt برابر ۱۲۰۰۰ نویسه، و صف ۲۵ کار ناتمام است. تنها generation تکمیل‌شده با تصویر می‌تواند والد باشد. `IImageStorage` نقطه توسعه برای S3/R2 است؛ filesystem فعلی مسیرهای DB را به مسیر امن داخل root تبدیل می‌کند. برای storage ابری، قرارداد خواندن فایل/پیوست هم باید با دریافت موقت فایل تطبیق داده شود.

## آزمون و توسعه

```powershell
dotnet test
dotnet tool restore --configfile NuGet.Config
dotnet ef migrations has-pending-model-changes --project src/Loomi
npm run build --prefix src/Loomi/Client
```

آزمون Chromium به نصب مرورگر با اسکریپت Playwright نیاز دارد. آزمون fixture تمام درخواست‌های سایت را پاسخ محلی می‌دهد و به حساب ChatGPT متصل نمی‌شود. برای smoke test رابط، سرور Development را روی پورت 5080 اجرا و کلید تست همان سرور را در `LOOMI_TEST_KEY` قرار دهید:

```powershell
node scripts/ui-smoke.cjs
```

تصاویر بررسی UI در `test-results` ذخیره می‌شوند و وارد git نمی‌شوند. برای تغییر UI، `npm run build` را تکرار کنید. Vite فقط ابزار bundle است؛ برای اجرای production سرور Node لازم نیست. `npm run dev` به backend محلی 5080 proxy می‌کند، اما جریان کامل cookie/SignalR/noVNC را با نسخه bundle و یک origin آزمون کنید.

Migration جدید: `dotnet ef migrations add Name --project src/Loomi --output-dir Data/Migrations`. Factory زمان طراحی بدون کلید ورود کار می‌کند. پیش از استقرار، وابستگی‌ها و backup را بررسی کنید.

## منابع پیاده‌سازی

- [Playwright persistent browser contexts](https://playwright.dev/dotnet/docs/api/class-browsertype#browser-type-launch-persistent-context)
- [Playwright Docker guidance](https://playwright.dev/dotnet/docs/docker)
- [ASP.NET Core SignalR authentication](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0)
- [ASP.NET Core antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0)

## English quick start

Restore .NET packages, run `npm ci` and `npm run build` in `src/Loomi/Client`, then run `scripts/dev.ps1` or `scripts/dev.sh` — each builds the solution and installs the Chromium build that matches the referenced Playwright version. Open localhost:5080, unlock Loomi, connect Chromium, log into ChatGPT manually, then create a project and submit an image prompt. Set `Security__AccessKey` to a random secret of at least 32 characters to use your own workspace key.

A plain `dotnet run` in the Development environment needs no configuration: it falls back to the built-in key `loomi-development-access-key-change-me`, logs a startup warning, and prefills it on the login form. Startup refuses that key in any other environment, and `GET /api/session` never reveals a key you configured yourself.

For Linux production, configure `.env`, build with Docker Compose and place the loopback app port behind a trusted HTTPS proxy using the provided Nginx example. Only the authenticated app exposes noVNC. Back up all three private volumes. Live ChatGPT selectors and account-specific image generation require manual acceptance testing; the browser fixture tests do not establish compatibility with the live site.
