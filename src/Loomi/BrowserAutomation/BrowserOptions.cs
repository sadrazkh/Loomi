namespace Loomi.BrowserAutomation;
public class BrowserOptions
{
    public bool Headless { get; set; }
    public string? ExecutablePath { get; set; }
    public int NavigationTimeoutMs { get; set; } = 60000;
    public int GenerationTimeoutSeconds { get; set; } = 600;
    public int StableSeconds { get; set; } = 8;
    public Selectors Selectors { get; set; } = new();
}
public class Selectors
{
    public string Composer { get; set; } = "#prompt-textarea";
    public string LoggedIn { get; set; } = "[data-testid='accounts-profile-button']";
    public string LoggedOut { get; set; } = "[data-testid='login-button']";
    public string Send { get; set; } = "[data-testid='send-button']";
    public string Stop { get; set; } = "[data-testid='stop-button']";
    public string Assistant { get; set; } = "[data-message-author-role='assistant']";
    public string GeneratedImage { get; set; } = "img[alt*='Generated'], img[alt*='generated'], img[alt*='تصویر']";
    public string FileInput { get; set; } = "input[type='file']";
    public string AttachmentMenu { get; set; } = "button[aria-label*='Add photos'], button[aria-label*='Attach'], button[data-testid='composer-plus-btn']";
    public string UploadReady { get; set; } = "button[aria-label*='Remove file'], button[aria-label*='Remove attachment']";
    public string UploadBusy { get; set; } = "[role='progressbar']";
}
