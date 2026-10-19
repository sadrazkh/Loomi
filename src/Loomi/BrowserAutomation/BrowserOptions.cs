namespace Loomi.BrowserAutomation;
public class BrowserOptions
{
    public bool Headless { get; set; }
    public string? ExecutablePath { get; set; }
    /// <summary>Installed browsers to fall back to, in order, when Playwright's own Chromium build cannot start on this host.</summary>
    public string[] Channels { get; set; } = ["chrome", "msedge"];
    /// <summary>Default switches to drop. The automation banner makes sites treat the owner's own manual login as a bot.</summary>
    public string[] IgnoreDefaultArgs { get; set; } = ["--enable-automation"];
    public string[] Args { get; set; } = ["--disable-dev-shm-usage", "--disable-blink-features=AutomationControlled"];
    /// <summary>Loopback port of the noVNC service. Only Docker runs one; a direct run has no remote desktop.</summary>
    public int DesktopPort { get; set; } = 6080;
    public int NavigationTimeoutMs { get; set; } = 60000;
    /// <summary>How long a click, a fill or a wait for one control may take when nothing more specific applies.</summary>
    public int ActionTimeoutMs { get; set; } = 20000;
    /// <summary>How long reopening a conversation may take, from its address to its message box. On the live site a full load of any conversation took
    /// 35 to 55 seconds (the home page 8 to 14), the site fetching the whole thread before it shows the box: with only the 20 seconds every other
    /// control gets, every follow-up in a chat failed while every first message worked. Nothing has been sent while this runs, so waiting costs no more
    /// than time.</summary>
    public int ConversationOpenSeconds { get; set; } = 150;
    /// <summary>How long one look at the page may take while waiting for an answer before the tab is taken for hung and reopened. A tab the kernel
    /// killed for memory never answered at all, and held its account for good.</summary>
    public int HungSeconds { get; set; } = 90;
    /// <summary>How long to let the site render before judging the connection state.</summary>
    public int SettleTimeoutMs { get; set; } = 15000;
    public int GenerationTimeoutSeconds { get; set; } = 600;
    public int StableSeconds { get; set; } = 8;
    /// <summary>How many reference images one generation may carry, and how many files one upload request may hold.</summary>
    public int MaxInputs { get; set; } = 4;
    /// <summary>How long the site may take to show every attachment as ready before the run gives up on it.</summary>
    public int UploadTimeoutMs { get; set; } = 60000;
    /// <summary>Phrases the owner has seen the site use when an account's image allowance is spent. A reply containing one moves the work to another
    /// account, as before; any other reply without a picture is returned to the person as an answer. Empty by default: guessing the site's wording would
    /// turn somebody's genuine question into a silent retry elsewhere, which is the very thing this exists to stop.</summary>
    public string[] SpentPhrases { get; set; } = [];
    /// <summary>How long a turn may sit with nothing on the page — no reply, no picture, no stop control, nothing in progress — before it is given up on.
    /// Only reached when the reply and progress hooks have both drifted; it bounds the wait instead of letting it run to the full timeout.</summary>
    public int QuietSeconds { get; set; } = 45;
    /// <summary>How long an opened conversation may take to show its earlier turns before the run goes on regardless.</summary>
    public int ConversationLoadSeconds { get; set; } = 10;
    /// <summary>Bring every enabled account up when the server starts. A deployment restarts on every update, and without this each restart left the
    /// accounts disconnected, with the UI telling everyone to wait, until the owner came back and pressed Connect.</summary>
    public bool ConnectOnStart { get; set; } = true;
    public Selectors Selectors { get; set; } = new();
}
public class Selectors
{
    /// <summary>The site moved from a textarea with an id to a ProseMirror contenteditable labelled in aria; both forms are listed so either DOM works.</summary>
    public string Composer { get; set; } = "#prompt-textarea, div[contenteditable='true'][aria-label='Ask ChatGPT'], form div[contenteditable='true'], div.ProseMirror[contenteditable='true']";
    /// <summary>ChatGPT dropped nearly every data-testid in favour of aria labels; the old hook is kept in case it returns.</summary>
    public string LoggedIn { get; set; } = "[data-testid='accounts-profile-button'], button[aria-label='Open profile menu'], button[aria-label*='profile menu']";
    public string LoggedOut { get; set; } = "[data-testid='login-button'], [data-testid='mobile-login-button'], a[href*='/auth/login'], button[data-testid='signup-button']";
    /// <summary>Bot-verification interstitial. Only the owner can clear it, so it must not be reported as a missing login.</summary>
    public string Challenge { get; set; } = "#challenge-form, #challenge-running, #cf-chl-widget, iframe[src*='challenges.cloudflare.com']";
    public string Send { get; set; } = "[data-testid='send-button'], button[aria-label='Send prompt'], button[aria-label^='Send']";
    public string Stop { get; set; } = "[data-testid='stop-button'], button[aria-label='Stop streaming'], button[aria-label^='Stop streaming']";
    /// <summary>A reply in words. The site marks it only by its markdown style now; the author marker it used to carry is kept in case it returns.</summary>
    public string Assistant { get; set; } = "[data-markdown-text-style='assistant-message'], [data-message-author-role='assistant']";
    /// <summary>Signs that a picture is on its way. Measured on the live site: the stop control goes several seconds before the picture lands, and in that
    /// gap only these say the turn is not over. Counted against what was there before sending, since a continued conversation already holds some.</summary>
    /// <summary>The site's way of asking before it draws: the composer is replaced by a form holding the question and a plain text field to answer in.
    /// Recognised by that shape rather than by its wording, which follows the account's language.</summary>
    public string QuestionForm { get; set; } = "form:has(input[type='text']), form:has(input:not([type]))";
    /// <summary>The answer field inside that form.</summary>
    public string QuestionInput { get; set; } = "input[type='text'], input:not([type])";
    public string ImagePending { get; set; } = "[data-testid^='image-gen-loading'], [data-testid='generated-image-gallery'], [aria-label*='Generating image'], button[aria-label='Like this image']";
    /// <summary>The site labels a finished picture and also wraps it in a gallery of its own; either hook finds it, and the alt forms survive a rename of the gallery.</summary>
    public string GeneratedImage { get; set; } = "[data-testid='generated-image-gallery'] img, button[data-testid='generated-image-preview'] img, img[alt*='Generated'], img[alt*='generated'], img[alt*='تصویر']";
    public string FileInput { get; set; } = "input[type='file']";
    /// <summary>Only needed when the composer renders no file input until its menu opens; the live site currently has the input in the page already.</summary>
    public string AttachmentMenu { get; set; } = "button[aria-label*='Add files'], button[aria-label*='Add photos'], button[aria-label*='Attach'], button[data-testid='composer-plus-btn']";
    /// <summary>One per attachment the composer has accepted. The site labels them "Remove &lt;filename&gt;" now, so the match is on the verb.</summary>
    public string UploadReady { get; set; } = "button[aria-label^='Remove'], button[aria-label*='Remove file'], button[aria-label*='Remove attachment']";
    public string UploadBusy { get; set; } = "[role='progressbar']";
}
