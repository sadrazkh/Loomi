# API عمومی Loomi

هر کاری که در رابط انجام می‌دهید از همین API می‌گذرد؛ یک ابزار خارجی، اسکریپت یا ربات با یک **توکن شخصی** دقیقاً همان مسیرها را می‌بیند. مستند ماشین‌خوان همه مسیرها در `/openapi/v1.json` است (در حالت Development برای همه، در غیر آن فقط برای مالک).

## احراز هویت

۱. در رابط: **تنظیمات → توکن‌های API → توکن جدید**. توکن شکل `lm_<۸ نویسه>_<۳۲ نویسه>` دارد و **فقط یک بار** نشان داده می‌شود؛ سرور تنها اثر انگشت (SHA-256) آن را نگه می‌دارد.
۲. در هر درخواست، هدر زیر را بفرستید:

```
Authorization: Bearer lm_xxxxxxxx_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

قواعد:

- توکن همان دسترسی کاربر شما را دارد (عضو یا مالک). با دقتِ رمز عبور نگهداری‌اش کنید.
- توکن فقط زیر `/api` کار می‌کند؛ نه `/desktop` و نه هاب SignalR.
- ساخت و باطل کردن توکن **با خود توکن ممکن نیست** (`403 SessionRequired`)؛ فقط از رابط یا نشست کوکی. یک توکن لو‌رفته نمی‌تواند برای خودش جانشین بسازد.
- با توکن نیازی به کوکی و هدر `X-CSRF-TOKEN` نیست. درخواستی که هدر Bearer دارد هرگز به کوکی برنمی‌گردد.
- سقف: **۶۰ درخواست در دقیقه به ازای هر توکن** → `429 {"error":"RateLimited"}` با هدر `Retry-After: 60`.
- توکن باطل‌شده یا کاربر غیرفعال → `401`.
- هر کاربر حداکثر ۲۰ توکن فعال دارد.

بررسی سریع:

```bash
curl -H "Authorization: Bearer $LOOMI_TOKEN" http://localhost:5237/api/session
```

پاسخ `authenticated: true`، نام کاربری، نقش و موجودی اعتبار (`credits`؛ برای مالک `null` چون شارژ نمی‌شود).

## جریان تولید تصویر

```
POST /api/projects                    → 201  پروژه (ظرف کار)
POST /api/projects/{id}/generate      → 202  generation با status: Queued
GET  /api/generations/{id}            → 200  وضعیت را تا Completed/Failed/Cancelled بپرسید
GET  /api/generations/{id}/image      → 200  فایل تصویر (PNG/WebP/JPEG)
```

```bash
# ۱. پروژه
curl -s -X POST -H "Authorization: Bearer $LOOMI_TOKEN" -H "Content-Type: application/json" \
  -d '{"title":"Posters"}' http://localhost:5237/api/projects
# → {"id":"…","title":"Posters","status":"Ready",…}

# ۲. درخواست تصویر (اعتبار همین‌جا کسر می‌شود)
curl -s -X POST -H "Authorization: Bearer $LOOMI_TOKEN" -H "Content-Type: application/json" \
  -d '{"prompt":"a lighthouse at dusk, film grain"}' http://localhost:5237/api/projects/$PROJECT/generate
# → 202 {"id":"…","status":"Queued","imageUrl":null,…}

# ۳. پرسیدن وضعیت (هر چند ثانیه؛ یک تولید معمولاً ۱ تا ۳ دقیقه طول می‌کشد)
curl -s -H "Authorization: Bearer $LOOMI_TOKEN" http://localhost:5237/api/generations/$GEN
# → {"status":"Completed","imageUrl":"/api/generations/…/image",…}

# ۴. دانلود
curl -s -H "Authorization: Bearer $LOOMI_TOKEN" -o out.png http://localhost:5237/api/generations/$GEN/image
```

نمونه Python:

```python
import time, requests
H = {"Authorization": "Bearer " + TOKEN}
base = "http://localhost:5237"
project = requests.post(f"{base}/api/projects", json={"title": "Posters"}, headers=H).json()
gen = requests.post(f"{base}/api/projects/{project['id']}/generate", json={"prompt": "a lighthouse at dusk"}, headers=H)
gen.raise_for_status()          # 409 با {"error": "InsufficientCredits"} یعنی موجودی کافی نیست
gid = gen.json()["id"]
while (g := requests.get(f"{base}/api/generations/{gid}", headers=H).json())["status"] not in ("Completed", "Failed", "Cancelled"):
    time.sleep(5)
if g["status"] == "Completed":
    open("out.png", "wb").write(requests.get(base + g["imageUrl"], headers=H).content)
else:
    print("failed:", g["errorMessage"])
```

### تصاویر مرجع (چند تصویر ورودی)

اول فایل‌ها را بارگذاری کنید، بعد شناسه‌ها را در `inputs` بدهید:

```
POST /api/uploads                     multipart/form-data، فیلد files (حداکثر ۴ فایل، هر کدام تا ۲۰ مگابایت)
                                      → 201 [{"id":"…","bytes":12345,"contentType":"image/png"}, …]
GET  /api/uploads/{id}                → خود تصویر
DELETE /api/uploads/{id}              → 204؛ اگر تولیدی از آن استفاده کرده 409 UploadInUse
```

فایل با **بایت‌هایش** شناخته می‌شود نه پسوندش: فقط PNG، JPEG و WebP. آپلودی که در هیچ تولیدی به کار نرود بعد از ۲۴ ساعت پاک می‌شود.

```bash
curl -s -X POST -H "Authorization: Bearer $LOOMI_TOKEN" \
  -F files=@sofa.png -F files=@fabric.jpg http://localhost:5237/api/uploads
# → [{"id":"U1",…},{"id":"U2",…}]

curl -s -X POST -H "Authorization: Bearer $LOOMI_TOKEN" -H "Content-Type: application/json" \
  -d '{"prompt":"the sofa upholstered in this fabric","inputs":[{"uploadId":"U1"},{"uploadId":"U2"}]}' \
  http://localhost:5237/api/projects/$PROJECT/generate
```

هر عضو `inputs` **یکی** از این دو را دارد: `uploadId` (آپلود خودتان) یا `generationId` (تولیدی تمام‌شده از خودتان). ترتیب همان ترتیب ارسال به سایت است و در پاسخ به‌صورت `inputs:[{uploadId, generationId, url}]` برمی‌گردد. در `edit` و `branch` تصویر والد خودبه‌خود ورودی اول است و `inputs` به آن اضافه می‌شود؛ جمعاً حداکثر ۴. اگر تنها ورودیِ یک `generate` یک تولید از همان پروژه باشد، همان والدش می‌شود تا درخت پروژه درست بماند.

### وضعیت‌های generation

`Queued` → `OpeningBrowser` → `OpeningChatGPT` → `SendingPrompt` → `WaitingForResponse` → `GeneratingImage` → `DownloadingImage` → `Completed`
و پایان‌های دیگر: `Failed` (با `errorMessage` کدی) و `Cancelled`.

### مسیرهای دیگر

| مسیر | کار |
|---|---|
| `GET /api/projects` | فهرست پروژه‌های شما |
| `GET /api/projects/{id}/generations` | همه تولیدهای یک پروژه |
| `POST /api/generations/{id}/edit {prompt}` | ویرایش روی همان گفت‌وگو (ادامه‌ی تصویر) |
| `POST /api/generations/{id}/branch {prompt}` | شاخه‌ی تازه از یک تصویر |
| `POST /api/generations/{id}/cancel` | لغو؛ در صف باشد یا در حال اجرا. اعتبار برمی‌گردد |
| `POST /api/generations/cancel` | لغو همه‌ی کارهای ناتمامِ خودتان (مالک: همه‌ی کاربران) |
| `DELETE /api/projects/{id}` | حذف پروژه و تصاویرش (تا کاری ناتمام دارد رد می‌شود) |
| `GET /api/credits` | موجودی و ۵۰ حرکت آخر دفتر |
| `GET /api/auth/status` | آیا ارائه‌دهنده‌ای آماده هست (`ready`) و اگر نه چرا (`stalled`) |

مسیرهای مالک (`/api/users`, `/api/auth/accounts`, `/api/pricing`) هم با توکنِ یک مالک کار می‌کنند، به جز مدیریت توکن‌ها.

## خطاها

بدنه‌ی هر خطا `{"error":"<code>"}` است؛ کد پایدار است و متن نیست.

| وضعیت | کد | معنی |
|---|---|---|
| 400 | `InvalidCsrf` | فقط نشست کوکی: هدر `X-CSRF-TOKEN` نبود |
| 400 | `InvalidTokenName` | نام توکن خالی یا بیش از ۶۴ نویسه |
| 400 | `NoFiles` / `TooManyFiles` / `FileTooLarge` / `InvalidImage` | آپلود: بدون فایل، بیش از ۴ فایل، فایل بالای ۲۰ مگابایت، یا بایت‌هایی که PNG/JPEG/WebP نیست |
| 409 | `TooManyInputs` | بیش از ۴ تصویر مرجع (با احتساب والد) |
| 409 | `InvalidInput` | عضو `inputs` هر دو یا هیچ‌کدام از شناسه‌ها را دارد، یا تولیدِ مرجع تمام نشده |
| 409 | `UploadInUse` | حذف آپلودی که تولیدی از آن ساخته شده |
| 401 | — | توکن نامعتبر، باطل‌شده، یا کاربر غیرفعال |
| 403 | `SessionRequired` | مدیریت توکن با توکن |
| 404 | `NotFound` | وجود ندارد **یا مال شما نیست** (تفاوتی داده نمی‌شود) |
| 409 | `InsufficientCredits` | قیمت این کار از موجودی بیشتر است |
| 409 | `QuotaExceeded` | سقف روزانه‌ی اختیاریِ شما پر شده |
| 409 | `NoAccount` | هیچ حساب ارائه‌دهنده‌ای برای این کار نیست |
| 409 | `ProjectBusy` | پروژه هنوز کاری در حال اجرا دارد |
| 409 | `QueueFull` | صف پر است؛ بعداً |
| 409 | `InvalidParent` / `InvalidPrompt` | ورودی نامعتبر |
| 409 | `TooManyTokens` | ۲۰ توکن فعال دارید |
| 429 | `RateLimited` | بیش از ۶۰ درخواست در دقیقه با این توکن |
| 500 | `ServerError` | خطای داخلی؛ جزئیات فقط در لاگ سرور |

`errorMessage` یک generation شکست‌خورده یکی از این کدهاست: `LoginRequired`, `VerificationRequired`, `GenerationTimeout`, `InvalidImage`, `ConversationNotSaved`, `NoAccount`, `NoImageReturned`, `QuotaExceeded`, `ContentBlocked`, `UploadFailed` (سایت پیوست‌ها را نپذیرفت), `InputMissing` (فایل مرجع دیگر روی دیسک نیست), `BrowserClosed`, `AutomationFailed`, `Interrupted`.
