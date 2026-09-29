# Owner Usage Report Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the owner see each user's usage, both figures and requests, on a web page and in the Telegram bot, and send the owner a nightly summary.

**Architecture:** A single `UsageReport` service counts from `Generations` and `CreditEntries` on demand. An owner-only `UsageController` exposes it to the web page (`UsageView.vue`), and the bot reads it directly (`UsageBot` plus `DailyReportWorker`). A new `Generation.Channel` column records whether a request came from the web, the bot or the API.

**Tech Stack:** ASP.NET Core 10, EF Core 10 on SQLite, Vue 3 with Vite, Telegram.Bot 19, xUnit.

Spec: `docs/superpowers/specs/2026-09-29-owner-usage-report-design.md`

## Global Constraints

- Enums are stored as ints. New members go at the end only.
- Migrations are made with a fresh build, not `--no-build`. `dotnet ef migrations has-pending-model-changes --project src/Loomi` must report no changes.
- Every UI string goes in both `en` and `fa`. Persian text uses Persian digits in the bot (`BotMessages.Number`).
- Never log prompt text, exception text or tokens.
- Commits go in the owner's name only, with no Co-Authored-By trailer.
- Do not run `npm run build` while `dotnet test` or `dotnet build` is running. Only the orchestrator runs the Vite build.
- After any test run, check the `Total:` count, because a partial run can still print "Passed".

---

### Task 1: Channel on every request, and a Tehran day

**Files:**
- Modify: `src/Loomi/Models/Entities.cs` (add `enum Channel`, `Generation.Channel`)
- Modify: `src/Loomi/Security/Viewer.cs` (`Channel` member; `From` → Api for a token, else Web; `For` → Bot)
- Modify: `src/Loomi/Services/GenerationService.cs` (stamp `Channel = viewer.Channel` where the `Generation` is created)
- Create: migration `GenerationChannel` in `src/Loomi/Data/Migrations`, with SQL backfill `UPDATE Generations SET Channel = 1 WHERE Id IN (SELECT GenerationId FROM BotRequests)`
- Modify: `Dockerfile` (add `ENV TZ=Asia/Tehran` after the heavy RUN layers, next to HEALTHCHECK)
- Test: `tests/Loomi.Tests/UsageTests.cs`

**Interfaces:**
- Produces:
  - `public enum Channel { Web, Bot, Api }`
  - `Generation.Channel`
  - `Viewer(Guid Id, bool IsOwner, Channel Channel = Channel.Web)`

- [ ] Write failing tests: a web session creates a `Web` row, a token creates an `Api` row, and the bot creates a `Bot` row.
- [ ] Implement, add the migration, run the tests and `has-pending-model-changes`, then commit.

### Task 2: UsageReport service and owner API

**Files:**
- Create: `src/Loomi/Services/UsageReport.cs`
- Create: `src/Loomi/Controllers/UsageController.cs`
- Modify: `src/Loomi/Program.cs` (register `UsageReport` as scoped; bind `ReportOptions` from `Reports`)
- Test: `tests/Loomi.Tests/UsageTests.cs`

**Interfaces (produced, consumed by Tasks 3 and 4):**

```csharp
namespace Loomi.Services;
public enum UsagePeriod { Today, Week, Month, All }
public record TemplateCount(string Title, int Count);
public record UserUsage(Guid UserId, string Username, UserRole Role, bool IsDisabled, int Requests, int Images, int Answers, int Failed, int Cancelled,
    int NewChats, int FollowUps, int Web, int Bot, int Api, int CreditsSpent, int? CreditsLeft, int UsedToday, int DailyQuota, DateTime? LastActive,
    IReadOnlyList<TemplateCount> Templates);
public record UsageSummary(UsagePeriod Period, DateTime? Since, int ActiveUsers, int Requests, int Images, int Failed, int CreditsSpent, IReadOnlyList<UserUsage> Users);
public record UsageItem(Guid Id, Guid ProjectId, string ProjectTitle, DateTime CreatedAt, string Prompt, Operation Operation, RunStatus Status, Channel Channel,
    string? TemplateTitle, int CreditCost, bool HasImage, string? ReplyText, string? ErrorMessage);
public class ReportOptions { public int DailyHour { get; set; } = 23; }
public class UsageReport(AppDbContext db)
{
    public const string DailyKey = "Reports.Daily", DailySentKey = "Reports.DailySentOn";
    public static DateTime? Since(UsagePeriod period);                  // UTC instant of the period's local start; null for All
    public static bool TryParse(string? value, out UsagePeriod period);  // "today" | "week" | "month" | "all", case-insensitive; null → Today
    public Task<UsageSummary> SummaryAsync(UsagePeriod period, CancellationToken ct);
    public Task<UserUsage?> UserAsync(Guid user, UsagePeriod period, CancellationToken ct);
    public Task<IReadOnlyList<UsageItem>> RequestsAsync(Guid user, int skip, int take, CancellationToken ct);
    public Task<bool> DailyEnabledAsync(CancellationToken ct);           // true unless Settings[DailyKey] == "off"
    public Task SetDailyEnabledAsync(bool enabled, CancellationToken ct);
}
```

- **HTTP (camelCase JSON; enums serialize as strings, as the rest of the API does):**
  - `GET /api/usage?period=`
  - `GET /api/usage/users/{id}?period=`
  - `GET /api/usage/users/{id}/requests?skip=&take=`
  - `GET /api/usage/daily-report` returns `{ enabled, hour, telegram }`, where `telegram` is true when a bot is configured.
  - `PUT /api/usage/daily-report` takes `{ enabled }`.
  - An unknown `period` gets 400 `{ error: "InvalidPeriod" }`.
  - A member gets 403.
- Images come from the existing `GET /api/generations/{id}/image`; the owner already passes `OwnedBy`.

- [ ] Write failing tests covering:
  - counts by status, operation and channel
  - Today excluding yesterday while Week includes it
  - credits spent net of refunds
  - owners reported with `CreditsLeft == null`
  - users with no usage listed with 0
  - requests newest first, with `take` capped
  - members getting 403
- [ ] Implement, run the tests and commit.

### Task 3: Bot — `/users`, user details, latest images, nightly report (agent, own worktree)

**Files:**
- Create: `src/Loomi/Telegram/UsageBot.cs` (renders and sends; stateless apart from its dependencies)
- Create: `src/Loomi/Telegram/DailyReportWorker.cs` (a `BackgroundService` that checks every minute; public `RunAsync(DateTime localNow, CancellationToken)` for tests)
- Modify: `src/Loomi/Telegram/TelegramBotWorker.cs`:
  - `case "/users"` for owners only, otherwise help
  - callbacks `usage:<t|w|m|a>`, `usr:<t|w|m|a><guidN>` and `uimg:<guidN>`, for owners only
- Modify: `src/Loomi/Telegram/BotMessages.cs` (new keys, both languages)
- Modify: `src/Loomi/Program.cs` (register `DailyReportWorker`)
- Test: `tests/Loomi.Tests/UsageBotTests.cs`

**Behaviour:**
- **Summary text:** a period title, then active users, requests, images, failed and credits spent. After that, up to 10 lines of `username — N requests, M images`. The buttons are:
  - one row with the four periods (the current one marked with •)
  - one button per active user (at most 10), `usr:<p><id:N>`
- **User text:**
  - the username with the role, and the period
  - requests broken down into images, answers, failed and cancelled
  - new chats against follow-ups
  - web, bot and API counts
  - credits spent and left (left is omitted for owners)
  - today's usage over the quota, with ∞ when the quota is 0
  - the top templates
  - last active, in local time as `yyyy/MM/dd HH:mm`, using the `fa-IR` culture in Persian
  - the last 5 requests, each as `• HH:mm <icon> «prompt≤60»`, where the icon is ✅ Completed, 💬 Answered, ❌ Failed, ⏹ Cancelled or ⏳ otherwise
  - buttons `uimg:<id>` ("🖼 latest images") and `usage:<p>` ("↩️ back")
- **Latest images:** the user's last 5 `Completed` generations that have an image, oldest first. Each is sent with `SendPhotoAsync` and a caption of the date and the prompt cut to 200 characters. With none, the bot says so.
- **Nightly report:** see the spec. It uses `ReportOptions.DailyHour` and `DateTime.Now`. `RunAsync(localNow)`:
  - returns if the hour is before `DailyHour`, the report is off, the date is already sent, or no bot is configured
  - builds the Today summary, and sends it to every owner chat when it has at least one request (as `OwnerAlertWorker.SendAsync` does)
  - records the date either way
- **Tests:**
  - `/users` from an owner-linked chat sends the summary; from a member it sends help.
  - The user button sends details.
  - The images button sends photos.
  - The nightly report is sent once, skipped on a quiet day, and not sent when off.

### Task 4: Web — Usage page (agent, own worktree)

**Files:**
- Create: `src/Loomi/Client/components/UsageView.vue`
- Create: `src/Loomi/Client/i18n-usage.js` (merged in `i18n.js` the way `i18n-api.js` is)
- Modify: `src/Loomi/Client/App.vue` (owner-only nav item "Usage" with the `BarChart3` icon; `view==='usage'` renders `<UsageView :t :locale :api :action :busy/>`)
- Modify: `src/Loomi/Client/style.css` (only if the existing classes cannot express it)

**Behaviour:** as in the spec's Web section.
- The period switch reuses the `segmented` style.
- There are four stat tiles.
- The users table is sortable by requests. Clicking a row opens a detail panel with the figures and the request list, 20 per page with "more" from `/requests?skip=`. Thumbnails come from `/api/generations/{id}/image`; clicking one opens the full image in the existing `Modal`.
- The daily report toggle calls `PUT /api/usage/daily-report`. When `telegram` is false, it shows a hint to set the bot up under Settings.

### Task 5: Merge, verify, deploy

- [ ] Merge the Task 3 and Task 4 branches and check each with `git merge-base --is-ancestor`.
- [ ] Run `npm run build` in `src/Loomi/Client`, and only then `dotnet test tests/Loomi.Tests`. The full suite must pass, with the Total above 284.
- [ ] Run `dotnet ef migrations has-pending-model-changes --project src/Loomi`.
- [ ] Commit, then `harbora deploy loomi -y`. Check that the container is healthy, the migration was applied, `date` in the container shows Tehran time, and `/api/usage` answers 401 without signing in.
