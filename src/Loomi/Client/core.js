// What every page shares: who is signed in, the chats, the one open chat, the connection, the live hub and the address bar.
// Pages import what they need from here instead of receiving it through props, so each page owns its own markup and its own local state.
import { ref, computed, watch } from 'vue';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { messages } from './i18n';

const stored = (key, fallback) => { try { return localStorage.getItem(key) || fallback; } catch { return fallback; } };
const keep = (key, value) => { try { localStorage.setItem(key, value); } catch { /* private window: the choice lasts this visit */ } };

export const lang = ref(stored('loomi.lang', 'fa'));
export const theme = ref(stored('loomi.theme', 'dark'));
export const t = k => messages[lang.value]?.[k] || messages.en[k] || k;
export const locale = computed(() => lang.value === 'fa' ? 'fa-IR' : 'en-GB');
export const n = v => Number(v ?? 0).toLocaleString(locale.value);
/** A string with {0}, {1}… filled in, numbers written the reader's way. */
export const tf = (k, ...args) => t(k).replace(/\{(\d)\}/g, (_, i) => typeof args[i] === 'number' ? n(args[i]) : String(args[i] ?? ''));
watch([lang, theme], () => {
  const root = document.documentElement;
  root.lang = lang.value; root.dir = lang.value === 'fa' ? 'rtl' : 'ltr'; root.dataset.lang = lang.value; root.dataset.theme = theme.value;
  document.querySelector('meta[name=theme-color]')?.setAttribute('content', theme.value === 'dark' ? '#101216' : '#f3f4f6');
  keep('loomi.lang', lang.value); keep('loomi.theme', theme.value);
}, { immediate: true });

export const authenticated = ref(false), initialized = ref(false), me = ref(null), devKey = ref(''), error = ref(''), busy = ref(false);
export const projects = ref([]), current = ref(null), generations = ref([]), packages = ref([]);
export const connected = ref({ ready: false, stalled: null, desktopUrl: null, providers: [] }), live = ref(false), queueInfo = ref({});
/** False until the first status arrives, so a page does not flash "paused" before it knows. */
export const statusKnown = ref(false);
export const provider = ref('ChatGPT');
export const isOwner = computed(() => me.value?.role === 'Owner');
export const ready = computed(() => connected.value.ready === true);
// Only providers this workspace actually has an account for; with one there is no choice to offer.
export const choices = computed(() => (connected.value.providers || []).filter(p => p.accounts > 0).map(p => p.provider));
watch(choices, list => { if (list.length && !list.includes(provider.value)) provider.value = list[0]; });
// Null remaining means no daily limit at all, and there is nothing to show for that.
export const quota = computed(() => me.value?.dailyRemaining != null ? me.value : null);
export const activePackages = computed(() => packages.value.filter(p => p.state === 'Active'));
export const FINISHED = ['Completed', 'Answered', 'Failed', 'Cancelled'], MAX_INPUTS = 4;
// Mirrors the Provider and Operation enums on the server; a price row exists for each provider, whether or not an account does yet.
export const PROVIDERS = ['ChatGPT', 'Gemini'], OPERATIONS = ['', 'Generate', 'Edit', 'Branch'];

let csrf = '', hub = null, timer = null, refreshing = false;
const ticks = new Set();
/** A page that keeps its own data fresh registers here; it runs on the same five-second beat as the chat list and is dropped when the page goes. */
export function onTick(fn) { ticks.add(fn); return () => ticks.delete(fn); }

export async function api(path, method = 'GET', body) {
  let response;
  try { response = await fetch(path, { method, credentials: 'same-origin', headers: { 'Content-Type': 'application/json', ...(method !== 'GET' ? { 'X-CSRF-TOKEN': csrf } : {}) }, body: body ? JSON.stringify(body) : undefined }); }
  catch { throw new Error('NetworkError'); }
  if (response.status === 401 && !path.endsWith('/login')) { authenticated.value = false; throw new Error('SessionExpired'); }
  if (!response.ok) { const payload = await response.json().catch(() => ({})); throw new Error(response.status === 429 ? 'RateLimited' : payload.error || 'ServerError'); }
  return response.status === 204 ? null : response.json();
}
/** Every multipart post goes through here, so a template's sample carries the same CSRF header and error mapping as the composer's files. */
export async function postForm(path, form) {
  let response;
  try { response = await fetch(path, { method: 'POST', credentials: 'same-origin', headers: { 'X-CSRF-TOKEN': csrf }, body: form }); }
  catch { throw new Error('NetworkError'); }
  if (response.status === 401) { authenticated.value = false; throw new Error('SessionExpired'); }
  if (!response.ok) { const payload = await response.json().catch(() => ({})); throw new Error(response.status === 413 ? 'FileTooLarge' : response.status === 429 ? 'RateLimited' : payload.error || 'ServerError'); }
  return response.json();
}
export async function uploadFiles(files) { const form = new FormData(); for (const f of files) form.append('files', f); return postForm('/api/uploads', form); }
/** One action at a time, its failure shown in the shell's toast. */
export async function action(fn) { if (busy.value) return; busy.value = true; error.value = ''; try { await fn(); } catch (e) { error.value = e.message; } finally { busy.value = false; } }

export async function session() {
  const s = await api('/api/session'); csrf = s.csrfToken; authenticated.value = s.authenticated;
  me.value = s.authenticated ? { username: s.username, role: s.role, dailyQuota: s.dailyQuota, dailyUsed: s.dailyUsed, dailyRemaining: s.dailyRemaining, credits: s.credits } : null;
  devKey.value = s.devAccessKey || '';
}
export async function login(username, password) { await api('/api/session/login', 'POST', { username, password }); await session(); await start(); }
export async function logout() {
  await api('/api/session/logout', 'POST'); await hub?.stop(); hub = null;
  authenticated.value = false; me.value = null; projects.value = []; current.value = null; generations.value = []; packages.value = [];
  await session(); go('home');
}
const positions = list => Object.fromEntries((list || []).map(p => [p.id, p]));
/** Packages arrived with the timed-package release; a server without them simply has none. */
export async function loadPackages() { packages.value = me.value?.role === 'Member' ? await api('/api/packages').catch(() => []) : []; }
export async function refresh() {
  if (!authenticated.value || refreshing) return; refreshing = true;
  try {
    const [ps, status, , queue] = await Promise.all([api('/api/projects?mine=true'), api('/api/auth/status'), session(), api('/api/generations/queue')]);
    projects.value = ps; connected.value = status; statusKnown.value = true; queueInfo.value = positions(queue);
    await loadPackages();
    if (current.value) {
      const id = current.value.id, gs = await api(`/api/projects/${id}/generations`);
      if (current.value?.id === id) { generations.value = gs; const updated = ps.find(p => p.id === id); if (updated) current.value = updated; }
    }
    for (const fn of ticks) fn();
  } finally { refreshing = false; }
}
export async function start() {
  await refresh();
  if (hub) await hub.stop();
  hub = new HubConnectionBuilder().withUrl('/hubs/status').withAutomaticReconnect().configureLogging(LogLevel.None).build();
  hub.on('QueueUpdated', list => queueInfo.value = positions(list));
  hub.on('GenerationUpdated', g => {
    if (current.value?.id === g.projectId) { const i = generations.value.findIndex(x => x.id === g.id); if (i >= 0) generations.value[i] = g; else generations.value.push(g); }
    if (FINISHED.includes(g.status)) refresh().catch(() => {});
  });
  hub.onreconnecting(() => live.value = false); hub.onreconnected(() => { live.value = true; refresh().catch(() => {}); }); hub.onclose(() => live.value = false);
  try { await hub.start(); live.value = true; } catch { live.value = false; }
  route();
}
export async function boot() {
  try { await session(); if (authenticated.value) await start(); }
  catch (e) { error.value = e.message; }
  finally { initialized.value = true; }
  clearInterval(timer); timer = setInterval(() => refresh().catch(() => {}), 5000);
  window.addEventListener('hashchange', route);
}

// ---- Where the person is: kept in the address so the back button, a reload and a shared link all land on the same page ----
// Pages: home, chat (with an id), chats (the phone's list), templates, gallery, account, admin (with a section).
// 'loading' until the address has been read, so a reload into a chat does not show home first.
export const view = ref('loading'), section = ref('overview');
export const ADMIN_SECTIONS = ['overview', 'users', 'accounts', 'templates', 'pricing', 'bot'];
/** Moves to a page by changing the address; the hashchange listener does the rest, so every way of getting there behaves the same. */
export function go(page, arg) {
  const hash = '#/' + [page, arg].filter(Boolean).join('/');
  if (location.hash === hash) route(); else location.hash = hash;
}
export function route() {
  const [page = 'home', arg] = location.hash.replace(/^#\/?/, '').split('/');
  // While another chat loads nothing shows, rather than the page that was there before or home flashing past on a reload.
  if (page === 'chat' && arg) { if (current.value?.id !== arg) { view.value = 'loading'; openChat(arg); } else view.value = 'chat'; return; }
  if (page === 'admin') { if (!isOwner.value && me.value) { go('home'); return; } section.value = ADMIN_SECTIONS.includes(arg) ? arg : 'overview'; view.value = 'admin'; window.scrollTo(0, 0); return; }
  view.value = ['home', 'chats', 'templates', 'gallery', 'account'].includes(page) ? page : 'home';
  if (view.value !== 'chat') { current.value = null; generations.value = []; }
  window.scrollTo(0, 0);
}
// Not through action(): opening a chat must work while something else is busy, or a tap on it would quietly do nothing.
async function openChat(id) {
  try {
    const gs = await api(`/api/projects/${id}/generations`);
    current.value = projects.value.find(p => p.id === id) || await api(`/api/projects/${id}`);
    generations.value = gs; view.value = 'chat'; window.scrollTo(0, document.documentElement.scrollHeight);
  } catch (e) { error.value = e.message; go('home'); }
}
export function openProject(p) { go('chat', p.id); }
/** A template makes its project on the server, so the list held here does not have it yet. */
export async function openCreated({ projectId }) { try { projects.value = await api('/api/projects?mine=true'); } catch { /* the next refresh finds it */ } go('chat', projectId); }
/** A new chat is a new project begun by its first message, and the page moves into it at once, as a chat app does. */
export async function startChat(body) {
  const { project, generation } = await api('/api/projects/start', 'POST', { provider: provider.value, ...body });
  projects.value = [project, ...projects.value.filter(p => p.id !== project.id)];
  current.value = project; generations.value = [generation]; go('chat', project.id);
  refresh().catch(() => {});
}
export async function reloadCurrent() { const id = current.value?.id; if (!id) return; const gs = await api(`/api/projects/${id}/generations`); if (current.value?.id === id) generations.value = gs; }

// ---- The open chat ----
// A project is one conversation, which takes one message at a time as the site itself does: the composer waits while anything in it is unfinished.
export const waiting = computed(() => generations.value.some(g => !FINISHED.includes(g.status)));
export const latest = computed(() => generations.value.at(-1));
// Until something in it was answered the next message starts the chat, so which provider answers is still open.
export const started = computed(() => generations.value.some(g => ['Completed', 'Answered'].includes(g.status)));
// The picture the next message works on anyway: editing it is only a matter of writing that message.
export const latestPicture = computed(() => generations.value.findLast(g => g.status === 'Completed' && g.imageUrl));

// ---- How a request will be paid, for the line beside the send button ----
/** The first package with room today, in the order the server spends them; null when the next image is paid from credits or is free. */
export const payingPackage = computed(() => {
  const active = activePackages.value;
  const room = p => p.remaining == null || p.remaining > 0;
  return active.find(p => p.kind === 'Unlimited' && room(p)) || active.find(p => p.kind === 'Daily' && room(p)) || active.find(p => p.kind === 'Credits' && room(p)) || null;
});

/** Whole days left, counting today's part as a day, so a package ending tonight reads "1 day" rather than "0". */
export const daysLeft = p => Math.max(0, Math.ceil((when(p.endsAt).getTime() - Date.now()) / 86400000));
/** The one-line name of a package the way the wallet and the tables show it: what it gives, then how long it has left. */
export function packageLabel(p) {
  const what = p.kind === 'Unlimited' ? t('shPkgUnlimited') : p.kind === 'Daily' ? tf('shPkgDaily', p.amount) : tf('shPkgCredits', p.remaining ?? p.amount);
  return p.state === 'Scheduled' ? `${what} · ${tf('shPkgStarts', date(p.startsAt))}` : `${what} · ${tf('shDaysLeft', daysLeft(p))}`;
}
/** Today's day in the person's calendar, for grouping chats: today, yesterday, the last seven days, earlier. */
export function dayGroup(iso) {
  const day = d => { const x = new Date(d); x.setHours(0, 0, 0, 0); return x.getTime(); };
  const diff = Math.round((day(Date.now()) - day(iso)) / 86400000);
  return diff <= 0 ? 'today' : diff === 1 ? 'yesterday' : diff < 7 ? 'thisWeek' : 'earlier';
}
/** SQLite hands some times back without a zone; they are UTC, so they are read as UTC. */
export const when = iso => new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(iso) ? iso : iso + 'Z');
export const ago = iso => {
  const s = (Date.now() - when(iso).getTime()) / 1000, rtf = new Intl.RelativeTimeFormat(locale.value, { numeric: 'auto' });
  if (s < 60) return rtf.format(0, 'second');
  if (s < 3600) return rtf.format(-Math.round(s / 60), 'minute');
  if (s < 86400) return rtf.format(-Math.round(s / 3600), 'hour');
  if (s < 86400 * 7) return rtf.format(-Math.round(s / 86400), 'day');
  return when(iso).toLocaleDateString(locale.value, { day: 'numeric', month: 'long' });
};
export const date = iso => when(iso).toLocaleDateString(locale.value, { day: 'numeric', month: 'long', year: 'numeric' });

export async function cancelOne(g) { await action(async () => { await api(`/api/generations/${g.id}/cancel`, 'POST'); await refresh(); await reloadCurrent(); }); }
export async function cancelAll() { await action(async () => { await api('/api/generations/cancel', 'POST'); await refresh(); await reloadCurrent(); }); }
export async function deleteProject() { await action(async () => { await api(`/api/projects/${current.value.id}`, 'DELETE'); current.value = null; generations.value = []; go('home'); await refresh(); }); }
