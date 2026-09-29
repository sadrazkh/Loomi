# Owner usage report — design

Approved by the owner on 2026-09-29.

## Goal

The owner sees who uses Loomi, how much, and for what, both on a web page and in the Telegram bot. The owner also receives a short nightly summary in Telegram.

## Decisions

- **Detail level: figures plus the request list.** The owner sees each user's figures and each request: the text sent, the result image, the status, the channel, the template and the credits. The owner could already open any generation (`Ownership.OwnedBy` does not narrow an owner), so no new image route is needed.
- **Bot: an `/users` command plus a nightly report.** The report is sent at 23:00 local time and only when the day had activity. It can be switched off from the web page.
- **Computed on demand.** Everything is counted from `Generations` and `CreditEntries` at read time, the way `Quota` already works. There are no stored counters.
- **Periods:** today, the last 7 days, the last 30 days, all time. Each one starts at local midnight; 7 days means today and the six days before it.
- **Local day is Tehran.** The container runs with `TZ=Asia/Tehran` (the ENV line goes after the heavy layers of the Dockerfile). `DateTime.Today` therefore means the day the users are living in. This covers the daily quota, which until now reset at 03:30 Tehran time, the reports and the nightly hour.

## Data

- A new `enum Channel { Web, Bot, Api }` is stored as an int, and `Generation.Channel` is added.
- `Viewer` gains a `Channel` member:
  - `Viewer.From(principal)` gives `Api` when the request was authenticated by a personal token, otherwise `Web`.
  - `Viewer.For(user)` is used only by the bot and gives `Bot`.
- `GenerationService.SubmitAsync` stamps `generation.Channel = viewer.Channel`.
- Migration `GenerationChannel` adds the column with a default of 0 (Web). It then backfills `Bot` for every generation listed in `BotRequests`. Older API use cannot be told apart and stays `Web`.

## Figures per user, for a period

- **Requests:** every generation created in the period, in any status.
- **Outcomes:**
  - Images: `Completed`
  - Answers: `Answered`
  - Failed: `Failed`
  - Cancelled: `Cancelled`
- **Kind of request:**
  - New chats: `Generate` or `Branch`
  - Follow-ups: `Reply` or `Edit`
- **Channels:** counts of web, bot and API.
- **Credits:**
  - Spent is net: minus the sum of `Charge` and `Refund` entries in the period.
  - Left is the balance, and is null for owners, who are never charged.
- **Today:** used today, plus the daily quota (0 means no limit).
- **Templates:** the three most used in the period.
- **Last active:** the newest generation of all time, or null.

The overall summary for a period lists the users sorted by requests and then by last activity, including those with no usage. It also reports active users, total requests, images, failures and credits spent.

## Web (owner only)

- A **"Usage"** navigation item leads to a period switch, four tiles and a users table.
- **Tiles:** active users, requests (with the failure share), images, credits spent.
- **Table columns:** user, requests, images, failed, follow-ups, credits spent, channels, last active.
- **Selecting a user** opens a panel with their figures and a request list, newest first, 20 at a time with "more". Each entry shows the time, the prompt, a thumbnail that enlarges when clicked, the status, the channel, the template and the cost.
- **The nightly report toggle** lives on the same page.

## Bot (owner only)

- **`/users`** shows the period summary and the top users. It has buttons for the four periods and one button per active user (at most 10). A member who sends `/users` gets the help text, as with any unknown command.
- **A user button** shows that user's figures and their last 5 requests, each as a status icon, the time and the prompt cut to 60 characters. It has a "latest images" button (up to 5 photos) and a "back" button.
- **The nightly report** is sent by `DailyReportWorker` to every linked, enabled owner:
  - It runs at or after `Reports:DailyHour` (default 23) local time.
  - It is sent once per local day, and the date is stored in `Settings["Reports.DailySentOn"]`.
  - It is skipped when the day had no requests, but the date is still recorded.
  - `Settings["Reports.Daily"] == "off"` disables it.

## API (owner only, `[Authorize(Roles = Owner)]`)

- `GET /api/usage?period=today|week|month|all` returns `UsageSummary`.
- `GET /api/usage/users/{id}?period=` returns `UserUsage`, or 404.
- `GET /api/usage/users/{id}/requests?skip=0&take=20` returns `UsageItem[]`, with `take` capped at 50.
- `GET /api/usage/daily-report` and `PUT /api/usage/daily-report` carry `{ enabled, hour, telegram }`.

## Security

Members get 403 on every route above, and the bot ignores `/users` from them. The request list shows prompt text to the owner only. Nothing new is logged.

## Testing

- **Counts:** by status, operation and channel, and the period edges.
- **Credits:** spent is net of refunds.
- **Access:** members get 403 and owners 200.
- **Channel stamping:** web, token and bot.
- **Migration backfill:** bot rows become `Bot`.
- **Bot:** `/users` works for the owner and falls back to help for members. The user button and the images button work.
- **Nightly report:** sent once, skipped on a quiet day, respects the switch.
