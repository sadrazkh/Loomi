# راهنمای API عمومی Loomi — نسخه‌ی ۱

با این API هر اسکریپت، ربات یا برنامه‌ای می‌تواند همان کاری را بکند که شما در رابط Loomi می‌کنید: گفتگویی با ChatGPT شروع کند، تصویر بخواهد، منتظر بماند، تصویر را دانلود کند و در همان گفتگو ادامه دهد.

همه‌ی مسیرهای این راهنما زیر **`/api/v1`** هستند. این سطحِ پشتیبانی‌شده و پایدار API است. مسیرهای قدیمی‌تر `/api/*` (همان‌هایی که خود رابط وب صدا می‌زند) هنوز کار می‌کنند، اما برای کار رابط ساخته شده‌اند و ممکن است بی‌خبر عوض شوند؛ برای هر ابزار تازه از `v1` استفاده کنید.

- [مفاهیم](#مفاهیم)
- [احراز هویت](#احراز-هویت)
- [شروع سریع در سه قدم](#شروع-سریع-در-سه-قدم)
- [منتظر ماندن برای کارهای طولانی](#منتظر-ماندن-برای-کارهای-طولانی)
- [مرجع مسیرها](#مرجع-مسیرها)
- [فرستادن تصویر](#فرستادن-تصویر)
- [قالب‌ها](#قالبها)
- [خطاها](#خطاها)
- [محدودیت‌ها](#محدودیتها)
- [نمونه‌های کامل: curl، Python و JavaScript](#نمونههای-کامل-curl-python-و-javascript)
- [مسیرهای قدیمی `/api/*`](#مسیرهای-قدیمی-api)

## مفاهیم

**گفتگو (chat)** همان چیزی است که در رابط Loomi «پروژه» نام دارد، و هر پروژه دقیقاً **یک گفتگوی واقعی در ChatGPT** است. هر پیام تازه در یک گفتگو، پیام بعدی در همان گفتگوی ChatGPT است: هوش مصنوعی پیام‌ها و تصویرهای قبلی را می‌بیند، پس کافی است بنویسید «همین را زمستانی کن». تصویری که قبلاً در گفتگو هست دوباره فرستاده نمی‌شود.

**پیام (message)** یک درخواست در یک گفتگوست، و در عین حال **یک کار (job)** است که بعداً تمام می‌شود. ساخت هر تصویر از حدود نیم دقیقه تا چند دقیقه طول می‌کشد، و ممکن است پیش از آن در صف هم منتظر بماند. برای همین فرستادن پیام فوراً جواب می‌دهد (`202 Accepted`) و نتیجه را جداگانه می‌گیرید؛ [چطورش را اینجا ببینید](#منتظر-ماندن-برای-کارهای-طولانی).

هر پیام یکی از این وضعیت‌ها (`status`) را دارد:

| `status` | یعنی | پایانی است؟ |
|---|---|---|
| `queued` | در صف است؛ `queue` می‌گوید چند درخواست جلوتر است | نه |
| `running` | در حال اجرا روی ChatGPT؛ `stage` مرحله‌ی دقیق را می‌گوید | نه |
| `completed` | تصویر آماده است؛ `imageUrl` نشانی آن است | بله |
| `answered` | هوش مصنوعی به‌جای کشیدن تصویر **با متن جواب داد** (مثلاً سؤالی پرسید یا درخواست را نپذیرفت)؛ متن در `text` است. با پیام بعدی جوابش را بدهید | بله |
| `failed` | شکست خورد؛ `error` یک [کد پایدار](#کدهای-error-در-پیام-شکستخورده) است | بله |
| `cancelled` | لغو شد | بله |

`stage` نام دقیق مرحله است: `Queued`، `OpeningBrowser`، `OpeningChatGPT`، `SendingPrompt`، `WaitingForResponse`، `GeneratingImage`، `DownloadingImage`، و در پایان `Completed`، `Answered`، `Failed` یا `Cancelled`. برای تصمیم‌گیری در کد از `status` استفاده کنید؛ `stage` برای نشان دادن پیشرفت به کاربر است.

دو قاعده‌ی مهم:

- **هر گفتگو در هر لحظه فقط یک پیام می‌پذیرد**، درست مثل خود سایت ChatGPT. تا پیام قبلی تمام نشده، پیام بعدی با `409 ProjectBusy` رد می‌شود. صبر کنید پیام قبلی تمام شود، بعد بفرستید. برای کارهای هم‌زمان، چند گفتگوی جدا باز کنید.
- **اعتبار (credits) هنگام فرستادن پیام کسر می‌شود** (برای عضوها؛ مالک کارگاه شارژ نمی‌شود). اگر پیام `failed`، `cancelled` یا `answered` شود، اعتبارش برمی‌گردد، چون تصویری تحویل نشده است.

## احراز هویت

۱. در رابط Loomi بروید به **تنظیمات ← توکن‌های API ← توکن جدید**. توکن شکل `lm_<۸ نویسه>_<۳۲ نویسه>` دارد و **فقط یک بار** نشان داده می‌شود؛ سرور فقط اثر انگشت (SHA-256) آن را نگه می‌دارد.

۲. در هر درخواست این هدر را بفرستید:

```
Authorization: Bearer lm_xxxxxxxx_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

قواعد:

- توکن همان دسترسی حساب شما را دارد (عضو یا مالک). مثل رمز عبور از آن نگهداری کنید و در کد منبع نگذارید؛ از متغیر محیطی بخوانیدش (نمونه‌ها `LOOMI_TOKEN` را می‌خوانند).
- توکن فقط زیر `/api` کار می‌کند؛ نه برای `/desktop` و نه برای هاب SignalR.
- ساختن و باطل کردن توکن فقط از رابط ممکن است، نه با خود توکن (`403 SessionRequired`). پس توکنِ لورفته نمی‌تواند برای خودش جانشین بسازد. هر کاربر حداکثر ۲۰ توکن فعال دارد.
- با توکن نه کوکی لازم است و نه هدر `X-CSRF-TOKEN`.
- بدون توکن، یا با توکن نامعتبر، باطل‌شده یا متعلق به کاربرِ غیرفعال: `401` بدون بدنه.
- مالک کارگاه گفتگوها و پیام‌های همه‌ی کاربران را می‌بیند؛ هر عضو فقط مال خودش را.

بررسی سریع:

```bash
export LOOMI_TOKEN=lm_...
curl -H "Authorization: Bearer $LOOMI_TOKEN" https://loomi.example.com/api/v1/me
```

```json
{"id":"0ed6c7c3-89e5-456b-8c39-8dc285679e7f","username":"sara","role":"member","credits":1490,
 "dailyQuota":null,"dailyUsed":1,"dailyRemaining":null,
 "limits":{"maxImages":4,"maxImageBytes":20971520,"maxPromptLength":12000}}
```

در همه‌ی نمونه‌ها `https://loomi.example.com` را با نشانی Loomi خودتان عوض کنید. راهنمای داخل برنامه (تنظیمات ← «استفاده از Loomi در کد») همین نمونه‌ها را با نشانی واقعی سرور شما نشان می‌دهد.

## شروع سریع در سه قدم

**۱. توکن بسازید** و در متغیر محیطی بگذارید (بخش قبل).

**۲. گفتگو را با اولین پیام شروع کنید.** گفتگو به نام اولین پیامش نام‌گذاری می‌شود، مگر `title` بدهید:

```bash
curl -X POST https://loomi.example.com/api/v1/chats \
  -H "Authorization: Bearer $LOOMI_TOKEN" -H "Content-Type: application/json" \
  -d '{"prompt":"a lighthouse at dusk, film grain"}'
```

```json
{
  "chat": {"id":"f79c8e32-a3ee-428a-8ad5-ed7f1051aff7","title":"a lighthouse at dusk, film grain",
           "createdAt":"2026-09-25T00:14:28.2224134Z","updatedAt":"2026-09-25T00:14:28.2291012Z",
           "messageCount":1,"busy":true,"latestImageUrl":null},
  "message": {"id":"daac010e-9de6-4185-a030-df701cd36ce2","chatId":"f79c8e32-a3ee-428a-8ad5-ed7f1051aff7",
              "status":"queued","stage":"Queued","prompt":"a lighthouse at dusk, film grain","text":null,
              "imageUrl":null,"error":null,"queue":{"ahead":0,"etaSeconds":30},
              "createdAt":"2026-09-25T00:14:28.2264109Z","templateTitle":null}
}
```

**۳. منتظر بمانید، تصویر را بگیرید و ادامه دهید.** `wait` تا ۶۰ ثانیه صبر می‌کند و به محض تمام شدن پیام جواب می‌دهد؛ اگر هنوز تمام نشده بود `done: false` برمی‌گرداند و دوباره صدایش می‌زنید:

```bash
MESSAGE=daac010e-9de6-4185-a030-df701cd36ce2
curl -H "Authorization: Bearer $LOOMI_TOKEN" "https://loomi.example.com/api/v1/messages/$MESSAGE/wait?timeout=60"
# → {"done":true,"message":{"status":"completed","imageUrl":"/api/v1/messages/daac010e-…/image",…}}

curl -H "Authorization: Bearer $LOOMI_TOKEN" -o lighthouse.png \
  "https://loomi.example.com/api/v1/messages/$MESSAGE/image"

# پیام بعدی در همان گفتگو: تصویر قبلی دوباره فرستاده نمی‌شود، ChatGPT خودش آن را می‌بیند.
curl -X POST "https://loomi.example.com/api/v1/chats/f79c8e32-a3ee-428a-8ad5-ed7f1051aff7/messages" \
  -H "Authorization: Bearer $LOOMI_TOKEN" -H "Content-Type: application/json" \
  -d '{"prompt":"the same scene in winter"}'
# → 202 {"message":{"id":"b300cf47-…","status":"queued",…}}  و دوباره wait
```

همین جریان به‌صورت برنامه‌ی کامل و قابل اجرا در [نمونه‌های کامل](#نمونههای-کامل-curl-python-و-javascript) آمده است.

## منتظر ماندن برای کارهای طولانی

### چرا ناهمگام است؟

Loomi تصویر را از طریق رابط وب ChatGPT می‌سازد. هر تصویر از حدود ۳۰ ثانیه تا چند دقیقه طول می‌کشد (سقف هر اجرا ده دقیقه است) و اگر کارهای دیگری در صف باشند، پیش از آن هم باید منتظر بماند. نگه داشتن یک درخواست HTTP برای این مدت شکننده است: پراکسی‌ها، شبکه‌ی موبایل و کتابخانه‌های HTTP زودتر قطعش می‌کنند. برای همین فرستادن پیام فوراً برمی‌گردد و سه راه برای منتظر ماندن دارید. هیچ‌کدام کار را جلو یا عقب نمی‌اندازد: پیام روی سرور ادامه پیدا می‌کند، حتی اگر اتصال شما قطع شود.

### راه ۱ (پیشنهادی): حلقه‌ی long-poll با `wait`

```
GET /api/v1/messages/{id}/wait?timeout=60
```

- اگر پیام تمام شده باشد، **بی‌درنگ** جواب می‌دهد: `{"done": true, "message": {...}}`.
- اگر نه، تا `timeout` ثانیه صبر می‌کند و **همان لحظه‌ای که پیام تمام شود** جواب می‌دهد. اگر زمان تمام شد، `{"done": false, "message": {...}}` با وضعیت فعلی (مثلاً جایگاه در صف) برمی‌گرداند.
- `timeout` بین ۱ تا ۱۲۰ ثانیه است (پیش‌فرض ۶۰؛ مقدار بیرون از این بازه به نزدیک‌ترین حد برده می‌شود). پاسخ همیشه `200` است.
- حلقه ساده است: تا `done` برابر `true` نشده، دوباره صدا بزنید.

```python
while True:
    reply = requests.get(f"{BASE}/api/v1/messages/{message_id}/wait", params={"timeout": 60},
                         headers=headers, timeout=90).json()
    if reply["done"]:
        break
message = reply["message"]   # completed، answered، failed یا cancelled
```

هر فراخوانی `wait` فقط یک درخواست از سهمیه‌ی ۶۰ درخواست در دقیقه مصرف می‌کند؛ با `timeout=60` یعنی حدود یک درخواست در دقیقه برای هر پیامی که منتظرش هستید.

### راه ۲: رویدادهای زنده (Server-Sent Events)

```
GET /api/v1/messages/{id}/events
```

پاسخ `text/event-stream` است:

- بلافاصله یک رویداد با وضعیت فعلی پیام می‌آید، و بعد از آن **با هر تغییر** یک رویداد دیگر (تغییر مرحله، جابه‌جایی در صف، پایان).
- هر رویداد این شکل را دارد؛ `data` همان [شیء پیام](#شیء-پیام) است:

  ```
  event: status
  data: {"id":"8a114522-…","status":"running","stage":"GeneratingImage",…}

  ```

- اگر ۱۵ ثانیه چیزی گفته نشود، یک خط توضیح `: keep-alive` می‌آید تا پراکسی‌ها اتصال را نبندند.
- **بعد از وضعیت پایانی، سرور جریان را می‌بندد.** اگر پیام پیدا نشود، به‌جای جریان یک `404` معمولی می‌گیرید.

```bash
curl -N -H "Authorization: Bearer $LOOMI_TOKEN" "https://loomi.example.com/api/v1/messages/$MESSAGE/events"
```

Python:

```python
with requests.get(f"{BASE}/api/v1/messages/{message_id}/events", headers=headers, stream=True, timeout=(10, 60)) as response:
    response.raise_for_status()
    for line in response.iter_lines(decode_unicode=True):
        if line.startswith("data: "):
            message = json.loads(line[6:])
            print(message["status"], message["stage"])
# The loop ends by itself: the server closes the stream after the final state.
```

JavaScript (Node 18+):

```js
const response = await fetch(`${BASE}/api/v1/messages/${id}/events`, { headers: AUTH });
const decoder = new TextDecoder();
let buffer = '';
for await (const chunk of response.body) {
  buffer += decoder.decode(chunk, { stream: true });
  let end;
  while ((end = buffer.indexOf('\n\n')) >= 0) {
    const data = buffer.slice(0, end).split('\n').find(line => line.startsWith('data: '));
    buffer = buffer.slice(end + 2);
    if (data) { const message = JSON.parse(data.slice(6)); console.log(message.status, message.stage); }
  }
}
// The loop ends by itself: the server closes the stream after the final state.
```

`EventSource` مرورگر نمی‌تواند هدر `Authorization` بفرستد؛ در مرورگر یا از `fetch` مثل بالا استفاده کنید، یا از صفحه‌ای روی همان دامنه‌ی Loomi که با کوکیِ ورود کار می‌کند. اگر `EventSource` به کار می‌برید، بعد از رویداد پایانی `close()` را صدا بزنید، وگرنه خودش دوباره وصل می‌شود.

### راه ۳: پرسیدن دوره‌ای

`GET /api/v1/messages/{id}` را هر ۵ تا ۱۰ ثانیه بپرسید. ساده‌ترین است، اما هر پرسش یک درخواست از سهمیه‌ی دقیقه‌ای است و نتیجه را دیرتر می‌فهمید. اگر امکانش هست از `wait` استفاده کنید.

### زمان‌های انتظار و تکرار پیشنهادی

| موقعیت | کار درست |
|---|---|
| timeout کلاینت HTTP برای `wait` | دست‌کم `timeout` + ۳۰ ثانیه (مثلاً ۹۰ ثانیه برای `timeout=60`) |
| timeout برای بقیه‌ی درخواست‌ها | ۳۰ تا ۹۰ ثانیه؛ دانلود تصویرِ بزرگ ممکن است بیشتر بخواهد |
| قطع شبکه یا timeout در `wait`، `events` یا هر GET | بی‌خطر است؛ چند ثانیه صبر کنید و دوباره بفرستید. کار روی سرور ادامه دارد |
| قطع شبکه بعد از فرستادن POST | کورکورانه تکرار نکنید؛ اول با `GET /api/v1/chats/{id}` یا `GET /api/v1/chats` ببینید پیام ثبت شده یا نه، وگرنه ممکن است دو بار شارژ شوید |
| `409 ProjectBusy` | پیام قبلی این گفتگو هنوز تمام نشده؛ با `wait` منتظرش بمانید و بعد بفرستید |
| `409 QueueFull` | صف کل کارگاه پر است (۲۵ کار ناتمام)؛ یک تا چند دقیقه بعد دوباره امتحان کنید |
| `429 RateLimited` | به اندازه‌ی هدر `Retry-After` (ثانیه) صبر کنید و همان درخواست را تکرار کنید |
| `500` یا `502`/`503`/`504` | برای GET با فاصله‌ی رو به افزایش (مثلاً ۲، ۴، ۸ ثانیه) تکرار کنید؛ برای POST قاعده‌ی ردیف چهارم |
| پیام با `status: "running"` که مدت زیادی تمام نمی‌شود | عادی است؛ هر اجرا سقف ده دقیقه دارد و بعد از آن `failed` با `GenerationTimeout` می‌شود |

## مرجع مسیرها

قراردادهای عمومی:

- بدنه‌ی درخواست و پاسخ JSON است، با نام‌های camelCase. در درخواست‌هایی که بدنه دارند هدر `Content-Type: application/json` را بفرستید؛ بدون آن پاسخ `415 UnsupportedMediaType` است (`curl -d` به‌تنهایی بدنه را JSON معرفی نمی‌کند).
- شناسه‌ها GUID هستند. زمان‌ها ISO-8601 به وقت UTC با پسوند `Z` هستند.
- مسیری که زیر `/api/v1` وجود ندارد: `404 {"error":"NotFound"}`.
- مستند ماشین‌خوان (OpenAPI 3.1) فقط برای همین مسیرها: `GET /api/v1/openapi.json`، برای هر کاربرِ واردشده، با توکن یا کوکی.

| روش و مسیر | کار |
|---|---|
| `GET /api/v1/me` | حساب شما، اعتبار و سهمیه‌ی روزانه |
| `POST /api/v1/chats` | گفتگوی تازه، در صورت تمایل همراه با اولین پیام |
| `GET /api/v1/chats` | گفتگوهای شما، به ترتیب آخرین فعالیت |
| `GET /api/v1/chats/{id}` | یک گفتگو با همه‌ی پیام‌هایش |
| `DELETE /api/v1/chats/{id}` | حذف گفتگو و تصویرهایش |
| `POST /api/v1/chats/{id}/messages` | پیام بعدی در همان گفتگو |
| `GET /api/v1/messages/{id}` | وضعیت یک پیام |
| `GET /api/v1/messages/{id}/wait` | صبر تا تمام شدن پیام (long-poll) |
| `GET /api/v1/messages/{id}/events` | دنبال کردن زنده‌ی پیام (SSE) |
| `GET /api/v1/messages/{id}/image` | فایل تصویر |
| `POST /api/v1/messages/{id}/cancel` | لغو پیام |
| `GET /api/v1/templates` | قالب‌هایی که می‌توانید استفاده کنید |
| `POST /api/v1/templates/{id}/use` | استفاده از قالب در گفتگوی تازه |
| `GET /api/v1/openapi.json` | مستند OpenAPI همین مسیرها |

### شیء پیام

| فیلد | نوع | معنی |
|---|---|---|
| `id` | GUID | شناسه‌ی پیام |
| `chatId` | GUID | گفتگویی که پیام در آن است |
| `status` | string | `queued`، `running`، `completed`، `answered`، `failed` یا `cancelled` ([جدول](#مفاهیم)) |
| `stage` | string | مرحله‌ی دقیق، مثل `GeneratingImage` |
| `prompt` | string | متنی که شما فرستادید. در پیامِ ساخته‌شده از قالب، فقط یادداشت شما (ممکن است خالی باشد) |
| `text` | string یا null | وقتی هوش مصنوعی با متن جواب داده (`answered`)، همان متن |
| `imageUrl` | string یا null | نشانی نسبی تصویر، `/api/v1/messages/{id}/image`؛ فقط وقتی تصویر هست |
| `error` | string یا null | برای `failed`، یک [کد پایدار](#کدهای-error-در-پیام-شکستخورده) |
| `queue` | object یا null | فقط وقتی `queued` است: `ahead` تعداد درخواست‌های جلوتر، `etaSeconds` تخمین ثانیه‌ها تا **شروع** نوبت (گام‌های ۳۰ ثانیه‌ای). هر دو ممکن است `null` باشند: `ahead` تا وقتی صف‌بند هنوز جایش را تعیین نکرده، و `etaSeconds` وقتی فعلاً هیچ حسابی نمی‌تواند آن را اجرا کند |
| `createdAt` | زمان | زمان ثبت |
| `templateTitle` | string یا null | نام قالب، اگر پیام از قالب ساخته شده |

دستورِ پنهانِ قالب‌ها هیچ‌وقت در هیچ پاسخی نیست.

### شیء گفتگو

| فیلد | نوع | معنی |
|---|---|---|
| `id` | GUID | شناسه‌ی گفتگو (همان شناسه‌ی پروژه در رابط) |
| `title` | string | نام گفتگو |
| `createdAt`، `updatedAt` | زمان | ساخت، و آخرین فعالیت (مثلاً فرستادن پیام) |
| `messageCount` | int | تعداد پیام‌ها |
| `busy` | bool | پیام ناتمامی دارد، پس پیام بعدی فعلاً `409 ProjectBusy` می‌گیرد |
| `latestImageUrl` | string یا null | آخرین تصویر گفتگو |

### `GET /api/v1/me`

```json
{"id":"…","username":"sara","role":"member","credits":1490,"dailyQuota":null,"dailyUsed":1,"dailyRemaining":null,
 "limits":{"maxImages":4,"maxImageBytes":20971520,"maxPromptLength":12000}}
```

`role` یکی از `owner` و `member` است. `credits` برای مالک `null` است چون شارژ نمی‌شود. `dailyQuota` و `dailyRemaining` وقتی سقف روزانه‌ای تعیین نشده `null` هستند. `limits` همان سقف‌هایی است که سرور اعمال می‌کند.

### `POST /api/v1/chats`

بدنه (همه‌ی فیلدها اختیاری، ولی دست‌کم یکی از `title` و `prompt` لازم است):

| فیلد | معنی |
|---|---|
| `title` | نام گفتگو، حداکثر ۱۶۰ نویسه. اگر نباشد، گفتگو به نام اولین پیامش نام‌گذاری می‌شود |
| `prompt` | اولین پیام، حداکثر ۱۲۰۰۰ نویسه |
| `images` | [تصویرهای همراه](#فرستادن-تصویر) اولین پیام |
| `provider` | `"ChatGPT"` (پیش‌فرض) یا `"Gemini"`؛ فقط برای اولین پیام معنا دارد و گفتگو تا آخر با همان می‌ماند |

- با `prompt`: `201 Created` با هدر `Location: /api/v1/chats/{id}` و بدنه‌ی `{"chat": {...}, "message": {...}}`؛ پیام در صف است.
- بدون `prompt`: فقط گفتگو ساخته می‌شود، `201` با `{"chat": {...}}`. اولین پیام را بعداً با `POST /api/v1/chats/{id}/messages` بفرستید.
- اگر ثبت پیام رد شود (مثلاً `InsufficientCredits`)، هیچ گفتگوی خالی‌ای باقی نمی‌ماند.

خطاهای ممکن: `400` با `InvalidTitle`، `InvalidPrompt`، `InvalidProvider` یا [خطاهای تصویر](#فرستادن-تصویر)؛ `409` با `QuotaExceeded`، `InsufficientCredits`، `NoAccount`، `QueueFull`، `TooManyInputs`؛ `404` اگر `uploadId` مال شما نباشد.

### `GET /api/v1/chats?limit=20&before=…`

گفتگوها به ترتیب **آخرین فعالیت** (`updatedAt`)، جدیدترین اول:

```json
{"chats":[{"id":"5731ebdb-…","title":"a slow one","createdAt":"…","updatedAt":"2026-09-25T00:17:53.848625Z","messageCount":1,"busy":false,"latestImageUrl":null}, …],
 "nextBefore":"2026-09-25T00:16:49.0550908Z"}
```

- `limit` بین ۱ تا ۱۰۰ (پیش‌فرض ۲۰).
- برای صفحه‌ی بعد، `nextBefore` را همان‌طور که هست در `before` بفرستید (URL-encode کنید). در صفحه‌ی آخر `nextBefore` برابر `null` است.
- `before` یک زمان ISO-8601 است؛ مقدار نامعتبر `400 InvalidRequest` می‌گیرد.
- همه‌ی گفتگوهای شما در این فهرست هستند، چه با API ساخته شده باشند، چه در رابط وب یا از ربات تلگرام.

### `GET /api/v1/chats/{id}`

`{"chat": {...}, "messages": [ ... ]}`، پیام‌ها به ترتیب زمان. گفتگوی دیگران `404` است، نه `403`، تا وجود شناسه‌ها قابل حدس نباشد.

### `DELETE /api/v1/chats/{id}`

گفتگو، پیام‌ها و تصویرهایش را پاک می‌کند: `204`. تا پیامی در آن ناتمام است، با `409 ProjectBusy` رد می‌شود. یا منتظر بمانید یا اول آن پیام را لغو کنید.

### `POST /api/v1/chats/{id}/messages`

```json
{"prompt": "the same scene in winter", "images": []}
```

`202 Accepted` با هدر `Location: /api/v1/messages/{id}` و بدنه‌ی `{"message": {...}}`.

- پیام، ادامه‌ی همان گفتگوی ChatGPT است. تصویرهای قبلی دوباره فرستاده نمی‌شوند؛ فقط تصویرهایی که در همین پیام در `images` می‌گذارید همراهش می‌روند.
- اگر آن گفتگوی ChatGPT دیگر در دسترس نباشد (مثلاً حسابی که گفتگو روی آن بود حذف یا غیرفعال شده)، Loomi آخرین تصویر گفتگو را همراه پیام می‌فرستد و گفتگوی تازه‌ای در ChatGPT باز می‌کند؛ از دید شما همان گفتگو ادامه پیدا می‌کند.
- اگر پیام قبلی `answered` بوده (هوش مصنوعی سؤالی پرسیده)، پیام شما جوابِ همان سؤال است.
- گفتگوی بدون پیام: این اولین پیام آن است، و `provider` اختیاری را هم می‌پذیرد.

خطاها: `409 ProjectBusy` (پیام قبلی تمام نشده)، `400 InvalidPrompt` (خالی یا بیش از ۱۲۰۰۰ نویسه)، و بقیه‌ی خطاهای `POST /api/v1/chats`.

### `GET /api/v1/messages/{id}`

[شیء پیام](#شیء-پیام)، مستقیم (بدون پوشش).

### `GET /api/v1/messages/{id}/wait?timeout=60`

`{"done": bool, "message": {...}}`، همیشه `200`. توضیح کامل در [راه ۱](#راه-۱-پیشنهادی-حلقهی-long-poll-با-wait).

### `GET /api/v1/messages/{id}/events`

جریان SSE؛ توضیح کامل در [راه ۲](#راه-۲-رویدادهای-زنده-server-sent-events).

### `GET /api/v1/messages/{id}/image`

خود فایل تصویر (`image/png`، `image/jpeg` یا `image/webp`، بسته به آنچه ChatGPT داده). هدر `Range` هم پشتیبانی می‌شود. اگر پیام مال شما باشد ولی تصویری نداشته باشد (هنوز تمام نشده، یا `answered` یا `failed` است): `404 {"error":"NoImage"}`. `imageUrl` در شیء پیام دقیقاً همین مسیر است؛ آن را به نشانی پایه بچسبانید و با همان هدر `Authorization` دانلود کنید.

### `POST /api/v1/messages/{id}/cancel`

بدون بدنه. پاسخ `200` با [شیء پیام](#شیء-پیام).

- پیامِ در صف همان لحظه `cancelled` می‌شود و اعتبارش برمی‌گردد.
- پیامِ در حال اجرا متوقف می‌شود، ولی وضعیت پایانی‌اش چند لحظه بعد ثبت می‌شود؛ برای همین پاسخ ممکن است هنوز `running` باشد. با `wait` منتظر `cancelled` بمانید.
- لغوِ پیامی که تمام شده خطا نیست و چیزی را عوض نمی‌کند؛ پاسخ می‌گوید پیام چطور تمام شده است.

### `GET /api/v1/openapi.json`

مستند OpenAPI 3.1 فقط برای مسیرهای `/api/v1`، برای ساختن کلاینت خودکار یا وارد کردن در Postman و ابزارهای مشابه. مستند کامل همه‌ی مسیرها (`/openapi/v1.json`) بیرون از حالت Development فقط برای مالک باز است.

## فرستادن تصویر

`POST /api/v1/chats`، `POST /api/v1/chats/{id}/messages` و `POST /api/v1/templates/{id}/use` یک آرایه‌ی `images` می‌پذیرند. هر عضو **دقیقاً یکی** از این دو را دارد:

- `{"base64": "…"}`: خود فایل، به‌صورت base64 ساده یا یک data URL مثل `data:image/png;base64,iVBOR…`.
- `{"uploadId": "…"}`: شناسه‌ی فایلی که قبلاً با `POST /api/uploads` بارگذاری کرده‌اید.

قواعد:

- حداکثر ۴ تصویر در هر پیام (`limits.maxImages` در `/api/v1/me`)؛ بیشتر از آن: `409 TooManyInputs`.
- هر تصویر حداکثر ۲۰ مگابایت؛ بیشتر: `400 FileTooLarge`.
- فقط PNG، JPEG و WebP. نوع فایل از **خود بایت‌ها** تشخیص داده می‌شود، نه از نام فایل یا نوعی که data URL ادعا می‌کند: `400 InvalidImage`.
- base64 نامعتبر یا data URL بدون `;base64,`: `400 InvalidBase64`. عضوی که هر دو یا هیچ‌کدام از `base64` و `uploadId` را دارد: `400 InvalidImageInput`.
- تصویرهای base64 به‌عنوان آپلودهای خود شما ذخیره می‌شوند. اگر درخواست رد شود (مثلاً `ProjectBusy`)، همان لحظه پاک می‌شوند و چیزی باقی نمی‌ماند.
- ترتیب تصویرها همان ترتیبی است که به ChatGPT فرستاده می‌شوند.

Python و JavaScript (با تابع `call` نمونه‌های کامل):

```python
import base64
picture = base64.b64encode(open("sofa.jpg", "rb").read()).decode()
call("POST", "/api/v1/chats", json={"prompt": "this sofa in green velvet", "images": [{"base64": picture}]})
```

```js
import { readFile } from 'node:fs/promises';
const picture = (await readFile('sofa.jpg')).toString('base64');
await call('POST', '/api/v1/chats', { prompt: 'this sofa in green velvet', images: [{ base64: picture }] });
```

با curl، فایل بزرگ را بهتر است اول بارگذاری کنید (`multipart/form-data`، فیلد `files`، تا ۴ فایل در یک درخواست) و بعد شناسه‌اش را بفرستید:

```bash
curl -H "Authorization: Bearer $LOOMI_TOKEN" -F files=@sofa.jpg https://loomi.example.com/api/uploads
# → [{"id":"1d1ddbd4-b80d-44eb-aa92-f9f290575171","bytes":183422,"contentType":"image/jpeg"}]

curl -X POST https://loomi.example.com/api/v1/chats \
  -H "Authorization: Bearer $LOOMI_TOKEN" -H "Content-Type: application/json" \
  -d '{"prompt":"the sofa in this fabric","images":[{"uploadId":"1d1ddbd4-b80d-44eb-aa92-f9f290575171"}]}'
```

یا مستقیم با base64 (در macOS به‌جای `base64 -w0 sofa.jpg` بنویسید `base64 -i sofa.jpg`):

```bash
printf '{"prompt":"this sofa in green velvet","images":[{"base64":"%s"}]}' "$(base64 -w0 sofa.jpg)" \
  | curl -H "Authorization: Bearer $LOOMI_TOKEN" -H "Content-Type: application/json" --data-binary @- \
      https://loomi.example.com/api/v1/chats
```

آپلودی که در هیچ پیامی به کار نرود، بعد از ۲۴ ساعت پاک می‌شود.

## قالب‌ها

قالب، ظاهری آماده است که مالک کارگاه تعریف کرده: یک نمونه‌ی تصویری برای انتخاب، و یک دستور (prompt) که **فقط مالک می‌بیند**. شما قالب را انتخاب می‌کنید، عکس‌های خودتان و در صورت تمایل یک یادداشت می‌فرستید، و دستور پنهان قالب همراه آن‌ها به ChatGPT می‌رود. آن دستور در هیچ پاسخی از API نیست.

`GET /api/v1/templates`:

```json
{"templates":[{"id":"…","title":"Golden hour couple","category":"Couples","instructions":"One clear photo of both of you.",
               "minImages":1,"maxImages":2,"sampleUrl":"/api/templates/…/sample?v=638…","price":10}]}
```

- فقط قالب‌های فعالی که تصویر نمونه دارند، به ترتیبی که مالک چیده است.
- `minImages` و `maxImages` تعداد عکس‌هایی است که قالب می‌خواهد.
- `price` هزینه‌ی هر بار استفاده برای شما به اعتبار است (برای مالک `null`).
- `sampleUrl` را با همان هدر `Authorization` دانلود کنید.

`POST /api/v1/templates/{id}/use`:

```json
{"note": "make us smile", "images": [{"base64": "…"}]}
```

- `202` با `{"chat": {...}, "message": {...}}`. هر بار استفاده **یک گفتگوی تازه** به نام قالب می‌سازد، و بعد از تمام شدن می‌توانید در همان گفتگو با `POST /api/v1/chats/{id}/messages` ادامه دهید.
- `note` اختیاری است و حداکثر ۱۰۰۰ نویسه؛ بیشتر: `400 InvalidPrompt`.
- تعداد تصویرها باید بین `minImages` و `maxImages` باشد، وگرنه `409 TemplateInputs`.
- قالبی که نیست یا غیرفعال است: `404`.

## خطاها

بدنه‌ی هر خطا این است:

```json
{"error": "ProjectBusy"}
```

کد (`error`) پایدار است؛ در کد خود روی آن تصمیم بگیرید، نه روی متن یا فقط روی شماره‌ی وضعیت.

| وضعیت | `error` | معنی | چه کنید |
|---|---|---|---|
| 400 | `InvalidRequest` | بدنه JSON معتبر نیست، نوع فیلدی غلط است، یا پارامتری مثل `before` خوانا نیست | درخواست را درست کنید |
| 400 | `InvalidPrompt` | متن خالی یا بیش از ۱۲۰۰۰ نویسه؛ یادداشت قالب بیش از ۱۰۰۰ نویسه؛ یا تصویر بدون متن در ساخت گفتگو | |
| 400 | `InvalidTitle` | نام بیش از ۱۶۰ نویسه، یا گفتگو بدون `title` و بدون `prompt` | |
| 400 | `InvalidProvider` | `provider` جز `ChatGPT` و `Gemini` | |
| 400 | `InvalidImageInput` | عضو `images` هر دو یا هیچ‌کدام از `base64` و `uploadId` را دارد | |
| 400 | `InvalidBase64` | base64 یا data URL خوانا نیست | |
| 400 | `FileTooLarge` | تصویر بیش از ۲۰ مگابایت | کوچکش کنید |
| 400 | `InvalidImage` | بایت‌ها PNG، JPEG یا WebP نیستند | |
| 401 | (بدون بدنه) | توکن نیست، نامعتبر یا باطل است، یا کاربر غیرفعال شده | توکن تازه بسازید |
| 403 | `SessionRequired` | مدیریت توکن با خود توکن | از رابط انجام دهید |
| 404 | `NotFound` | وجود ندارد **یا مال شما نیست** (تفاوتی گذاشته نمی‌شود)؛ یا مسیری که در `v1` نیست | |
| 404 | `NoImage` | پیام تصویری ندارد (هنوز تمام نشده، یا `answered`/`failed` است) | اول `wait` |
| 409 | `ProjectBusy` | پیام قبلی این گفتگو هنوز تمام نشده؛ یا حذف گفتگویی که پیام ناتمام دارد | منتظر پیام قبلی بمانید |
| 409 | `QueueFull` | ۲۵ کار ناتمام در کل کارگاه | کمی بعد دوباره |
| 409 | `QuotaExceeded` | سقف روزانه‌ی شما پر شده | فردا، یا از مالک بخواهید |
| 409 | `InsufficientCredits` | اعتبار شما از هزینه‌ی این پیام کمتر است | از مالک اعتبار بخواهید |
| 409 | `NoAccount` | کارگاه حساب فعالی برای این ارائه‌دهنده ندارد | ارائه‌دهنده‌ی دیگری انتخاب کنید |
| 409 | `TooManyInputs` | بیش از ۴ تصویر | |
| 409 | `TemplateInputs` | تعداد تصویرها با `minImages`..`maxImages` قالب نمی‌خواند | |
| 409 | `NoConversation` / `InvalidParent` | گفتگوی ChatGPT درست در همان لحظه از دسترس خارج شد (نادر) | همان پیام را دوباره بفرستید |
| 415 | `UnsupportedMediaType` | بدنه بدون `Content-Type: application/json` | هدر را اضافه کنید |
| 429 | `RateLimited` | بیش از ۶۰ درخواست در دقیقه با این توکن | به اندازه‌ی `Retry-After` صبر کنید |
| 500 | `ServerError` | خطای داخلی؛ جزئیات فقط در لاگ سرور است | با فاصله دوباره امتحان کنید |

### کدهای `error` در پیام شکست‌خورده

وقتی `status` برابر `failed` است، `error` یکی از این‌هاست:

| کد | معنی |
|---|---|
| `LoginRequired` | حساب ارائه‌دهنده از سیستم خارج شده یا کلید API آن رد شده؛ مالک باید دوباره وصلش کند |
| `VerificationRequired` | ChatGPT تأیید انسانی خواسته؛ مالک باید از میز کار (desktop) رفعش کند |
| `GenerationTimeout` | تصویر در مهلت اجرا (ده دقیقه) آماده نشد |
| `NoImageReturned` | نه تصویری آمد و نه متنی که بشود خواند، یا سهمیه‌ی تصویر آن حساب تمام شده بود و حساب دیگری برای تلاش دوباره نبود |
| `ContentBlocked` | ارائه‌دهنده (Gemini) درخواست را به خاطر سیاست محتوا نپذیرفت؛ متن را عوض کنید. امتناعِ ChatGPT به‌صورت `answered` با متنِ خودش برمی‌گردد |
| `QuotaExceeded` | سقف روزانه‌ی شما پر شده است |
| `NoAccount` | حسابی برای اجرای کار نماند |
| `UploadFailed` | سایت پیوست‌ها را نپذیرفت |
| `InputMissing` | فایل یکی از تصویرهای ورودی دیگر روی دیسک نیست |
| `InvalidImage` | فایلی که ChatGPT داد تصویر معتبری نبود |
| `ConversationNotSaved` | گفتگوی ChatGPT ذخیره نشد |
| `BrowserClosed` | مرورگر وسط کار بسته شد |
| `Interrupted` | سرور وسط کار دوباره راه‌اندازی شد |
| `AutomationFailed` | هر خطای دیگر؛ جزئیات فقط در لاگ سرور |

اعتبارِ پیام شکست‌خورده برمی‌گردد. در بیشتر موارد فرستادن دوباره‌ی همان پیام کافی است.

## محدودیت‌ها

| چه | سقف |
|---|---|
| درخواست با هر توکن | ۶۰ در دقیقه (`429` با `Retry-After`)؛ نشست کوکیِ رابط وب شمرده نمی‌شود |
| توکن فعال برای هر کاربر | ۲۰ |
| متن پیام | ۱۲۰۰۰ نویسه |
| نام گفتگو | ۱۶۰ نویسه |
| یادداشت قالب | ۱۰۰۰ نویسه |
| تصویر در هر پیام | ۴، هر کدام تا ۲۰ مگابایت، PNG/JPEG/WebP |
| حجم بدنه‌ی JSON | حدود ۱۰۸ مگابایت (چهار تصویر ۲۰ مگابایتی به base64) |
| پیام هم‌زمان در هر گفتگو | ۱ (`409 ProjectBusy`) |
| کارهای ناتمام در کل کارگاه | ۲۵ (`409 QueueFull`) |
| `timeout` در `wait` | ۱ تا ۱۲۰ ثانیه، پیش‌فرض ۶۰ |
| `limit` در `GET /api/v1/chats` | ۱ تا ۱۰۰، پیش‌فرض ۲۰ |
| سکوت در جریان SSE | حداکثر ۱۵ ثانیه، بعد `: keep-alive` |
| مدت هر اجرا روی ChatGPT | ده دقیقه، بعد `failed` با `GenerationTimeout` |
| آپلودِ بی‌استفاده | بعد از ۲۴ ساعت پاک می‌شود |
| سقف روزانه و اعتبار | آنچه مالک برای شما تعیین کرده؛ در `GET /api/v1/me` |

## نمونه‌های کامل: curl، Python و JavaScript

هر سه برنامه یک کار را می‌کنند: گفتگویی را با اولین پیام شروع می‌کنند، با `wait` منتظر می‌مانند، تصویر را دانلود می‌کنند، پیام بعدی را در همان گفتگو می‌فرستند، دوباره منتظر می‌مانند و تصویر دوم را دانلود می‌کنند. `429` را با `Retry-After` مدیریت می‌کنند و اگر پیام تصویری نداشته باشد (`answered` یا `failed`) می‌گویند چرا. همین سه برنامه با نشانی واقعی سرور شما در رابط Loomi هم هست: **تنظیمات ← استفاده از Loomi در کد**.

فقط `BASE` را به نشانی Loomi خودتان تغییر دهید و توکن را در `LOOMI_TOKEN` بگذارید.

### curl (bash، با jq)

به `curl` و [`jq`](https://jqlang.github.io/jq/) نیاز دارد. اجرا: `LOOMI_TOKEN=lm_... bash loomi.sh`

```bash
#!/usr/bin/env bash
# Loomi API v1 with curl and jq: start a chat, wait for its picture, download it, then continue the chat.
# Run: LOOMI_TOKEN=lm_... bash loomi.sh
set -euo pipefail
BASE="https://loomi.example.com"
AUTH="Authorization: Bearer $LOOMI_TOKEN"

# call METHOD PATH [JSON]: prints the response body, or stops with the status and the error code.
call() {
  local args=(-sS -m 90 -w '\n%{http_code}' -X "$1" -H "$AUTH") out status
  if [ $# -ge 3 ]; then args+=(-H "Content-Type: application/json" -d "$3"); fi
  # A GET is safe to repeat, so curl may retry it after a timeout or a 429 (it honours Retry-After).
  if [ "$1" = GET ]; then args+=(--retry 5); fi
  out=$(curl "${args[@]}" "$BASE$2") || return 1
  status=${out##*$'\n'}; out=${out%$'\n'*}
  if [ "$status" -ge 400 ]; then echo "HTTP $status $out" >&2; return 1; fi
  printf '%s\n' "$out"
}

# wait_for ID: long-polls until the message is finished, then prints it.
wait_for() {
  local reply
  while :; do
    reply=$(call GET "/api/v1/messages/$1/wait?timeout=60") || return 1
    if [ "$(jq -r .done <<<"$reply")" = true ]; then jq .message <<<"$reply"; return; fi
    echo "still $(jq -r .message.stage <<<"$reply")" >&2
  done
}

# save MESSAGE FILE: downloads a finished message's picture, or says why there is none.
save() {
  if [ "$(jq -r .status <<<"$1")" != completed ]; then echo "no picture: $1" >&2; return 1; fi
  curl -sS --fail --retry 5 -H "$AUTH" -o "$2" "$BASE$(jq -r .imageUrl <<<"$1")"
  echo "saved $2"
}

# 1. Start a chat with its first message.
start=$(call POST /api/v1/chats '{"prompt":"a lighthouse at dusk, film grain"}')
chat=$(jq -r .chat.id <<<"$start")
# 2. Wait for the answer and download the picture.
first=$(wait_for "$(jq -r .message.id <<<"$start")")
save "$first" lighthouse.png
# 3. The next message in the same chat, then wait and download again.
next=$(call POST "/api/v1/chats/$chat/messages" '{"prompt":"the same scene in winter"}')
second=$(wait_for "$(jq -r .message.id <<<"$next")")
save "$second" lighthouse-winter.png
```

### Python (requests)

به `pip install requests` نیاز دارد. اجرا: `LOOMI_TOKEN=lm_... python loomi.py`

```python
"""Loomi API v1 with requests: start a chat, wait for its picture, download it, then continue the chat.
Run: LOOMI_TOKEN=lm_... python loomi.py"""
import os
import time

import requests

BASE = "https://loomi.example.com"
session = requests.Session()
session.headers["Authorization"] = "Bearer " + os.environ["LOOMI_TOKEN"]


def call(method, path, **kwargs):
    """One request. Waits out a 429 as Retry-After asks, and raises with the error code on any other failure."""
    while True:
        response = session.request(method, BASE + path, timeout=90, **kwargs)
        if response.status_code != 429:
            break
        time.sleep(int(response.headers.get("Retry-After", "60")))
    if not response.ok:
        raise RuntimeError(f"HTTP {response.status_code} {response.text}")
    return response


def wait_for(message_id):
    """Long-polls until the message is finished; each call holds for up to a minute."""
    while True:
        try:
            reply = call("GET", f"/api/v1/messages/{message_id}/wait", params={"timeout": 60}).json()
        except (requests.ConnectionError, requests.Timeout):
            time.sleep(5)  # a dropped connection loses nothing: the message keeps going on the server
            continue
        if reply["done"]:
            return reply["message"]
        print("still", reply["message"]["stage"])


def save(message, path):
    """Downloads a finished message's picture, or says why there is none."""
    if message["status"] != "completed":
        raise RuntimeError(f"no picture: {message['status']} {message['error'] or message['text']}")
    with open(path, "wb") as file:
        file.write(call("GET", message["imageUrl"]).content)
    print("saved", path)


# 1. Start a chat with its first message.
started = call("POST", "/api/v1/chats", json={"prompt": "a lighthouse at dusk, film grain"}).json()
# 2. Wait for the answer and download the picture.
save(wait_for(started["message"]["id"]), "lighthouse.png")
# 3. The next message in the same chat, then wait and download again.
sent = call("POST", f"/api/v1/chats/{started['chat']['id']}/messages", json={"prompt": "the same scene in winter"}).json()
save(wait_for(sent["message"]["id"]), "lighthouse-winter.png")
```

### JavaScript (Node 18 یا جدیدتر، fetch)

بدون وابستگی. پسوند فایل `.mjs` باشد تا `await` در سطح بالا کار کند. اجرا: `LOOMI_TOKEN=lm_... node loomi.mjs`

```js
// Loomi API v1 with Node 18+ fetch: start a chat, wait for its picture, download it, then continue the chat.
// Run: LOOMI_TOKEN=lm_... node loomi.mjs
import { writeFile } from 'node:fs/promises';

const BASE = 'https://loomi.example.com';
const AUTH = { Authorization: `Bearer ${process.env.LOOMI_TOKEN}` };
const sleep = seconds => new Promise(resolve => setTimeout(resolve, seconds * 1000));

// One request. Waits out a 429 as Retry-After asks, and throws with the error code on any other failure.
async function call(method, path, body) {
  for (;;) {
    const response = await fetch(BASE + path, {
      method,
      headers: body ? { ...AUTH, 'Content-Type': 'application/json' } : AUTH,
      body: body && JSON.stringify(body),
      signal: AbortSignal.timeout(90_000),
    });
    if (response.status === 429) { await sleep(Number(response.headers.get('Retry-After') ?? 60)); continue; }
    if (!response.ok) throw new Error(`HTTP ${response.status} ${await response.text()}`);
    return response;
  }
}

// Long-polls until the message is finished; each call holds for up to a minute.
async function waitFor(id) {
  for (;;) {
    const { done, message } = await (await call('GET', `/api/v1/messages/${id}/wait?timeout=60`)).json();
    if (done) return message;
    console.log('still', message.stage);
  }
}

// Downloads a finished message's picture, or says why there is none.
async function save(message, file) {
  if (message.status !== 'completed') throw new Error(`no picture: ${message.status} ${message.error ?? message.text}`);
  const image = await call('GET', message.imageUrl);
  await writeFile(file, Buffer.from(await image.arrayBuffer()));
  console.log('saved', file);
}

// 1. Start a chat with its first message.
const started = await (await call('POST', '/api/v1/chats', { prompt: 'a lighthouse at dusk, film grain' })).json();
// 2. Wait for the answer and download the picture.
await save(await waitFor(started.message.id), 'lighthouse.png');
// 3. The next message in the same chat, then wait and download again.
const sent = await (await call('POST', `/api/v1/chats/${started.chat.id}/messages`, { prompt: 'the same scene in winter' })).json();
await save(await waitFor(sent.message.id), 'lighthouse-winter.png');
```

## مسیرهای قدیمی `/api/*`

رابط وب Loomi از مسیرهای `/api/projects`، `/api/generations`، `/api/templates` و مانند آن‌ها استفاده می‌کند. این مسیرها با توکن هم کار می‌کنند و فعلاً باقی هستند، ولی برای رابط ساخته شده‌اند و بی‌خبر تغییر می‌کنند. **`/api/v1` سطح پشتیبانی‌شده است**؛ ابزار تازه را روی آن بسازید و ابزارهای قدیمی را کم‌کم به آن ببرید:

| قدیمی | در v1 |
|---|---|
| `POST /api/projects` و بعد `POST /api/projects/{id}/generate` | `POST /api/v1/chats` با `prompt` |
| `POST /api/generations/{id}/reply`، `/edit`، `/branch` | `POST /api/v1/chats/{id}/messages` (خودش تصمیم می‌گیرد گفتگو را چطور ادامه دهد) |
| پرسیدن دوره‌ای `GET /api/generations/{id}` | `GET /api/v1/messages/{id}/wait` یا `/events` |
| `GET /api/generations/{id}/image` | `GET /api/v1/messages/{id}/image` |
| `POST /api/generations/{id}/cancel` | `POST /api/v1/messages/{id}/cancel` |
| `POST /api/templates/{id}/use` | `POST /api/v1/templates/{id}/use` |
| `GET /api/session` | `GET /api/v1/me` |

این مسیرها هنوز فقط در سطح قدیمی هستند: بارگذاری چندبخشی `POST /api/uploads` (که در `images` با `uploadId` به کار می‌رود)، `GET /api/credits` (موجودی و ۵۰ حرکت آخر دفتر اعتبار)، `GET /api/auth/status` (آیا ارائه‌دهنده‌ای آماده است و اگر نه چرا)، مسیرهای مالک (`/api/users`، `/api/auth/accounts`، `/api/pricing`، مدیریت قالب‌ها) و اتصال تلگرام (`/api/telegram/link`). ساختن توکن و اتصال تلگرام فقط از نشستِ واردشده در رابط ممکن است، نه با توکن.
