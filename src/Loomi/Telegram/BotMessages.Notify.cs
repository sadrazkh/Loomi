namespace Loomi.Telegram;
public static partial class BotMessages
{
    /// <summary>The help text with turning the news off added under it. Help is what a person reads when they look for how to make this stop.</summary>
    public static string Help(bool persian) => Text("help", persian) + "\n\n" + Text("helpNotify", persian);
    /// <summary>The news the bot sends on its own, and turning it off: keys here, in both languages.</summary>
    private static readonly Dictionary<string, (string En, string Fa)> NotifyTable = new(StringComparer.Ordinal)
    {
        // The news itself, written once for each language and sent with both, Persian first. The picture above a digest is the sample of the template it names.
        ["notifyNewTemplate"] = ("New template: “{0}”\nThe picture is its sample. If you like it, give it a try.",
                                 "قالب تازه: «{0}»\nاین عکس نمونه‌ی همین قالب است. اگر خوشتان آمد امتحانش کنید."),
        ["notifyNewTemplates"] = ("New template: “{0}”, and {1} more\nThe picture is a sample of “{0}”. If you like it, give it a try; the others are under /templates.",
                                  "قالب تازه: «{0}» و {1} قالب دیگر\nاین عکس نمونه‌ی «{0}» است. اگر خوشتان آمد امتحانش کنید؛ بقیه هم در /templates هستند."),
        ["notifyWinBack"] = ("It has been a while. Whenever you feel like it, send a photo or a few words and I will make you an image. The button below opens the ready-made templates too.",
                             "مدتی است ندیدیمتان. هر وقت دلتان خواست، یک عکس یا چند کلمه بفرستید تا برایتان تصویر بسازم. قالب‌های آماده هم با دکمه‌ی زیر باز می‌شوند."),
        ["notifyLowCredit"] = ("Your credits are nearly gone: {0} left, and an image takes {1}.", "اعتبارتان رو به پایان است: {0} اعتبار مانده و هر تصویر {1} اعتبار می‌خواهد."),
        ["notifyBuyAt"] = ("To buy more: {0}", "برای خرید اعتبار: {0}"),
        ["notifyBtnTry"] = ("Try it", "امتحانش کن"),
        ["notifyBtnTemplates"] = ("Templates", "قالب‌ها"),
        ["notifyBtnOff"] = ("Turn off news", "خاموش کردن اعلان‌ها"),
        ["notifyBtnOn"] = ("Turn news back on", "روشن کردن اعلان‌ها"),
        // Answers to /notify and to the buttons. Whoever turns the news off is told what still comes, so they do not fear losing their own images.
        ["notifyNowOff"] = ("News is off. I will not tell you about new templates or low credits any more. Your images, package reminders and /balance carry on as before. /notify turns it back on.",
                            "اعلان‌ها خاموش شد. دیگر از قالب‌های تازه یا کم شدن اعتبارتان خبر نمی‌دهم. تصویرهایتان، یادآوری بسته‌ها و /balance مثل قبل سر جایشان هستند. با /notify دوباره روشن می‌شود."),
        ["notifyNowOn"] = ("News is on. Now and then I will tell you about new templates or when your credits run low, and never late at night. /notify turns it off.",
                           "اعلان‌ها روشن شد. گاهی از قالب‌های تازه یا کم شدن اعتبارتان خبرتان می‌کنم، آن هم نه آخر شب. با /notify خاموش می‌شود."),
        ["notifyOwner"] = ("You are the owner, so this news does not come to you; it goes to members. Its switches are on the Bot page.",
                           "شما مالک هستید و این اعلان‌ها برای شما نمی‌آید؛ برای کاربرها فرستاده می‌شود. تنظیماتش در صفحه‌ی ربات است."),
        ["helpNotify"] = ("/notify — turn the bot's news on or off", "/notify — روشن یا خاموش کردن اعلان‌های ربات"),
        ["cmdNotify"] = ("Turn the bot's news on or off", "روشن یا خاموش کردن اعلان‌های ربات"),
    };
}
