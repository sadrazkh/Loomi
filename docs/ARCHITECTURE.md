# Loomi architecture

One ASP.NET Core host serves a small Vue 3 workspace, JSON controllers, authenticated images, SignalR and an authenticated noVNC reverse proxy. No OpenAI API or private ChatGPT endpoints are used.

`Models` contains project and generation entities; `Data` contains EF Core SQLite mappings and migrations; `Repositories` owns queries; `Services` owns submission, persistent queue processing and filesystem storage; `BrowserAutomation` owns a single persistent Chromium context with centralized configurable DOM selectors. `DTOs` separates public responses from storage paths. `Client` holds Vue components, translations and CSS; Vite bundles local dependencies into `wwwroot/dist` without a separate SPA server.

Generation state: Queued → OpeningBrowser → OpeningChatGPT → SendingPrompt → WaitingForResponse → GeneratingImage → DownloadingImage → Completed, or Failed. The database is the durable queue. A single hosted worker processes FIFO; browser access is guarded by a semaphore, including connection/reset actions. On restart, interrupted operations fail explicitly rather than resubmitting a possibly accepted prompt. Queued operations survive restart.

Each generation records its own conversation URL. Edits follow the parent's conversation and attach the parent's stored image to disambiguate earlier versions. Branches start a new conversation and upload the parent. New projects reserve an empty new-chat page; ChatGPT assigns a durable conversation URL only after submission. A new root generation starts a fresh conversation. The project records the most recently completed conversation.

SQLite, images, browser profile and ASP.NET data-protection keys live outside wwwroot in a persistent Storage volume. The application is single-user and single-instance; scaling requires distributed locking and a separate browser worker. Files are served only through authorized generation endpoints. App access uses an environment-provided access key, cookie authentication, antiforgery headers, rate limiting and same-origin WebSocket validation. ChatGPT credentials never pass through the application login form.

noVNC, websockify and x11vnc listen only on container loopback. The ASP.NET authenticated proxy is the sole desktop entry point. The desktop is the user's trusted browser session; protect the application access key like that session. Deploy behind HTTPS and configure explicit forwarded-proxy addresses.
