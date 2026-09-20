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
    /// <summary>How long to let the site render before judging the connection state.</summary>
    public int SettleTimeoutMs { get; set; } = 15000;
    public int GenerationTimeoutSeconds { get; set; } = 600;
    public int StableSeconds { get; set; } = 8;
    /// <summary>How many reference images one generation may carry, and how many files one upload request may hold.</summary>
    public int MaxInputs { get; set; } = 4;
    /// <summary>How long the site may take to show every attachment as ready before the run gives up on it.</summary>
    public int UploadTimeoutMs { get; set; } = 60000;
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
    public string Assistant { get; set; } = "[data-message-author-role='assistant']";
    /// <summary>The site labels a finished picture and also wraps it in a gallery of its own; either hook finds it, and the alt forms survive a rename of the gallery.</summary>
    public string GeneratedImage { get; set; } = "[data-testid='generated-image-gallery'] img, button[data-testid='generated-image-preview'] img, img[alt*='Generated'], img[alt*='generated'], img[alt*='تصویر']";
    public string FileInput { get; set; } = "input[type='file']";
    /// <summary>Only needed when the composer renders no file input until its menu opens; the live site currently has the input in the page already.</summary>
    public string AttachmentMenu { get; set; } = "button[aria-label*='Add files'], button[aria-label*='Add photos'], button[aria-label*='Attach'], button[data-testid='composer-plus-btn']";
    public string UploadReady { get; set; } = "button[aria-label*='Remove file'], button[aria-label*='Remove attachment']";
    public string UploadBusy { get; set; } = "[role='progressbar']";
}
