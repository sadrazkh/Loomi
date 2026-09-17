namespace Loomi.Telegram;
/// <summary>What the bot says, in both languages. It cannot share the Vue table: that one is a browser bundle, and nothing on the server can read it.
/// Failure keys are the safe error codes themselves, so a code from anywhere in the server turns into a sentence without a second mapping to keep in step.</summary>
public static class BotMessages
{
    /// <summary>Telegram sends a full tag such as "fa-IR"; the language is the part before the region.</summary>
    public static bool IsPersian(string? languageCode) => languageCode?.StartsWith("fa", StringComparison.OrdinalIgnoreCase) == true;
    public static string Text(string key, bool persian, params object[] args)
    {
        var (en, fa) = Table.TryGetValue(key, out var found) ? found : Table["AutomationFailed"];
        return args.Length == 0 ? (persian ? fa : en) : string.Format(persian ? fa : en, args);
    }
    private static readonly Dictionary<string, (string En, string Fa)> Table = new(StringComparer.Ordinal)
    {
        ["help"] = ("Send me a description and I will make the image. A photo with a caption uses that photo as a reference, and several photos sent together become one request.\n\n/balance — your credits\n/status — what is running\n/cancel — stop your unfinished work",
                    "توضیح تصویری را که می‌خواهید بفرستید تا بسازم. عکس همراه با شرح، همان عکس را به‌عنوان مرجع می‌گیرد و چند عکس که با هم فرستاده شوند یک درخواست می‌شوند.\n\n/balance — اعتبار شما\n/status — کارهای در جریان\n/cancel — لغو کارهای ناتمام شما"),
        ["linkFirst"] = ("This chat is not linked to a Loomi account. Open Loomi, go to Settings → Telegram, ask for a code, and send it here as: /start CODE",
                         "این گفت‌وگو به هیچ حساب Loomi وصل نیست. در Loomi به تنظیمات ← تلگرام بروید، یک کد بگیرید و این‌جا بفرستید: /start CODE"),
        ["linked"] = ("Linked to {0}. Send a description and I will make the image.", "به {0} وصل شد. توضیح تصویر را بفرستید تا بسازم."),
        ["linkUnknown"] = ("That code is not one of ours. Ask for a new one in Settings → Telegram.", "این کد از ما نیست. در تنظیمات ← تلگرام کد تازه بگیرید."),
        ["linkExpired"] = ("That code has expired. Codes last ten minutes; ask for a new one in Settings → Telegram.", "این کد منقضی شده است. هر کد ده دقیقه اعتبار دارد؛ در تنظیمات ← تلگرام کد تازه بگیرید."),
        ["linkChatTaken"] = ("This chat is already linked to a Loomi account. Unlink it there first.", "این گفت‌وگو از قبل به یک حساب Loomi وصل است. اول از همان‌جا قطعش کنید."),
        ["linkUserTaken"] = ("That account is already linked to another chat. Unlink it in Settings → Telegram first.", "آن حساب به گفت‌وگوی دیگری وصل است. اول در تنظیمات ← تلگرام قطعش کنید."),
        ["queued"] = ("Queued. I will send the image here when it is ready.", "در صف قرار گرفت. وقتی آماده شد تصویر را همین‌جا می‌فرستم."),
        ["balance"] = ("Credits: {0}. Generations today: {1}.", "اعتبار: {0}. تولیدهای امروز: {1}."),
        ["balanceOwner"] = ("You are the owner, so your work is not charged. Generations today: {0}.", "شما مالک هستید و کارتان شارژ نمی‌شود. تولیدهای امروز: {0}."),
        ["statusBusy"] = ("{0} unfinished: {1}.", "{0} کار ناتمام: {1}."),
        ["statusIdle"] = ("Nothing of yours is running.", "هیچ کاری از شما در جریان نیست."),
        ["cancelled"] = ("Cancelled {0}.", "{0} کار لغو شد."),
        ["nothingToCancel"] = ("You have nothing unfinished to cancel.", "کار ناتمامی برای لغو ندارید."),
        ["needCaption"] = ("Send the photo with a caption saying what to make from it.", "عکس را همراه با شرحی از آنچه می‌خواهید از آن ساخته شود بفرستید."),
        ["photoRejected"] = ("I could not use that photo. Send a PNG, JPEG or WebP under 20 MB.", "نتوانستم از آن عکس استفاده کنم. یک PNG، JPEG یا WebP کمتر از ۲۰ مگابایت بفرستید."),
        ["InvalidPrompt"] = ("The description is empty or longer than 12000 characters.", "توضیح خالی است یا بیش از ۱۲۰۰۰ نویسه دارد."),
        ["TooManyInputs"] = ("Too many reference images: {0} at most, and I take the first ones.", "تصویر مرجع بیش از حد است: حداکثر {0} تا، و اولی‌ها را برمی‌دارم."),
        ["QueueFull"] = ("The queue is full. Try again in a few minutes.", "صف پر است. چند دقیقه دیگر دوباره بفرستید."),
        ["QuotaExceeded"] = ("Your daily limit is spent. It resets at midnight.", "سهمیه روزانه شما تمام شده است. نیمه‌شب از نو شروع می‌شود."),
        ["InsufficientCredits"] = ("You do not have enough credits for this.", "اعتبار شما برای این کار کافی نیست."),
        ["NoAccount"] = ("No provider account is ready right now.", "هیچ حساب ارائه‌دهنده‌ای اکنون آماده نیست."),
        ["InvalidInput"] = ("One of the reference images cannot be used.", "یکی از تصویرهای مرجع قابل استفاده نیست."),
        ["InvalidParent"] = ("One of the reference images cannot be used.", "یکی از تصویرهای مرجع قابل استفاده نیست."),
        ["ProjectBusy"] = ("That project still has work running.", "این پروژه هنوز کاری در حال اجرا دارد."),
        ["LoginRequired"] = ("The provider account has to be signed in again; only the owner can do that.", "حساب ارائه‌دهنده باید دوباره وارد شود؛ فقط مالک می‌تواند این کار را بکند."),
        ["VerificationRequired"] = ("The provider is asking for a human check; only the owner can clear it.", "ارائه‌دهنده بررسی انسانی می‌خواهد؛ فقط مالک می‌تواند آن را رد کند."),
        ["GenerationTimeout"] = ("It took too long and was given up on. Your credits are back.", "بیش از حد طول کشید و رها شد. اعتبارتان برگشت."),
        ["InvalidImage"] = ("What came back was not a usable image. Your credits are back.", "آنچه برگشت تصویر قابل استفاده‌ای نبود. اعتبارتان برگشت."),
        ["ConversationNotSaved"] = ("The conversation could not be saved. Your credits are back.", "گفت‌وگو ذخیره نشد. اعتبارتان برگشت."),
        ["NoImageReturned"] = ("No image came back — the provider account's image allowance looks spent. Your credits are back.", "تصویری برنگشت — به نظر می‌رسد سهمیه تصویر حساب ارائه‌دهنده تمام شده است. اعتبارتان برگشت."),
        ["ContentBlocked"] = ("The provider refused that request.", "ارائه‌دهنده این درخواست را نپذیرفت."),
        ["UploadFailed"] = ("The reference images could not be attached. Your credits are back.", "تصویرهای مرجع پیوست نشدند. اعتبارتان برگشت."),
        ["InputMissing"] = ("A reference image is no longer on the server. Your credits are back.", "یکی از تصویرهای مرجع دیگر روی سرور نیست. اعتبارتان برگشت."),
        ["BrowserClosed"] = ("The browser window closed while it was running. Your credits are back.", "پنجره مرورگر وسط کار بسته شد. اعتبارتان برگشت."),
        ["Interrupted"] = ("The server restarted while it was running. Your credits are back.", "سرور وسط کار دوباره راه‌اندازی شد. اعتبارتان برگشت."),
        ["Cancelled"] = ("Cancelled.", "لغو شد."),
        ["NotFound"] = ("I could not find that.", "آن را پیدا نکردم."),
        ["AutomationFailed"] = ("That did not work. Try again in a moment.", "انجام نشد. کمی بعد دوباره امتحان کنید."),
    };
}
