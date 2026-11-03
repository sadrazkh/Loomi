namespace Loomi.Telegram;
public static partial class BotMessages
{
    /// <summary>What the bot says when someone signs up in it: keys here, in both languages.</summary>
    private static readonly Dictionary<string, (string En, string Fa)> SignupTable = new(StringComparer.Ordinal)
    {
        // The welcome: what this is, the gift when there is one, and the plain truth about where the photos go.
        ["signupWelcome"] = ("Welcome to Loomi. Describe the picture you want or send your photo, and AI makes the image for you.",
                             "به Loomi خوش آمدید. تصویری را که می‌خواهید توصیف کنید یا عکستان را بفرستید تا با هوش مصنوعی برایتان ساخته شود."),
        ["signupGift"] = ("Gift images for a new account: {0}.", "{0} تصویر هدیه همراه حساب تازه‌تان است."),
        ["signupPrivacy"] = ("Privacy: your photos go to ChatGPT to make the picture, they stay on our server until you delete the chat, and the owner of the service can see them.",
                             "حریم خصوصی: عکس‌هایتان برای ساخت تصویر به ChatGPT فرستاده می‌شوند، تا وقتی گفتگو را پاک نکنید روی سرور ما می‌مانند و مدیر سرویس هم می‌تواند آن‌ها را ببیند."),
        ["btnSignup"] = ("Start and get my gift", "شروع و دریافت هدیه"),
        ["btnSignupPlain"] = ("Start", "شروع"),
        ["btnHaveAccount"] = ("I already have an account", "قبلاً حساب دارم"),
        ["signupHaveAccount"] = ("If you already have a Loomi account, sign in on the website, open your account page, choose Telegram and press Connect Telegram. Telegram comes back here and this chat is linked.",
                                 "اگر از قبل حساب Loomi دارید، در سایت وارد شوید، صفحه‌ی حساب را باز کنید، «تلگرام» را انتخاب کنید و «اتصال به تلگرام» را بزنید. تلگرام به همین‌جا برمی‌گردد و این گفتگو وصل می‌شود."),
        // After the button.
        ["signupDone"] = ("Your account is ready. Gift images: {0}, good for {1} days. Send a description and I will make the image, or pick a template below and send your photo. Gift images carry a small Loomi mark; images you make with credits do not. /web opens your account on the website.",
                          "حسابتان آماده است، با {0} تصویر هدیه که {1} روز اعتبار دارند. توضیح تصویری را که می‌خواهید بفرستید تا بسازم، یا از دکمه‌های زیر یک قالب انتخاب کنید و عکستان را بفرستید. تصویرهای هدیه یک نشان کوچک Loomi دارند؛ تصویرهایی که با اعتبار بسازید نشانی ندارند. با /web حسابتان را در وب باز می‌کنید."),
        ["signupDonePlain"] = ("Your account is ready. Send a description and I will make the image, or pick a template below and send your photo. /web opens your account on the website.",
                               "حسابتان آماده است. توضیح تصویری را که می‌خواهید بفرستید تا بسازم، یا از دکمه‌های زیر یک قالب انتخاب کنید و عکستان را بفرستید. با /web حسابتان را در وب باز می‌کنید."),
        ["signupBack"] = ("Welcome back: this chat is linked to your account again. Send a description whenever you like.", "خوش برگشتید؛ این گفتگو دوباره به حسابتان وصل شد. هر وقت خواستید توضیح بفرستید."),
        ["signupAlready"] = ("This chat already has an account. Send a description and I will make the image.", "این گفتگو از قبل حساب دارد. توضیح تصویر را بفرستید تا بسازم."),
        ["signupFull"] = ("We are full for today. Please try again tomorrow.", "ظرفیت ثبت‌نام امروز پر شده است. لطفاً فردا دوباره امتحان کنید."),
        ["signupDisabled"] = ("This account has been disabled. Ask the owner of the service.", "این حساب غیرفعال شده است. با مدیر سرویس تماس بگیرید."),
        // Goes to the owner's chats, in both languages, like the other alerts.
        ["signupOwnerNotice"] = ("Loomi: a new account signed up in the bot: {0}.", "Loomi: حساب تازه‌ای در ربات ساخته شد: {0}."),
        // The way into the web.
        ["helpWeb"] = ("/web — a one-time link to your account on the website", "/web — لینک یک‌بارمصرف ورود به حسابتان در وب"),
        ["webLink"] = ("Your way into Loomi on the web. The link works once and lasts {1} minutes:\n{0}\n\nTo come and go without a link, set a password on your account page.",
                       "این لینک ورود شما به Loomi در وب است؛ فقط یک بار کار می‌کند و {1} دقیقه اعتبار دارد:\n{0}\n\nبرای ورود بدون لینک، در صفحه‌ی حساب یک گذرواژه بگذارید."),
        ["webOwner"] = ("The owner signs in on the website with a password. No link is given here, so that a stolen chat can never become owner access.",
                        "مالک با گذرواژه در سایت وارد می‌شود. این‌جا لینکی داده نمی‌شود تا یک گفتگوی دزدیده‌شده هرگز به دسترسی مالک نرسد."),
        ["webNoAddress"] = ("The web address of this service is not set. Ask the owner of the service.", "نشانی وب این سرویس تنظیم نشده است. با مدیر سرویس تماس بگیرید."),
        ["webPrivateOnly"] = ("Send /web in your private chat with me: the link signs in whoever opens it.", "فرمان /web را در گفتگوی خصوصی با من بفرستید؛ این لینک هر کسی را که بازش کند وارد می‌کند."),
        // Added under "not enough credits" for an account made here, once the owner has said where to buy.
        ["signupWhereToBuy"] = ("To buy more: {0}", "برای خرید: {0}"),
    };
}
