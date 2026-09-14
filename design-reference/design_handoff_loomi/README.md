# Handoff: Loomi — Bilingual AI Image Generation Workspace

## Overview
Loomi is a creative workspace for generating, editing and branching images through a driven
ChatGPT session running in a remote browser. A user owns several **projects**; inside a project
they send prompts, **edit** a previous image, or **branch** a new direction from any earlier
image. The full lineage is shown as a branch graph.

Target stack: **ASP.NET Core (API + SignalR) + Vue 3 (Composition API, Pinia, vue-i18n)**.
Fully bilingual **English / Persian** with true RTL mirroring (not just `text-align`).
**Dark mode is the default**; a light theme is specified as well.

## About the Design Files
The two HTML files in this bundle are **design references created in HTML** — prototypes that
show intended look, structure and behavior. They are **not production code to copy**.
The task is to **recreate these designs inside the target codebase** (Vue 3 + ASP.NET Core)
using its own component patterns, routing, state management and build pipeline.
Where the target codebase already has primitives (buttons, modals, toasts), use those and
restyle them to the tokens below rather than porting the prototype's inline styles.

- `Loomi App.dc.html` — the clickable high-fidelity prototype (all screens + states).
- `Loomi Spec.dc.html` — the design documentation page (IA, flows, wireframes, tokens,
  component inventory, RTL rules, copy table). Open both in a browser.

## Fidelity
**High fidelity.** Colors, typography, spacing, borders, motion timings and copy are final and
listed below. Recreate pixel-faithfully using the codebase's libraries. The only placeholders
are the generated images themselves (rendered as gradient tiles) — replace with real image URLs.

---

## Design Tokens

### Color — Dark (default)
| Token | Hex | Use |
| --- | --- | --- |
| `--bg` | `#0b0d0f` | app ground |
| `--s1` | `#111417` | cards, sidebar, composer |
| `--s2` | `#171b1f` | inset rows, toolbars |
| `--s3` | `#1e2328` | wells, skeleton base |
| `--line` | `#242a30` | hairline borders |
| `--line2` | `#323a42` | strong borders, device bezels |
| `--text` | `#e9edf0` | primary text |
| `--muted` | `#8d979f` | secondary text |
| `--faint` | `#5d666d` | tertiary / metadata |
| `--accent` | `#7aa5cf` | steel accent |
| `--accent-dim` | `rgba(122,165,207,.13)` | tinted fills, active nav |
| `--accent-line` | `rgba(122,165,207,.35)` | accent borders |
| `--ok` | `#63b892` | completed |
| `--warn` | `#d7a15c` | in-progress |
| `--bad` | `#d47878` | failed / destructive |
| `--glass` | `rgba(17,20,23,.72)` | sticky headers, + `backdrop-filter: blur(14px)` |

### Color — Light
| Token | Hex |
| --- | --- |
| `--bg` | `#f1f2f4` |
| `--s1` | `#ffffff` |
| `--s2` | `#f7f8f9` |
| `--s3` | `#eceef1` |
| `--line` | `#dfe2e6` |
| `--line2` | `#c7ccd2` |
| `--text` | `#191c1f` |
| `--muted` | `#5f6a73` |
| `--faint` | `#8b949b` |
| `--accent` | `#43688e` |
| `--accent-dim` | `rgba(67,104,142,.10)` |
| `--accent-line` | `rgba(67,104,142,.32)` |
| `--ok` | `#2f7d5c` · `--warn` `#a56d22` · `--bad` `#a84646` |

Theme is switched by `data-theme="dark|light"` on `<html>`; every value above is a CSS variable
in `:root` / `:root[data-theme="light"]`.

### Typography
- Latin: **Barlow Condensed** (headings, 500/600/700) over **Barlow** (body, 400/500/600).
- Persian: **Vazirmatn** (400/500/600/700) for both roles — there is no condensed Persian face,
  so Persian headings use weight 700 instead.
- Switched by `:root[data-lang="fa"] { --font-ui: 'Vazirmatn'; --font-head: 'Vazirmatn'; }`.

| Role | Size / line-height | Family |
| --- | --- | --- |
| display | 52 / 1.02, uppercase, `letter-spacing:.01em` | heading |
| h1 (screen title) | 26 / 1.15, uppercase, `.02em` | heading |
| h2 (panel title) | 19 / 1.2, uppercase, `.04em` | heading |
| section kicker | 13–14, uppercase, `.12–.2em` | heading |
| body | 14 / 1.6 | body |
| small | 12.5 / 1.55 | body |
| micro / meta | 11–11.5 / 1.4, `.12em` when uppercase | body |

Persian overrides: line-height **1.9–1.95**, `letter-spacing: 0`, `text-transform: none`.
Numerals in metrics use `font-variant-numeric: tabular-nums`.

### Spacing (4px base)
`xs 4` · `sm 8` · `md 14` · `lg 20` · `xl 28` · `2xl 44`

### Radius
`0` everywhere (cards, buttons, inputs, frames), `999px` only for status dots.
The geometry is deliberately square — do not round anything.

### Shadows
| Token | Value |
| --- | --- |
| card | `0 2px 10px rgba(0,0,0,.25)` |
| raised (composer) | `0 12px 40px rgba(0,0,0,.35)` |
| device / phone | `0 18px 50px rgba(0,0,0,.35)` |
| modal | `0 30px 80px rgba(0,0,0,.5)` |
| selection ring | `0 0 0 3px var(--accent-dim)` |

### Surfaces
level 0 `--bg` → level 1 `--s1` → level 2 `--s2` → level 3 `--s3`. Glass (`--glass` + blur 14)
is reserved for sticky headers only.

### Borders
All borders are **1px hairline**. Default `--line`; emphasis `--line2`; selected/active
`--accent` or `--accent-line`. Empty states use `1px dashed --line2`.

### Icons
Lucide, **stroke-width 1.5**. Sizes: sm 14 / md 16 (default) / lg 20.
(The prototype substitutes unicode glyphs — use real Lucide icons in the implementation:
`plus`, `layout-grid`, `list`, `search`, `settings`, `star`, `more-horizontal`, `arrow-left`,
`arrow-up`, `download`, `copy`, `git-branch`, `pencil`, `x`, `refresh-cw`, `moon`, `sun`,
`chevron-left/right`, `paperclip`, `check`, `alert-triangle`, `loader`.)

### Motion
| What | Timing |
| --- | --- |
| hover / color | 140ms ease-out |
| panel & sidebar width | 200–220ms `cubic-bezier(.2,.7,.3,1)` |
| element enter | 260ms, translateY(8px) → 0 + fade |
| node pop-in | 260ms, scale(.97) → 1 + fade |
| progress bar width | 400ms |
| skeleton shimmer | 1200ms linear infinite |
| status pulse | 1400ms ease-in-out infinite |
| spinner | 800ms linear infinite |

Honor `prefers-reduced-motion: reduce` → all durations to 0, keep opacity changes only.

---

## Screens / Views

### 1. App shell
**Layout:** `display:flex`, full viewport height. Sidebar is a flex item (`236px` expanded,
`68px` collapsed, 220ms transition), `position:sticky; top:0; height:100vh`,
`border-inline-end: 1px solid var(--line)`, background `--s1`. Main is `flex:1; min-width:0`.

**Sidebar contents, top to bottom:**
- Brand row: 26×26 mark (`1px solid --accent-line`, `--accent-dim` fill, letter "L" in
  Barlow Condensed 700 / 15px, accent color), wordmark 19px Condensed 600 uppercase `.04em`,
  and a 26×26 collapse icon-button. The collapse chevron direction flips in RTL.
- **New project** button: full width, 9×10 padding, `1px solid --accent-line`,
  `--accent-dim` fill, accent text 13px/600. Hover → `rgba(122,165,207,.22)`.
- **Nav list:** Projects / current project / Gallery / Settings / Mobile. Each row: 8×10 padding,
  16px icon slot, 13.5px label, optional right-aligned count in 11px `--faint`.
  Active row: `--accent-dim` background, `--accent-line` border, accent text.
  Inactive hover: `--s3`.
- **Recent** group: 11.5px uppercase `.14em` `--faint` heading, then rows with a 16×16 project
  swatch and a 12.5px truncated title.
- Footer block (`border-top: 1px solid --line`, 12px padding):
  connection button (7px dot + `0 0 0 3px` halo, status label 12px/600 in status color,
  "ChatGPT" sub-label 10.5px `--faint`), a two-cell **EN / فا** segmented control plus a 34px
  theme toggle, and the user chip (26×26 initials square, name 12px/600, plan 10.5px `--faint`).

### 2. Projects screen
**Header** (sticky, `--glass` + blur 14, `1px solid --line` bottom, 20×28 padding):
title "Projects" (26px Condensed 600 uppercase) · spacer · search field (min 220px, hairline
frame, leading `⌕`, 13px input, trailing `⌘K` hint in an 11px bordered chip) · sort button
(cycles Recent ↔ Name) · grid/list view toggle (two-cell segmented).

**Disconnected banner** (only when session ≠ connected): 12×14 padding, `1px solid rgba(212,120,120,.35)`,
`rgba(212,120,120,.08)` fill, red dot, title 13px/600, body 12px `--muted`, and a **Connect ChatGPT**
accent button on the trailing edge.

**Grid:** `grid-template-columns: repeat(auto-fill, minmax(240px,1fr)); gap:16px`.
**ProjectCard:** `1px solid --line`, `--s1` fill.
- Thumbnail area 132px tall, project gradient, `border-bottom: 1px solid --line`, centered
  2-letter mark in Condensed 700 / 40px at `rgba(255,255,255,.16)`; status chip pinned
  `top:8px; inset-inline-end:8px` (10.5px, `rgba(0,0,0,.35)` fill, 1px border in the status color,
  glyph + label).
- Body 12–13px padding: title 14px/600 truncated, meta line `"{n} generations · {relative time}"`
  in 11.5px `--faint`, and a `⋯` context-menu button (stops propagation).
- Hover: `border-color: --line2`, `transform: translateY(-2px)`, 160ms. Enter animation: rise 260ms.

**Sample data (use for seeding/demo):** Saffron Packaging / بسته‌بندی زعفران (24, 2 min ago,
generating) · Album Covers / کاورهای آلبوم (12, 1 h ago) · Interior Moodboard / مودبورد داخلی
(38, yesterday) · Ceramics — Product Shots / سرامیک — عکس محصول (9, 3 days ago) ·
Brand Mascot / مسکات برند (3, last week, failed).

**Empty / no-result state:** `1px dashed --line2`, 56×24 padding, centered.
Title 20px Condensed uppercase `--muted`, body 13px `--faint`, "Clear search" button.

### 3. Project screen
**Header** (sticky, glass): back icon-button (arrow flips in RTL) · project title 22px Condensed
600 with a 11.5px `--faint` meta line · favorite star icon-button (accent when on) · spacer ·
connection pill (dot + label) · **view segmented control** (Tree / Timeline / Grid) · `⋯` menu.

#### 3a. Tree view (default) — the branch graph
Horizontal, left-to-right lineage (mirrored right-to-left in Persian).
- Node card: **208 × ~150px**, absolutely positioned inside a sized relative canvas.
- Grid maths: `x = col * (208 + 82)`, `y = row * (150 + 26)`. Rows may be fractional (e.g. 1.5)
  to center a parent between its children. Canvas size = `(maxCol+1)*208 + maxCol*82 + 40`
  by `(maxRow+1)*176 + 20`; the container scrolls on both axes.
- Positioning uses `inset-inline-start` (not `left`) so the whole graph mirrors in RTL.
- **Edges** are one absolutely-positioned `<svg>` layer behind the nodes, drawn as cubic béziers:
  `M x1 y1 C x1+46 y1, x2-46 y2, x2 y2` where `x1 = parentX + 208`, `y1 = parentY + 75`,
  `x2 = childX`, `y2 = childY + 75`.
  - **Edit** edges: solid, `--line2`, 1.25px.
  - **Branch** edges: dashed `4 4`, `--accent`, 1.25px.
  - In RTL the svg layer gets `transform: scaleX(-1)` (nodes mirror natively via logical inset).
- **GenerationNode:** 104px image area (gradient placeholder, `#{id}` in Condensed 30px at
  `rgba(255,255,255,.18)`), status chip top-leading, kind chip ("Edit" / "Branch") bottom-leading,
  then 9–10px padding: prompt 11.5px/1.45 `--muted` clamped to 33px, and a row with timestamp
  (10.5px `--faint`) + **Edit** / **Branch** / copy (`⧉`) micro-buttons (10.5px, hairline,
  accent on hover).
  - Selected: `border-color: --accent` + `box-shadow: 0 0 0 3px var(--accent-dim)`.
  - Busy: `rgba(0,0,0,.5)` scrim over the image with the pulsing state label in `--warn`.

**Seed tree** (id, parent, col, row, kind, status):
`1,–,0,1.5,–,completed` · `2,1,1,0,edit,completed` · `3,1,1,3,branch,completed` ·
`4,2,2,0,edit,completed` · `5,2,2,1.5,branch,completed` · `6,4,3,0,edit,failed` ·
`7,3,2,3,edit,generating`.

#### 3b. Timeline view
Single column, max 760px. Each row is indented by `min(col,3) * 26px` (lineage by indentation),
with a 14px rail column on the leading side: a 9×9 square outlined in the status color plus a
1px `--line` vertical line. Card: hairline frame, 12px padding, 88×88 thumbnail, status chip +
kind + timestamp on one line, prompt 12.5px/1.5, then Edit / Branch / Copy prompt buttons.

#### 3c. Grid (gallery) view
Filter chips (All / Completed / Generating / Failed) then a masonry `columns: 4 190px;
column-gap: 14px` with `break-inside: avoid`. Tile: gradient image of variable height
(140–210px), prompt 11.5px, hover reveals a 3-button action row (Edit / Branch / ↓).
Hover: `border-color: --accent-line`.
**Loading:** 8 skeleton tiles for ~1s — shimmer gradient
`linear-gradient(90deg, --s2 0, --s3 120px, --s2 240px)` at `background-size: 420px 100%`,
animated `-420px → 420px` over 1.2s, plus two pulsing text bars.

#### 3d. Detail panel (trailing side)
302px wide, `border-inline-start: 1px solid --line`, `--s1`, collapses to 0 when nothing is
selected (200ms). Contains: "Details #{id}" title + close, 150px preview, Prompt block,
a two-column Parent / Status grid, then a primary **Edit** button and a Branch / Download pair.

#### 3e. Prompt composer (sticky, always reachable)
`position: fixed; bottom: 0; inset-inline-end: 0; inset-inline-start: <sidebar width>`,
16–26px padding, background `linear-gradient(to top, var(--bg) 55%, transparent)`.
Inner card: `max-width: 900px`, centered, `1px solid --line2` (→ `--accent-line` when a context
is active), `--s1`, raised shadow. Stacked regions:
1. **Context chip row** (only when editing/branching): `--accent-dim` fill, 28×28 parent
   thumbnail, label **"Editing Image #12"** / **"Starting branch from Image #12"** in 12.5px
   accent 600, and an `✕` to clear.
2. **Run bar** (only while generating): spinner (12px, 1.5px accent ring, transparent top,
   800ms spin), phase label 12.5px/600 accent, a 2px progress track (`--s3`) with an accent fill
   at the phase's percentage (400ms transition), percentage in tabular 11px, and a **Cancel** button.
3. **Textarea**: transparent, no border, 14px/1.55, 14px padding, 2 rows, auto-grow to 6.
4. **Action row**: "Attach image" button · mode segmented control (Create / Edit / Branch) ·
   spacer · `⌘ + ↵ to send` hint (11px `--faint`) · **Send** button — solid `--accent` fill with
   `#0b0d0f` text, 8×18 padding, 13px/600, `↑` glyph. Disabled at 0.5 opacity while running.

### 4. Connection modal
Backdrop `rgba(0,0,0,.6)` + `blur(3px)`, centered dialog `min(520px, 92vw)`,
`1px solid --line2`, `--s1`, modal shadow, 180ms pop-in.
- Header: 9px status dot with `0 0 0 4px` halo, title "ChatGPT connection" (19px Condensed
  uppercase), "Last connected · {time}" 12px `--faint`, close `✕`.
- Status block: 14px padding, border + fill tinted from the status color, glyph, status label
  13.5px/600, and a description line 12px `--muted`.
- Actions: primary (**Reconnect** or **Connect ChatGPT** when disconnected), then a pair —
  **Open login session** and **Reset session**.
- Footnote 11.5px `--faint` above a `--line` top border.
Focus is trapped; Esc and backdrop click close.

### 5. Login session screen
Two columns: `minmax(0,1fr) 300px`, 20px gap.
- **Browser frame:** chrome bar (`--s2`, 9×12 padding) with three 8×8 dots, a URL field
  (`chat.openai.com/auth/login`, 11.5px, hairline, `--bg` fill), and a "Stream live" indicator
  (pulsing 6px green dot). Viewport 420px tall, `--s2`, centered explanatory block
  ("A remote browser is open" 13.5px/600 + 12px `--faint` body), and `noVNC · 1280×800 · 24 fps`
  pinned bottom-leading. **Implementation: this is where the noVNC canvas mounts.**
- **Side rail:** "How this works" card with three numbered steps (20×20 accent-outlined numerals):
  1. Log in to ChatGPT in the frame. 2. Complete verification if it is requested.
  3. Return to the app — the session is picked up automatically.
  Then a primary **Return to app** button, a **Reset session** secondary, and a 11.5px note about
  14-day expiry.

### 6. Settings screen
Max width 880px. Sections are hairline cards with an uppercase 14px Condensed header row and
rows of `label + hint + control`, separated by `1px solid --line`.
- **Appearance:** Language (English / فارسی segmented) · Theme (Dark / Light) · Default project
  view (Tree / Timeline / Grid).
- **Browser session:** status row (dot, label in status color, "Last connected · {time}") with
  **Open login session** and **Reconnect**; then **Reset session** with a red-outlined Reset button.
- Three-card row: **Storage** (4.8 GB / 20 GB, 4px progress bar at 24%, "86 images stored",
  Manage storage) · **Keyboard shortcuts** (label + key chip rows) · **Data** (Export all
  projects (.zip) / Import from file / Delete all data in `--bad`).

**Shortcuts:** `⌘↵` send · `⌘N` new project · `⌘K` search · `E` edit selected · `B` branch
selected · `⌘⇧L` toggle language.

### 7. Mobile
Drawer replaces the sidebar (232px, overlay `rgba(0,0,0,.45)`, swipe to close). The branch graph
becomes the indented timeline. The composer docks to the bottom edge above the keyboard and keeps
its context chip. All tap targets ≥ 44×44. Generation detail opens as a full sheet.
The prototype's "Mobile" screen shows three 300×620 reference frames: drawer, project timeline,
and generating state.

---

## Interactions & Behavior

- **Select a node** → detail panel opens (or mobile sheet); accent border + ring.
- **Edit** on a node → sets mode `edit`, sets composer context to that node, chip reads
  "Editing Image #12". **Branch** → mode `branch`, chip reads "Starting branch from Image #12".
  The mode segmented control mirrors this; choosing "Create" clears the context.
- **Send** (`⌘ + ↵`): if the session is not connected, open the connection modal instead.
  Otherwise run the state machine below, then append a child node under the context node
  (or the latest node), select it, clear the prompt and context, and show a toast
  "Image #13 ready".
- **Cancel** aborts the run and toasts "Generation cancelled"; the typed prompt is preserved.
- **Reconnect** → status goes `reconnecting` for ~1.4s → `connected`, modal closes,
  "Last connected" becomes "just now".
- **Login session** → returning sets the session to connected and navigates back to Projects.
- **Favorite** toggles and toasts; **Copy prompt** toasts "Prompt copied".
- **Search** filters projects live; empty result swaps in the no-result empty state.
- Toasts: bottom-center, single at a time, auto-dismiss 1.8s.

### Generation state machine
`idle → connecting (8%) → sending prompt (22%) → waiting for response (45%) →
generating (78%) → downloading (94%) → completed | failed`, plus `reconnecting` at any point.
Each step ≈ 950ms in the prototype; in production these are pushed over SignalR.
Every state shows **glyph + color + written label** — never color alone:
`○ idle` · `◌ connecting` · `↑ sending` · `◔ waiting` · `◐ generating` · `↓ downloading` ·
`✓ completed` · `! failed` · `⟳ reconnecting`.
While a generation runs, a placeholder node appears in the tree with a shimmer skeleton.
Failed cards keep the prompt and offer Retry + Copy prompt.

### Empty states (dashed frame, glyph, title EN/FA, body, one CTA)
- No projects yet / هنوز پروژه‌ای نیست → "New project"
- Empty project / پروژه خالی است → "Write a prompt"
- ChatGPT disconnected / اتصال ChatGPT قطع است → "Connect"
- Generation failed / تولید ناموفق بود → "Retry"
- No matching projects / پروژه‌ای پیدا نشد → "Clear search"

---

## RTL / LTR behaviour
- Single source of truth: `dir` and `data-lang` on `<html>`, written by a `useDirection()`
  composable. No component reads a hard-coded `left`/`right`.
- Use only logical properties: `margin-inline-*`, `padding-inline-*`, `inset-inline-start/end`,
  `border-inline-start/end`, `text-align: start/end`.
- The **whole shell mirrors**: sidebar moves to the right, detail panel to the left, composer
  offsets follow the sidebar on the correct side.
- Directional icons flip (back arrow, chevrons); neutral icons (star, gear, download) do not.
- The branch graph's SVG edge layer takes `transform: scaleX(-1)` in RTL; nodes mirror natively
  because they are positioned with `inset-inline-start`.
- IDs, timestamps and percentages stay LTR inside `unicode-bidi: isolate` spans.
- Persian disables `text-transform: uppercase` and raises line-height to ~1.9.

## Responsive behaviour
| Range | Rule |
| --- | --- |
| ≥ 1280px | Full shell: sidebar 236, tree canvas, detail panel 302, composer max 900 centered. |
| 1024–1279 | Detail panel becomes an overlay sheet; sidebar auto-collapses to 68px icons. |
| 768–1023 | Sidebar becomes a drawer; tree keeps 2D but scrolls horizontally. |
| < 768 | Tree → indented timeline; gallery → 2 columns; composer docks to the bottom edge; targets ≥ 44px; detail opens as a full sheet. |

## Accessibility
- Body text ≥ 4.5:1 on every surface; the steel accent is used at ≥ 3:1 for chrome and large text
  only — never for paragraph copy on the accent tint.
- Status is glyph + label + color, never color alone.
- Full keyboard path: Tab order follows visual order (and mirrors in RTL), Esc closes modal/drawer,
  arrow keys move between tree nodes, `E`/`B` act on the selection.
- Focus: `:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px }` everywhere.
- Modals trap focus and restore it on close; toasts announce via `aria-live="polite"`.
- Tree is also exposed as a nested list to assistive tech (`role="tree" / "treeitem"`), with each
  node labelled "Generation 13, branched from 12, completed".
- Minimum touch target 44×44 on mobile.

---

## State Management (Pinia)
- `useUiStore` — `lang`, `theme`, `sidebarExpanded`, `defaultView`, `currentView`, toasts.
- `useProjectsStore` — `projects[]`, `query`, `sort`, `filter`, CRUD, `favorite`.
- `useGenerationStore` — `nodes[]` (id, parentId, kind `edit|branch|null`, status, prompt,
  imageUrl, createdAt), `selectedId`, `composerContext`, `mode`, `run { phase, progress }`.
  Derived getters build the layout (`col`, `row`) and the edge path list.
- `useConnectionStore` — `status` (`connected|disconnected|reconnecting|connecting`),
  `lastConnectedAt`, `sessionId`, actions `connect`, `reconnect`, `reset`, `openLoginSession`.

**Layout algorithm for the tree:** `col = depth`; assign each leaf a sequential row, then set an
internal node's row to the average of its children's rows (this produces the 1.5 values in the
seed data). Recompute on every node insert.

**Realtime:** a SignalR hub pushes generation transitions; the composer run bar, the node card and
the sidebar status dot all read the same store, never their own timers.

## Suggested Vue 3 structure
```
src/
├ App.vue
├ layouts/AppShell.vue
├ components/
│  ├ AppSidebar.vue
│  ├ LanguageSwitch.vue · ThemeToggle.vue
│  ├ ProjectCard.vue · ProjectGrid.vue
│  ├ ProjectHeader.vue · ViewToggle.vue
│  ├ GenerationTree.vue · GenerationEdges.vue
│  ├ GenerationNode.vue · GenerationCard.vue
│  ├ GenerationGallery.vue · ImageCard.vue
│  ├ GenerationDetailPanel.vue
│  ├ PromptComposer.vue · ComposerContextChip.vue
│  ├ ConnectionModal.vue · ConnectionStatus.vue
│  ├ LoginSessionFrame.vue
│  ├ SettingsDrawer.vue · SettingRow.vue
│  └ ui/ BaseButton, BaseIconButton, BaseInput, BaseTextarea, BaseSelect, BaseModal,
│        BaseDrawer, BaseTooltip, BaseDropdown, BaseToast, BaseBadge, BaseTabs,
│        SkeletonBlock, EmptyState, StatusIndicator, ConfirmDialog
├ composables/ useDirection · useGenerationSocket · useShortcuts · useToasts
├ stores/ projects · generation · connection · ui
├ locales/ en.json · fa.json
└ styles/tokens.css
```

## Routes
| Route | Screen |
| --- | --- |
| `/` | Projects |
| `/p/:id` | Project — tree (default) |
| `/p/:id?view=timeline` | Project — timeline |
| `/p/:id?view=grid` | Project — gallery |
| `/p/:id/g/:gen` | Generation detail (panel desktop / sheet mobile) |
| `/connection` | Connection modal over any route, deep-linkable |
| `/connection/login` | Login session (remote browser) |
| `/settings` | Settings |

## UI copy — en.json / fa.json
| Key | English | فارسی |
| --- | --- | --- |
| `nav.projects` | Projects | پروژه‌ها |
| `nav.recent` | Recent | اخیر |
| `nav.favorites` | Favorites | علاقه‌مندی‌ها |
| `nav.gallery` | Gallery | گالری |
| `nav.settings` | Settings | تنظیمات |
| `action.newProject` | New project | پروژه جدید |
| `action.edit` | Edit | ویرایش |
| `action.branch` | Branch | شاخه |
| `action.download` | Download | دانلود |
| `action.copyPrompt` | Copy prompt | کپی پرامپت |
| `action.rename` | Rename | تغییر نام |
| `action.send` | Send | ارسال |
| `action.cancel` | Cancel | لغو |
| `action.attach` | Attach image | پیوست تصویر |
| `composer.placeholder` | Describe the image you want… | تصویری که می‌خواهید را توصیف کنید… |
| `composer.editing` | Editing Image #{id} | در حال ویرایش تصویر #{id} |
| `composer.branching` | Starting branch from Image #{id} | شروع شاخه از تصویر #{id} |
| `composer.shortcut` | ⌘ + ↵ to send | ⌘ + ↵ برای ارسال |
| `state.idle` | Idle | آماده |
| `state.connecting` | Connecting | در حال اتصال |
| `state.sending` | Sending prompt | ارسال پرامپت |
| `state.waiting` | Waiting for response | در انتظار پاسخ |
| `state.generating` | Generating | در حال تولید |
| `state.downloading` | Downloading | در حال دریافت |
| `state.completed` | Completed | تکمیل شد |
| `state.failed` | Failed | ناموفق |
| `state.reconnecting` | Reconnecting | اتصال مجدد |
| `conn.connected` | Connected | متصل |
| `conn.disconnected` | Disconnected | قطع |
| `conn.connect` | Connect ChatGPT | اتصال به ChatGPT |
| `conn.reconnect` | Reconnect | اتصال مجدد |
| `conn.openLogin` | Open login session | باز کردن نشست ورود |
| `conn.reset` | Reset session | بازنشانی نشست |
| `conn.last` | Last connected | آخرین اتصال |
| `conn.title` | ChatGPT connection | اتصال ChatGPT |
| `conn.disconnectedTitle` | ChatGPT is disconnected | ChatGPT قطع است |
| `conn.disconnectedBody` | Projects stay available, but new generations are paused. | پروژه‌ها در دسترس‌اند، اما تولید جدید متوقف شده است. |
| `login.title` | Login session | نشست ورود |
| `login.frameTitle` | A remote browser is open | یک مرورگر راه دور باز شده است |
| `login.step1` | Log in to ChatGPT in the frame. | در قاب روبه‌رو وارد ChatGPT شوید. |
| `login.step2` | Complete verification if it is requested. | در صورت درخواست، تأیید هویت را کامل کنید. |
| `login.step3` | Return to the app — the session is picked up automatically. | به اپ برگردید — نشست به‌صورت خودکار برداشته می‌شود. |
| `login.return` | Return to app | بازگشت به اپ |
| `empty.noProjects` | No projects yet | هنوز پروژه‌ای نیست |
| `empty.emptyProject` | Empty project | پروژه خالی است |
| `empty.failed` | Generation failed | تولید ناموفق بود |
| `empty.noResults` | No matching projects | پروژه‌ای پیدا نشد |
| `empty.noResultsBody` | Try a different name, or clear the search. | نام دیگری را امتحان کنید یا جستجو را پاک کنید. |
| `settings.language` | Language | زبان |
| `settings.theme` | Theme | تم |
| `settings.dark` / `settings.light` | Dark / Light | تیره / روشن |
| `settings.defaultView` | Default project view | نمای پیش‌فرض پروژه |
| `settings.storage` | Storage | فضای ذخیره |
| `settings.browserSession` | Browser session | نشست مرورگر |
| `settings.shortcuts` | Keyboard shortcuts | میان‌برهای صفحه‌کلید |
| `settings.data` | Data | داده |
| `settings.exportData` | Export all projects (.zip) | خروجی همه پروژه‌ها (.zip) |
| `settings.deleteAll` | Delete all data | حذف همه داده‌ها |
| `view.tree` / `view.timeline` / `view.grid` | Tree / Timeline / Grid | درخت / تایم‌لاین / شبکه |

## Assets
No binary assets ship with this bundle. Generated images are gradient placeholders in the
prototype — wire them to the real image URLs returned by the API. Fonts are Google Fonts
(**Barlow**, **Barlow Condensed**, **Vazirmatn**); self-host them for production.
Icons: Lucide at stroke-width 1.5.

## Files in this bundle
- `Loomi App.dc.html` — the interactive high-fidelity prototype.
- `Loomi Spec.dc.html` — the visual design documentation (tokens, wireframes, IA, copy table).
- `README.md` — this document (self-sufficient; implement from it alone if needed).
