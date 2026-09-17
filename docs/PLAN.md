# نقشه راه Loomi: چند ارائه‌دهنده، اعتبار، API، تلگرام

این سند برنامه اجرای مرحله‌به‌مرحله است. هر فاز طوری نوشته شده که یک نشست تازه — با هر مدلی — بتواند آن را بدون خواندن گفت‌وگوهای قبلی اجرا کند. فازها به ترتیب وابستگی هستند و هر کدام به‌تنهایی قابل تحویل است.

پایه: commit `350cbf0` روی `master`. ۸۱ تست سبز. اولین تولید تصویر واقعی روی ChatGPT با یک حساب انجام شده است.

## قواعد ثابت برای همه فازها

این‌ها اختیاری نیستند؛ هر کدام از یک شکست واقعی در همین پروژه آمده‌اند.

1. **تست قبل از کد.** ابتدا تست شکست‌خورده، بعد پیاده‌سازی. هیچ تستی سایت زنده را لمس نمی‌کند؛ `FixtureLauncher` در `tests/Loomi.Tests/BrowserTests.cs` با `Body` صفحه جایگزین می‌سازد.
2. **هیچ global query filter روی `UserId`.** dispatcher بدون کاربر اجرا می‌شود و با فیلتر سراسری دیتابیس را خالی می‌بیند و بی‌صدا هیچ کاری نمی‌کند. فیلتر مالکیت فقط صریح و در مسیرهای درخواست (`Ownership.OwnedBy`).
3. **migration فقط جایی که فاز می‌گوید.** migration را با build تازه بسازید (نه `--no-build`) و بعد `dotnet ef migrations has-pending-model-changes --project src/Loomi` باید «No changes» بگوید. enum ها به‌صورت int ذخیره می‌شوند؛ عضو جدید فقط به انتهای enum اضافه می‌شود.
4. **هیچ متن exception مرورگر، prompt، کوکی، توکن یا رمزی وارد لاگ نمی‌شود.** خطاها فقط با کد امن (`GenerationWorker.ErrorCodeFor`) به UI می‌رسند.
5. **antiforgery روی هر درخواست غیر-GET زیر `/api`** برای نشست کوکی می‌ماند. (فاز ۲ برای توکن Bearer استثنا می‌گذارد؛ نه قبل از آن.)
6. **دو زبانه.** هر رشته جدید در `src/Loomi/Client/i18n.js` هم `en` و هم `fa` دارد. تعداد کلیدها در دو زبان باید برابر بماند. رابط RTL است.
7. **سبک کد.** فشرده، expression-bodied، کامنت فقط برای «چرا». قبل از نوشتن، فایل همسایه را بخوانید.
8. **باندل Vue یک‌بار در پایان فاز** با `npm run build --prefix src/Loomi/Client`. build همزمان Vite و dotnet تست‌ها را بی‌صدا حذف می‌کند.
9. **تحویل فاز = کل تست‌ها سبز + migration هم‌خوان + یک commit با پیام توضیحی به نام مالک، بدون trailer.**
10. **selector حدسی ننویسید.** اگر DOM سایت لازم است، از نشست زنده با یک endpoint موقت ساختار (نه محتوا) را بخوانید، اصلاح کنید، endpoint را حذف کنید. سابقه: تصویر تولیدشده ChatGPT بیرون عنصر پیام دستیار، داخل `conversation-turn` است.

## معماری هدف

```
   Vue UI (cookie)      REST API (Bearer token)      Telegram bot (long polling)
        └──────────────────────┬──────────────────────────────┘
                     یک ClaimsPrincipal مشترک
                               │
                 GenerationService  ←→  Credits (ledger + pricing)
                               │ enqueue (charge)
                          Dispatcher
                               │ claims a row, picks an account of the right Provider
        ┌──────────────────────┼──────────────────────┐
  ChatGPT (browser)     Gemini (API or browser)    ...
```

اصول: یک نقطه برای ساخت هویت، یک نقطه برای شمارش اعتبار، یک واسط `IImageProvider` که هر ارائه‌دهنده پشت آن است، و `GenerationService` تنها راه ورود کار به صف — UI، API و ربات همه از همان می‌گذرند.

---

## فاز ۰ — پاک‌سازی ساختار و schema نهایی

**وضعیت: انجام شد (commit `52e5c7c`).** ۸۲ تست سبز، migration `ProvidersCreditsInputs` روی دیتابیس واقعی اعمال و حساب `default` موجود به‌عنوان حساب ChatGPT پذیرفته شد. دو انحراف کوچک از متن اصلی: (۱) جدول `Accounts` نامش عوض نشد (فقط نوع CLR به `ProviderAccount`)، پس هیچ rename فیزیکی و ریسک SQLite در کار نبود. (۲) کاربر owner همچنان `DailyQuota = int.MaxValue` دارد نه `0`؛ چون `0 = بدون سقف` تازه معنا یافته، در فاز ۱ می‌توان owner را به `0` تمیزتر برد. جدول‌های `Uploads/GenerationInputs/CreditEntries/PricingRules/ApiTokens/TelegramLinks/LinkCodes` ساخته شده‌اند ولی هنوز رفتاری ندارند.

**سختی: سخت.** پایه همه فازهای بعدی است؛ اشتباه اینجا در همه‌جا تکثیر می‌شود.

### هدف
کد فعلی را به شکلی درآوریم که فازهای بعد فقط منطق اضافه کنند، نه اینکه ساختار را جابه‌جا کنند. کل schema جدید در **یک** migration اینجا ساخته می‌شود تا فازهای بعد هیچ migration جدیدی نسازند.

### تصمیم‌های گرفته‌شده
- `BrowserAccount` → `ProviderAccount`. حسابِ یک ارائه‌دهنده است، نه لزوماً یک مرورگر.
- `Provider` enum: `{ ChatGPT, Gemini }`. `AccountKind` enum: `{ Browser, ApiKey }`.
- **اعتبار جای سهمیه روزانه را می‌گیرد.** `AppUser.DailyQuota` می‌ماند اما معنی‌اش «سقف روزانه اختیاری» می‌شود (`0` = بدون سقف). ارز اصلی «اعتبار» است که در فاز ۱ فعال می‌شود.
- موجودی اعتبار **ذخیره نمی‌شود**؛ جمع دفتر (`CreditEntries`) است — همان فلسفه «بشمار، ذخیره نکن» که برای سهمیه به کار رفت.
- ورودی‌های یک generation به جدول جدا می‌روند تا چند تصویر ممکن شود. `ParentGenerationId` برای نمایش درخت می‌ماند.
- `BrowserAutomationService` (پل قدیمی که همه‌چیز را به «اولین حساب» می‌بست) **حذف می‌شود**. `/api/auth/status` وضعیت تجمیعی استخر را می‌دهد؛ `connect`/`reset` فقط به‌ازای حساب.

### schema (یک migration به نام `ProvidersCreditsInputs`)

| جدول | تغییر |
|---|---|
| `ProviderAccounts` (rename از `BrowserAccounts`) | + `Provider` int، + `Kind` int، + `Secret` string? (کلید API، محافظت‌شده با Data Protection)، `ProfileDirectory` nullable |
| `Generations` | + `Provider` int، + `CreditCost` int (۰ تا فاز ۱) |
| `Uploads` (جدید) | `Id`, `UserId`, `Path`, `ContentType`, `Bytes`, `CreatedAt`؛ index روی `UserId` |
| `GenerationInputs` (جدید) | `Id`, `GenerationId`, `Order`, `UploadId?`, `SourceGenerationId?`؛ دقیقاً یکی از دو FK پر است |
| `CreditEntries` (جدید) | `Id`, `UserId`, `Amount` (مثبت/منفی), `Kind` int `{Grant, Charge, Refund, Adjust}`, `GenerationId?`, `ByUserId?`, `Note?`, `CreatedAt`؛ index `(UserId, CreatedAt)` |
| `PricingRules` (جدید) | `Id`, `Provider`, `Operation?` (null = همه), `Cost`؛ unique `(Provider, Operation)` |
| `ApiTokens` (جدید) | `Id`, `UserId`, `Name`, `Prefix` (۸ نویسه)، `Hash`, `CreatedAt`, `LastUsedAt?`, `RevokedAt?`؛ unique `Prefix` |
| `TelegramLinks` (جدید) | `Id`, `UserId`, `ChatId` long, `LinkedAt`؛ unique `ChatId`, unique `UserId` |
| `LinkCodes` (جدید) | `Code` (PK, ۸ نویسه), `UserId`, `ExpiresAt` |

داده موجود: هر `BrowserAccount` با `Provider = ChatGPT`, `Kind = Browser` منتقل می‌شود. هیچ ردیفی گم نمی‌شود.

### کد
- `src/Loomi/Providers/IImageProvider.cs`: `Provider Provider { get; }`، `Task<ConnectionStatus> StatusAsync(ProviderAccount)`, `Task<ConnectionStatus> ConnectAsync(ProviderAccount, ct)`, `Task ResetAsync(ProviderAccount, ct)`, `Task<BrowserResult> RunAsync(ProviderAccount, GenerationRequest, report, ct)`. `GenerationRequest` = prompt + فهرست مسیر فایل‌های ورودی + گفتگوی والد (برای Edit).
- `src/Loomi/Providers/ChatGptProvider.cs`: پوشش `BrowserPool`/`BrowserSession` فعلی. رفتار فعلی عیناً حفظ می‌شود.
- `src/Loomi/Providers/ProviderRegistry.cs`: `Provider → IImageProvider`.
- Dispatcher (`GenerationWorker`): حساب را از بین حساب‌های **همان `Provider`** انتخاب می‌کند. بقیه منطق (claim دیتابیسی، کمترین بار، `AccountHealth`) دست نمی‌خورد.
- `AuthController`: `GET /api/auth/status` → `{ providers: [{ provider, accounts: n, connected: n, stalled }] }` تجمیعی؛ `connect`/`reset` فقط `accounts/{id}/...`. رابط (مودال اتصال) به این شکل درمی‌آید.
- `Quota.cs` می‌ماند و تنها محل شمارش مصرف روزانه است؛ کوئری تکراری در `UsersController.UsageAsync` حذف و به آن ارجاع داده می‌شود.
- `Security/Principal.cs`: تنها جایی که از یک `AppUser` claims ساخته می‌شود (`SessionController.Login` از آن استفاده می‌کند؛ فاز ۲ هم همان را برای توکن به کار می‌برد).
- `GenerationService.SubmitAsync` امضایش `GenerationRequest` می‌گیرد (prompt, provider, inputs, parent). UI فعلاً فقط ChatGPT و بدون ورودی می‌فرستد.

### معیار پذیرش
- کل تست‌های موجود سبز؛ تست جدید برای: انتقال داده حساب‌ها، انتخاب حساب بر اساس Provider، وضعیت تجمیعی، حذف پل قدیمی (هیچ ارجاعی به `BrowserAutomationService` نمانده).
- `GET /api/auth/status` دیگر به «اولین حساب» وابسته نیست.
- migration با `has-pending-model-changes` هم‌خوان.

### تله‌ها
- `has-pending-model-changes` با سرور در حال اجرا (فایل قفل) شکست می‌خورد؛ اول سرور را ببندید.
- rename جدول در SQLite: EF `RenameTable` را پشتیبانی می‌کند؛ ولی index ها را دستی بررسی کنید.

---

## فاز ۱ — اعتبار و قیمت‌گذاری

**وضعیت: انجام شد (commit `4d58b62`).** ۹۰ تست سبز، بدون migration تازه (جدول‌ها از فاز ۰ آمدند). روی دیتابیس واقعی تأیید شد: قیمت پیش‌فرض بذر شد، سهمیه owner از `int.MaxValue` به `0` نرمال شد، اعطای اعتبار و شارژ و بازپرداخت و رد `InsufficientCredits` همه زنده کار کردند.

سه انحراف/تکمیل نسبت به متن اصلی: (۱) **owner شارژ نمی‌شود** — او هزینه خود حساب‌های ارائه‌دهنده را می‌دهد، پس گرفتن اعتبار از او دفترداری بی‌پشتوانه است؛ همین برای کاری که کاربرش دیگر وجود ندارد هم صادق است. (۲) ساخت کاربر یک **موجودی اولیه** اختیاری می‌گیرد تا هر کاربر تازه دو درخواست جدا لازم نداشته باشد. (۳) `RefundAsync` خودش هم بررسی می‌کند کار `Failed` یا `Cancelled` باشد، نه فقط اینکه شارژ شده — تصویری که تحویل شده پولش داده شده است.

سه بازمانده فاز ۰ هم اینجا جمع شد: سنتینل سهمیه owner، پیام «مرورگر روی همین دسکتاپ باز است» که در بازنویسی مودال افتاده بود، و نمایش «سهمیه تصویر این حساب امروز تمام شده» که API می‌داد ولی رابط نشان نمی‌داد.

**سختی: متوسط.**

### هدف
هر کاربر موجودی اعتبار دارد (مثلاً ۱۵۰۰). مالک تعیین می‌کند هر ارائه‌دهنده/عملیات چند اعتبار می‌برد. کار در لحظه صف‌شدن شارژ می‌شود؛ شکست یا لغو برمی‌گرداند.

### تصمیم‌های گرفته‌شده
- شارژ در `GenerationService.SubmitAsync`: هزینه از `PricingRules` (اول `(Provider, Operation)` دقیق، بعد `(Provider, null)`)، اگر موجودی کم بود `InsufficientCredits` → 409. مبلغ در `Generation.CreditCost` ثبت و یک `CreditEntry(Charge, -cost, GenerationId)` نوشته می‌شود.
- بازپرداخت: وقتی generation به `Failed` یا `Cancelled` می‌رسد، **دقیقاً یک** `Refund` با `GenerationId` نوشته می‌شود؛ قبلش چک می‌شود Refund برای آن generation وجود نداشته باشد (idempotent). این در مسیر Failed/Cancelled در `GenerationWorker` و در `GenerationsController.CancelAsync` انجام می‌شود — یک متد مشترک `Credits.RefundAsync(db, generationId)`.
- حرکت کار به حساب دیگر (`MoveOnAsync`) هزینه را دوبار نمی‌گیرد؛ generation همان ردیف است.
- سهمیه روزانه اختیاری (`DailyQuota > 0`) همچنان در همان‌جا اعمال می‌شود؛ اعتبار و سهمیه هر دو باید بگذرند.
- پیش‌فرض قیمت‌ها اگر جدول خالی بود: ChatGPT = 10، Gemini = 5 (قابل ویرایش توسط مالک). بذر در `OwnerBootstrap`.

### API
- `GET /api/session` → + `credits` (موجودی).
- `GET /api/credits` (خود کاربر): موجودی + ۵۰ رکورد آخر دفتر.
- مالک: `POST /api/users/{id}/credits { amount, note }` (Grant/Adjust، منفی مجاز)، `GET /api/users/{id}/credits`.
- مالک: `GET/PUT /api/pricing` فهرست قوانین.

### رابط
- موجودی کنار نام کاربر و در پایین کادر متن، به‌جای «سهمیه باقی‌مانده» وقتی سهمیه صفر است.
- صفحه Users: ستون موجودی + دکمه «افزودن اعتبار».
- بخش جدید در Settings مالک: «قیمت‌ها».
- خطای `InsufficientCredits` دو زبانه.

### تست‌ها
شارژ صحیح با قانون دقیق و قانون عمومی؛ رد با موجودی ناکافی؛ بازپرداخت یک‌بار در Failed و یک‌بار در Cancelled و نه دوبار؛ بازپرداخت نشدن در Completed؛ منفی شدن مجاز فقط با Adjust مالک؛ کاربر عادی نمی‌تواند به دیگری اعتبار بدهد (۴۰۳).

---

## فاز ۲ — توکن API و API عمومی

**وضعیت: انجام شد (commit `b2c0115`).** ۱۰۷ تست سبز، بدون migration تازه. روی برنامه زنده تأیید شد: توکن از رابط ساخته شد، با آن بدون کوکی و CSRF پروژه ساخته شد (201)، توکن غلط و بی‌شکل 401، `/desktop` با توکن 401، مدیریت توکن با توکن `403 SessionRequired`، سند OpenAPI با ۳۱ مسیر و scheme «Bearer»، و درخواست شصت‌ویکم `429 {"error":"RateLimited"}` با `Retry-After: 60`. توکن باطل‌شده بلافاصله 401 شد.

انحراف‌ها و تکمیل‌ها نسبت به متن اصلی: (۱) `Microsoft.AspNetCore.OpenApi` **پکیج است** نه جزء فریم‌ورک؛ نسخه 10.0.12 اضافه شد. (۲) به‌جای «بررسی AuthenticationType» به‌تنهایی، یک **policy scheme** انتخاب‌گر (`Auto`) جلوی Cookie و Token نشسته: درخواستی که هدر Bearer دارد فقط از مسیر توکن می‌رود و هرگز به کوکیِ همراهش برنمی‌گردد — این است که معافیت CSRF را بی‌خطر می‌کند، و تست دارد. (۳) توکن **فقط زیر `/api`** معتبر است؛ نه `/desktop` و نه هاب. (۴) **ساخت و باطل کردن توکن با خود توکن ممکن نیست** (`[SessionOnly]` → 403) تا توکن لو‌رفته نتواند جانشین بسازد؛ سقف ۲۰ توکن فعال به‌ازای هر کاربر. (۵) rate limit به‌صورت `GlobalLimiter` partition‌شده روی claim پیشوند است، و رد شدن بدنه‌ی JSON و `Retry-After` دارد — قبلاً 429 بدون بدنه بود. (۶) حذف کاربر توکن‌هایش را هم پاک می‌کند (FK ندارد). (۷) `docs/API.md` برای integrator نوشته شد: جریان ساخت پروژه → generate → polling → دانلود، با جدول همه کدهای خطا. دو بازمانده هم رفع شد: پنل حساب‌های ChatGPT برای عضو نمایش داده می‌شد (API از فاز ۰ owner-only بود) و سهمیه‌ی `0` در فهرست کاربران «۰» نشان می‌داد نه «بدون سقف».

**سختی: متوسط به بالا.** تعامل احراز هویت Bearer با antiforgery و rate limit ظریف است.

### هدف
هر کاربر می‌تواند توکن بسازد و با آن از همان API استفاده کند (بدون کوکی و CSRF). ربات تلگرام و هر ابزار خارجی از همین می‌گذرد.

### تصمیم‌های گرفته‌شده
- شکل توکن: `lm_<prefix8>_<secret32>`. فقط hash ذخیره می‌شود (SHA-256 کافی است چون secret تصادفی ۳۲ بایتی است و نیازی به کندسازی ندارد). توکن **فقط یک‌بار** در پاسخ ساخت نمایش داده می‌شود.
- scheme جدید `Token` (AuthenticationHandler سفارشی در `Security/TokenAuthentication.cs`): هدر `Authorization: Bearer ...` → پیدا کردن با `Prefix` → مقایسه hash با زمان ثابت → `Principal.From(user)` → به‌روزرسانی `LastUsedAt` (حداکثر یک‌بار در دقیقه تا هر درخواست write نشود). کاربر غیرفعال یا توکن باطل‌شده → 401.
- policy پیش‌فرض: `Cookie OR Token`. وقتی هویت از `Token` آمده، میان‌افزار antiforgery رد می‌شود (بررسی `User.Identity.AuthenticationType == "Token"`). برای کوکی هیچ تغییری.
- rate limit جدا برای توکن: ۶۰ درخواست در دقیقه به‌ازای هر توکن (partition روی prefix).
- مالک می‌تواند توکن‌های همه را ببیند و باطل کند؛ کاربر فقط مال خودش.
- OpenAPI با `Microsoft.AspNetCore.OpenApi` (داخل فریم‌ورک، بدون پکیج) در `/openapi/v1.json`، فقط در Development یا برای مالک.

### API
- `GET /api/tokens`, `POST /api/tokens { name }` → `{ id, name, prefix, token }` (یک‌بار)، `DELETE /api/tokens/{id}` (revoke).
- مالک: `GET /api/users/{id}/tokens`, `DELETE /api/users/{id}/tokens/{tokenId}`.
- مسیرهای موجود بدون تغییر؛ فقط با Bearer هم کار می‌کنند. برای ابزار خارجی مهم: `POST /api/projects/{id}/generate` → 202 با `id`؛ `GET /api/generations/{id}` برای polling؛ `GET /api/generations/{id}/image` برای دانلود.

### رابط
- Settings: بخش «توکن‌های API» با ساخت (نمایش یک‌باره + کپی)، فهرست، باطل کردن.

### تست‌ها
درخواست با Bearer معتبر بدون کوکی و بدون CSRF موفق؛ توکن باطل/کاربر غیرفعال 401؛ کوکی همچنان CSRF می‌خواهد؛ توکن دیگری ۴۰۴؛ hash در پاسخ فهرست نیست؛ rate limit 429؛ مالک توکن دیگری را باطل می‌کند.

---

## فاز ۳ — چند تصویر ورودی

**وضعیت: انجام شد (commit `bab17c5`).** ۱۱۹ تست سبز، بدون migration تازه. طبق قاعده ۱۰ با یک endpoint موقت روی نشست زنده تأیید شد و بعد حذف شد: `input[type=file]` سایت `multiple` دارد، دو فایل با یک `SetInputFiles` رفتند، بعد از چند ثانیه دقیقاً دو دکمه‌ی «Remove file N: name» (selector `UploadReady` درست بود) و صفر `progressbar`. دو اجرای واقعی با دو تصویر مرجع هر دو تصویر را در turn کاربر کنار prompt گذاشتند؛ هر دو `NoImageReturned` شدند و ساختار پاسخ (متن ~۳۳۰ نویسه، بدون تصویر، الگوی limit/refusal) نشان داد سهمیه‌ی تصویر حساب تمام بود، نه اینکه آپلود خراب باشد — همان مسیری که از قبل مدیریت می‌شود. تنها selector منحرف‌شده منوی پیوست بود («Add files and more») که گسترده شد؛ عملاً لازم هم نیست چون input از ابتدا در صفحه هست.

انحراف‌ها و تکمیل‌ها: (۱) آپلودها زیر `Storage/images/uploads/{user}/` هستند نه `Storage/uploads/`، تا یک `Resolve` همه را پوشش دهد. (۲) والد در edit/branch **به‌عنوان ورودی ۰ ثبت می‌شود** و dispatcher همه‌ی ورودی‌ها را یک‌دست از `GenerationInputs` می‌خواند؛ ردیف‌های قدیمی بدون ورودی همچنان والدشان را می‌فرستند. (۳) تنها ورودیِ generate اگر یک تولید از **همان پروژه** باشد والد می‌شود؛ چند ورودی مجموعه‌اند و والد ندارند. (۴) سقف `MaxInputs` روی مجموع با احتساب والد است. (۵) `DELETE /api/uploads/{id}` اضافه شد (فقط تا وقتی هیچ تولیدی از آن استفاده نکرده؛ وگرنه `UploadInUse`). (۶) پاک‌سازی یتیم‌ها ساعتی در همان حلقه‌ی dispatcher است، نه فقط در startup. (۷) گزینه‌ی `UploadTimeoutMs` و کدهای خطای `UploadFailed`/`InputMissing`. (۸) سقف بدنه‌ی Kestrel برای `/api/uploads` در میان‌افزار امنیتی بالا می‌رود، چون بررسی antiforgery فرم را قبل از attribute‌های MVC می‌خواند. (۹) شکل multipart خالیِ HttpClient دات‌نت (section بدون Content-Disposition) با ProblemDetails رد می‌شود نه `NoFiles`؛ مرورگر واقعی برای FormData خالی فقط boundary پایانی می‌فرستد که `NoFiles` می‌دهد و تست همان را می‌فرستد.

**سختی: سخت.** آپلود چند فایل روی سایت زنده باید تأیید شود.

### هدف
کاربر می‌تواند چند تصویر بفرستد و با prompt توضیح دهد چه بسازد. Edit/Branch به حالت خاص همین (یک ورودی = تصویر والد) تبدیل می‌شوند.

### تصمیم‌های گرفته‌شده
- `POST /api/uploads` (multipart، حداکثر ۴ فایل در هر درخواست، هر فایل تا ۲۰ مگابایت، فقط PNG/JPEG/WebP با بررسی امضای فایل نه پسوند) → فهرست `{ id, bytes, contentType }`. ذخیره در `Storage/uploads/{userId}/{id}.{ext}` با همان الگوی امن `ImageStorage.Resolve`.
- `POST .../generate|edit|branch` می‌پذیرد: `{ prompt, inputs: [{ uploadId } | { generationId }] }` حداکثر `Browser:MaxInputs` (پیش‌فرض ۴). آپلود باید مال همان کاربر باشد (مالک همه را می‌بیند)؛ generation ورودی باید `Completed` با تصویر باشد.
- در `BrowserSession.RunAsync`: `SetInputFilesAsync` با آرایه مسیرها؛ منتظر `UploadReady` به تعداد فایل‌ها (شمارش) و ناپدید شدن `UploadBusy`. selector ها را با نشست زنده تأیید کنید (قاعده ۱۰).
- ورودی‌ها در `GenerationInputs` با `Order` ثبت می‌شوند؛ اگر تنها ورودی یک generation است، `ParentGenerationId` هم همان را می‌گیرد تا درخت فعلی درست بماند.
- پاک‌سازی: آپلودهایی که در هیچ generation استفاده نشده‌اند بعد از ۲۴ ساعت حذف می‌شوند (کار زمان‌بندی‌شده ساده در `GenerationWorker.SweepAsync`).

### رابط
- کادر متن: دکمه پیوست، پیش‌نمایش بندانگشتی، حذف هر کدام، شمارنده `n/4`.
- کارت generation: نمایش بندانگشتی ورودی‌ها.

### تست‌ها
رد پسوند درست با محتوای غلط؛ رد فایل بزرگ؛ آپلود دیگری ۴۰۴؛ ثبت ترتیب ورودی‌ها؛ fixture با سه فایل که `window.submission.files == 3` بدهد؛ پاک‌سازی آپلود یتیم.

---

## فاز ۴ — ربات تلگرام

**وضعیت: انجام شد (commit `4adeb33`).** ۱۲۹ تست سبز در آن نقطه، بدون migration تازه. `Telegram.Bot` 19.0.0، long polling در `TelegramBotWorker`، و بدون توکن سرویس بی‌صدا غیرفعال است — روی برنامه زنده تأیید شد: `/api/telegram/link` با `configured:false` جواب می‌دهد و ساخت کد `409 TelegramNotConfigured`.

انحراف‌ها: (۱) نتیجه از یک `GenerationEvents` داخلی می‌آید که `GenerationWorker` کنار همان `NotifyAsync` SignalR منتشر می‌کند؛ SignalR دست‌نخورده ماند. (۲) ربات فقط کارِ خودش را جواب می‌دهد — هر چیزی در پروژه‌ی «Telegram» آن کاربر — تا تصویری که در وب ساخته شده سرزده در چت نیفتد. (۳) زبان آخرین پیام هر چت در حافظه نگه داشته می‌شود، چون کارِ تمام‌شده پیامی پشتش ندارد که زبانش را بگوید. (۴) متن‌های ربات یک جدول سمت سرور است نه `i18n.js` (آن یک باندل مرورگر است و سرور نمی‌خواندش)، و کلیدهای خطایش همان کدهای امن موجودند تا نگاشت دومی برای هماهنگ نگه‌داشتن نباشد. (۵) `Cancellation` از `GenerationsController` بیرون کشیده شد تا ربات و API بر سر معنی لغو و بازپرداخت از هم جدا نیفتند. (۶) حذف کاربر اتصال و کدهایش را هم پاک می‌کند. (۷) مدیریت اتصال `[SessionOnly]` است، مثل مدیریت توکن.

**تأیید نشده:** هیچ توکن ربات واقعی روی این ماشین نیست، پس هیچ پیامی تا تلگرام واقعی نرفته. `ITelegramClient` در همه‌ی ۱۰ تست جایگزین شده و مسیر long polling فقط از راه ساختار بررسی شده است.

**سختی: متوسط به بالا.** media group تلگرام (چند عکس با هم) به‌صورت پیام‌های جدا می‌رسد.

### هدف
کاربر حساب Loomi خود را به تلگرام وصل می‌کند و از داخل تلگرام تصویر می‌سازد، عکس مرجع می‌فرستد، موجودی می‌بیند و لغو می‌کند.

### تصمیم‌های گرفته‌شده
- پکیج `Telegram.Bot` (آخرین پایدار). long polling در یک `BackgroundService` (`Telegram/TelegramBotWorker.cs`)؛ اگر `Telegram:BotToken` تنظیم نشده، سرویس بی‌صدا غیرفعال است. webhook لازم نیست، پس بدون HTTPS عمومی هم کار می‌کند.
- اتصال حساب: کاربر در Settings دکمه «اتصال تلگرام» → کد ۸ نویسه‌ای با انقضای ۱۰ دقیقه (`LinkCodes`) → در ربات `/start <code>` → `TelegramLinks`. یک کاربر فقط یک چت، یک چت فقط یک کاربر.
- ربات **از HTTP خودش استفاده نمی‌کند**؛ مستقیم `GenerationService` را با `Principal.From(user)` صدا می‌زند. یک نقطه ورود کار به صف.
- دستورها: متن ساده → generate در پروژه پیش‌فرض کاربر «Telegram» (اگر نبود ساخته می‌شود)؛ عکس + caption → generate با ورودی؛ چند عکس با یک caption (media group) → جمع‌آوری با تأخیر ۱.۵ ثانیه روی `media_group_id` بعد یک generate؛ `/balance`؛ `/cancel` (همه کارهای ناتمام خودش)؛ `/status`.
- نتیجه: با `SendPhoto` برگردانده می‌شود؛ خطا با همان متن دو زبانه `i18n` (زبان: فارسی اگر `language_code` کاربر تلگرام `fa` بود).
- به‌روزرسانی وضعیت: ربات به همان رویداد `GenerationUpdated` گوش می‌دهد (از طریق یک `IGenerationEvents` داخلی که `NotifyAsync` هم آن را صدا می‌زند) تا وقتی کار تمام شد پیام بفرستد، نه با polling.
- امنیت: فایل عکس تلگرام از طریق Bot API دانلود و مثل آپلود عادی اعتبارسنجی می‌شود (امضا، اندازه). هیچ توکنی در لاگ نیست.

### تست‌ها
اتصال با کد معتبر/منقضی/استفاده‌شده؛ چت ناشناس هیچ کاری نمی‌کند؛ جمع‌آوری media group؛ نگاشت خطا به پیام؛ `/cancel` فقط کار خودش. برای Bot API یک واسط نازک (`ITelegramClient`) بگذارید تا در تست جایگزین شود.

---

## فاز ۵ — ارائه‌دهنده Google (Gemini)

**وضعیت: انجام شد (commits `7fc26b9` و `fd3503f`).** ۱۴۴ تست سبز، بدون migration تازه. **تصمیم فورک: API رسمی** — ChatGPT دقیقاً همان مرورگر ماند و هیچ تغییری نکرد.

نام مدل، endpoint و هدر کلید از مستندات رسمی روز ۲۰۲۶-۰۹-۱۷ خوانده شد (<https://ai.google.dev/gemini-api/docs/image-generation>): `POST https://generativelanguage.googleapis.com/v1beta/interactions`، هدر `x-goog-api-key`، بدنه `{model, input:[{type:text|image}]}`، و تصویر در بلوک `image` داخل `steps[].content`. مقدارها در `appsettings.json` و README با نشانی همان صفحه ثبت شدند.

تأیید زنده روی برنامه واقعی: ساخت حساب Gemini بدون کلید `400 ApiKeyRequired`، حساب ChatGPT با کلید `400 ApiKeyNotAllowed`، حساب Gemini با کلید → `kind: ApiKey`، `keyHint: ••••5678`، و کلید نه در فهرست API و نه به‌صورت خام در دیتابیس. کار صف‌شده روی Gemini با کلید جعلی ابتدا `AutomationFailed` شد — **یک باگ واقعی که فقط تماس زنده نشانش داد**: Google کلید رد‌شده را با ۴۰۰ و `status: INVALID_ARGUMENT` جواب می‌دهد و دلیل واقعی (`API_KEY_INVALID`) داخل `details[]` است. نگاشت اصلاح شد، بدنه‌ی واقعی به‌صورت verbatim در تست نشست، و اجرای دوباره `LoginRequired` داد و حساب کنار گذاشته شد.

انحراف‌ها: (۱) `Gemini__ApiRevision` اختیاری و خالی است؛ مستندات هدر را نام می‌برد ولی اجباری بودنش را نمی‌گوید، پس چیزی اختراع نشد. (۲) `BrowserResult.ConversationUrl` nullable شد و پروژه لینک قبلی‌اش را از دست نمی‌دهد. (۳) شکاف شناخته‌شده‌ی `ProjectsController` بسته شد: `provider` اختیاری با پیش‌فرض ChatGPT؛ `edit`/`branch` از والد به ارث می‌برند. (۴) درخواست برای ارائه‌دهنده‌ای که هیچ حساب فعالی سرویسش نمی‌دهد `409 NoAccount` می‌گیرد، ولی کارگاه بدون هیچ حسابی همچنان صف می‌کند. (۵) یک flake واقعی در بستر تست پیدا و رفع شد: `SqliteConnection.ClearAllPools()` سراسری است و یک factory هنگام پاک‌سازی، اتصال‌های factory دیگری را می‌بست؛ حالا هر کدام بدون pool کار می‌کند.

**تأیید نشده:** هیچ کلید واقعی Gemini روی این ماشین نیست، پس هیچ تصویری واقعاً از Gemini ساخته نشده. مسیر موفقیت فقط با `HttpMessageHandler` جعلی و بر پایه شکل مستندشده تست شده است؛ اولین کلید واقعی باید با یک تولید ساده امتحان شود.

**سختی: سخت.**

### تصمیمی که این فاز با آن شروع می‌شود
دو راه هست و باید صریح انتخاب شود:

| | مرورگر (gemini.google.com) | API رسمی Gemini |
|---|---|---|
| هم‌خوانی با فلسفه ChatGPT | بله | نه |
| ورود حساب Google در مرورگر خودکار | Google ورود در مرورگر خودکار را مسدود می‌کند («این مرورگر امن نیست»)؛ حتی با کانال Chrome واقعی ناپایدار است | لازم نیست |
| پایداری | selector های Google زود عوض می‌شوند | پایدار، مستند |
| هزینه | حساب Google | کلید API؛ tier رایگان دارد، بعدش پولی |
| noVNC روی سرور | لازم | لازم نیست |

**پیشنهاد: API رسمی.** ChatGPT به این دلیل مرورگری ماند که معماری موجود بود؛ Google ارائه‌دهنده جدیدی است و قفل شدن ورود Google در مرورگر خودکار یک ریسک شناخته‌شده است، نه حدس. اگر مالک مرورگر را انتخاب کرد، ساختار همان است و فقط `GeminiBrowserProvider` به‌جای `GeminiApiProvider` نوشته می‌شود؛ در آن صورت ابتدا ورود دستی را با یک حساب واقعی روی همین ماشین امتحان کنید و اگر Google مسدود کرد، به API برگردید.

### تصمیم‌های گرفته‌شده (مسیر API)
- `ProviderAccount` با `Kind = ApiKey`، `Secret` = کلید محافظت‌شده با `IDataProtector` (هیچ‌وقت در پاسخ API یا لاگ نمی‌آید؛ فهرست فقط `••••` + ۴ نویسه آخر).
- `Providers/GeminiApiProvider.cs`: `HttpClient` به endpoint تولید تصویر Gemini با مدل قابل تنظیم در `Gemini:Model`؛ ورودی‌های تصویر به‌صورت inline base64. **نام مدل و شکل درخواست را از مستندات رسمی روز اجرا بخوانید و در README ثبت کنید؛ از حافظه ننویسید.**
- `RunAsync` همان `report` ها را می‌دهد (`SendingPrompt` → `GeneratingImage` → `DownloadingImage`) تا UI بدون تغییر بماند. `ConversationUrl` برای Gemini null است و UI لینک چت را نشان نمی‌دهد.
- نگاشت خطا: 429/quota → `NoImageReturned` (تا همان منطق «حساب تمام شد» و انتقال به حساب دیگر کار کند)؛ کلید نامعتبر → `LoginRequired`؛ محتوای مسدود → کد جدید `ContentBlocked`.
- سقف روزانه حساب و `AccountHealth` عیناً برای حساب Gemini کار می‌کند.
- UI: در کادر متن یک انتخاب ارائه‌دهنده (فقط ارائه‌دهنده‌هایی که حساب فعال دارند)؛ در Settings افزودن حساب Gemini با کلید.

### تست‌ها
provider با `HttpMessageHandler` جعلی: موفق، 429، کلید بد، مسدود؛ راز در هیچ پاسخی نیست؛ dispatcher کار Gemini را به حساب ChatGPT نمی‌دهد و برعکس؛ قیمت‌گذاری جدا.

---

## فاز ۶ — استقرار

**وضعیت: انجام شد (commit `f50a823`).** CI حالا بعد از `docker build` خود کانتینر را بالا می‌آورد، از داخلش `/health` را می‌پرسد و می‌بیند noVNC روی ۶۰۸۰ گوش می‌دهد، با dump لاگ در شکست و timeout مشخص. بررسی migration بلافاصله بعد از build نشست، چون `--no-build` هر assembly روی دیسک را باور می‌کند.

دو یافته‌ی واقعی: (۱) نمونه‌ی nginx سقف بدنه‌ی ۱ مگابایت داشت و هر آپلود فاز ۳ را قبل از رسیدن به برنامه ۴۱۳ می‌کرد؛ به ۸۵ مگابایت رسید. (۲) volume چهارم برای uploads لازم نیست، چون آپلودها زیر همان ریشه‌ی images می‌نشینند. `shm_size` از `LOOMI_SHM_SIZE` خوانده می‌شود و README جدول اندازه سرور و تفاوت «تمام شدن RAM» با «تمام شدن /dev/shm» را دارد.

**تأیید نشده:** روی این ماشین Docker نیست. هیچ‌کدام از Dockerfile، compose و CI اینجا اجرا نشده‌اند؛ فقط خوانده شده‌اند. اولین push که CI را بزند، اولین تأیید واقعی است.

**سختی: متوسط.**

- Dockerfile: چند مرورگر روی همان display `:99`؛ حافظه هر Chrome حدود ۵۰۰MB → `shm_size` و راهنمای سایز سرور در README.
- CI: بعد از `docker build`، کانتینر را واقعاً بالا بیاورد و `/health` و پورت noVNC (۶۰۸۰) را چک کند — همان کلاس باگی که `/desktop/` را ۵۰۲ کرده بود.
- متغیرهای جدید در `.env.example` و compose: `Telegram__BotToken`, `Gemini__Model`.
- HTTPS اجباری در Production همان است؛ README برای توکن API یادآوری کند که توکن فقط روی HTTPS فرستاده شود.
- Backup: جدول‌های جدید در همان `loomi.db` هستند؛ `Storage/uploads` به فهرست volume ها اضافه شود.

---

## ترتیب و وابستگی

```
فاز ۰ ──► فاز ۱ ──► فاز ۲ ──► فاز ۴ (ربات؛ از فاز ۳ برای عکس استفاده می‌کند)
           │
           └──► فاز ۳ ──┘
فاز ۵ بعد از ۰ و ۱ هر وقت.   فاز ۶ آخر.
```

فاز ۳ و فاز ۲ مستقل‌اند و می‌توانند جابه‌جا شوند. فاز ۴ به هر دو نیاز دارد. فاز ۵ فقط به ۰ و ۱ نیاز دارد.

## آنچه عمداً در این نقشه نیست

- webhook تلگرام (long polling کافی است و HTTPS عمومی نمی‌خواهد).
- ذخیره موجودی به‌صورت ستون (جمع دفتر است).
- global query filter.
- جابه‌جایی به دیتابیس دیگر؛ SQLite برای این مقیاس کافی است و WAL فعال است.
