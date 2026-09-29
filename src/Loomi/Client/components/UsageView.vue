<script setup>
import { ref, computed, watch, nextTick, onMounted } from 'vue';
import { BarChart3, Users, MessagesSquare, Images, Layers, AlertTriangle, ArrowLeft, ArrowDown, ArrowUp, RefreshCw, ShieldCheck, UserX, Globe, Send, Code, CircleCheck, CircleX, Ban, LoaderCircle, MessageSquareText, LayoutTemplate, FolderOpen, Download, Gauge, ChevronDown } from 'lucide-vue-next';
import Modal from './Modal.vue';
// The owner's view of everybody's use. Busy and error belong to the shell, as in TemplatesView: every call goes through its action(), so a failure lands in the same toast.
const props = defineProps({ t:Function, locale:String, api:Function, action:Function, busy:Boolean });
const PERIODS = [['today','usageToday'],['week','usageWeek'],['month','usageMonth'],['all','usageAll']];
const COLUMNS = [['requests','usageRequests'],['images','usageImagesCol'],['failed','usageFailed'],['followUps','usageFollowUps'],['creditsSpent','usageCreditsCol']];
const CHANNELS = { Web:[Globe,'channelWeb','web'], Bot:[Send,'channelBot','bot'], Api:[Code,'channelApi','api'] };
const BADGES = { Completed:['ok',CircleCheck], Answered:['accent',MessageSquareText], Failed:['bad',CircleX], Cancelled:['',Ban] };
const PAGE = 20;
const period = ref('today'), summary = ref(null), daily = ref(null), loading = ref(false), sort = ref({ key:'requests', desc:true });
const person = ref(null), requests = ref([]), requestsLoaded = ref(false), more = ref(false), expanded = ref(new Set()), viewing = ref(null), heading = ref(null);
let lastRow = null;

const n = v => Number(v ?? 0).toLocaleString(props.locale);
// Times read back from the database carry no zone marker; read without one, a browser would take a UTC time for its own local time.
const when = iso => new Date(/[zZ]$|[+-]\d\d:?\d\d$/.test(iso) ? iso : iso + 'Z');
const dateTime = iso => new Intl.DateTimeFormat(props.locale, { dateStyle:'medium', timeStyle:'short' }).format(when(iso));
const day = iso => new Intl.DateTimeFormat(props.locale, { dateStyle:'medium' }).format(when(iso));
// Within the week "3 hours ago" says more than a date; past it, the date is what is remembered.
function ago(iso) {
 const seconds = (when(iso) - Date.now()) / 1000, abs = Math.abs(seconds), rtf = new Intl.RelativeTimeFormat(props.locale, { numeric:'auto' });
 if (abs >= 7 * 86400) return day(iso);
 if (abs < 60) return rtf.format(0, 'second');
 if (abs < 3600) return rtf.format(Math.round(seconds / 60), 'minute');
 if (abs < 86400) return rtf.format(Math.round(seconds / 3600), 'hour');
 return rtf.format(Math.round(seconds / 86400), 'day');
}
// A small share keeps one decimal, so one failure in two hundred does not read as none at all.
const share = (part, whole) => new Intl.NumberFormat(props.locale, { style:'percent', maximumFractionDigits:part && part / whole < .1 ? 1 : 0 }).format(part / whole);
// Zero means no daily limit, and a pre-upgrade owner may still carry the old unbounded sentinel; neither is a number worth showing.
const limited = u => u.dailyQuota > 0 && u.dailyQuota <= 100000;
const fill = (key, values) => Object.entries(values).reduce((text, [k, v]) => text.replace(`{${k}}`, v), props.t(key));
const failNote = (failed, total) => failed ? { note:fill('usageFailedShare', { p:share(failed, total) }), warn:true } : { note:total ? props.t('usageNoneFailed') : '' };
const imageNote = (images, total) => total ? fill('usageImagesShare', { p:share(images, total) }) : '';

const periodKey = computed(() => PERIODS.find(([key]) => key === period.value)[1]);
const since = computed(() => summary.value?.since ? fill('usageSince', { date:day(summary.value.since) }) : props.t('usageAllTime'));
const emptyText = computed(() => props.t(period.value === 'today' ? 'usageEmptyToday' : period.value === 'all' ? 'usageEmptyAll' : 'usageEmptyPeriod'));
const overview = computed(() => {
 const s = summary.value; if (!s) return [];
 return [
  { icon:Users, label:'usageActiveUsers', value:n(s.activeUsers), note:fill('usageActiveOf', { n:n(s.users.length) }) },
  { icon:MessagesSquare, label:'usageRequests', value:n(s.requests), ...failNote(s.failed, s.requests) },
  { icon:Images, label:'usageImages', value:n(s.images), note:imageNote(s.images, s.requests) },
  { icon:Layers, label:'usageCreditsSpent', value:n(s.creditsSpent), note:props.t('usageNetOfRefunds') },
 ];
});
const figures = computed(() => {
 const u = person.value; if (!u) return [];
 return [
  { icon:MessagesSquare, label:'usageRequests', value:n(u.requests), ...failNote(u.failed, u.requests) },
  { icon:Images, label:'usageImages', value:n(u.images), note:imageNote(u.images, u.requests) },
  { icon:Layers, label:'usageCreditsSpent', value:n(u.creditsSpent), note:u.creditsLeft != null ? `${props.t('usageCreditsLeft')}: ${n(u.creditsLeft)}` : props.t('usageNotCharged') },
  { icon:Gauge, label:'usageTodayUse', value:`${n(u.usedToday)} / ${limited(u) ? n(u.dailyQuota) : '∞'}`, note:limited(u) ? `${props.t('quotaLeft')}: ${n(Math.max(0, u.dailyQuota - u.usedToday))}` : props.t('usageNoLimit') },
 ];
});
const groups = computed(() => {
 const u = person.value; if (!u) return [];
 const running = u.requests - u.images - u.answers - u.failed - u.cancelled;
 return [
  { title:'usageOutcome', rows:[['usageImagesCol', u.images], ['usageAnswers', u.answers], ['usageFailed', u.failed], ['usageCancelled', u.cancelled], ...(running > 0 ? [['usageInProgress', running]] : [])] },
  { title:'usageKind', rows:[['usageNewChats', u.newChats], ['usageFollowUps', u.followUps]] },
  { title:'usageChannels', rows:[['channelWeb', u.web], ['channelBot', u.bot], ['channelApi', u.api]] },
 ];
});

const value = (u, key) => key === 'lastActive' ? (u.lastActive ? when(u.lastActive).getTime() : 0) : u[key];
// Busiest first, as the server sends them; whatever the order, people who did nothing in the period stay together at the end.
const people = computed(() => {
 const { key, desc } = sort.value;
 return [...(summary.value?.users ?? [])].sort((a, b) => (!a.requests - !b.requests) || (desc ? value(b, key) - value(a, key) : value(a, key) - value(b, key)));
});
function sortBy(key) { sort.value = sort.value.key === key ? { key, desc:!sort.value.desc } : { key, desc:true }; }
const ariaSort = key => sort.value.key === key ? (sort.value.desc ? 'descending' : 'ascending') : null;

async function loadSummary() { const p = period.value, s = await props.api(`/api/usage?period=${p}`); if (period.value === p) summary.value = s; }
async function loadPerson() {
 const id = person.value?.userId, p = period.value; if (!id) return;
 const u = await props.api(`/api/usage/users/${id}?period=${p}`);
 if (person.value?.userId === id && period.value === p) person.value = u;
}
async function loadRequests(reset) {
 const id = person.value?.userId; if (!id) return;
 const page = await props.api(`/api/usage/users/${id}/requests?skip=${reset ? 0 : requests.value.length}&take=${PAGE}`);
 if (person.value?.userId !== id) return;
 // A request made since the last page pushes the older ones down a place, so the next page can repeat one already shown.
 const seen = new Set(reset ? [] : requests.value.map(r => r.id));
 requests.value = [...(reset ? [] : requests.value), ...page.filter(r => !seen.has(r.id))];
 more.value = page.length === PAGE; requestsLoaded.value = true;
}
// While figures are reloaded the old ones stay in place, dimmed, so switching the period does not make the page jump.
async function run(fn) { await props.action(async () => { loading.value = true; try { await fn(); } finally { loading.value = false; } }); }
// The shell's action() drops a call made while another is running, so the first load waits its turn rather than leaving the page empty.
const idle = () => new Promise(resolve => { if (!props.busy) return resolve(); const stop = watch(() => props.busy, b => { if (!b) { stop(); resolve(); } }); });
onMounted(async () => { await idle(); await run(() => Promise.all([loadSummary(), props.api('/api/usage/daily-report').then(d => { daily.value = d; })])); });
async function choose(p) { if (p === period.value || props.busy) return; period.value = p; await run(() => Promise.all([loadSummary(), loadPerson()])); }
function refresh() { return run(() => Promise.all([loadSummary(), loadPerson(), person.value ? loadRequests(true) : null])); }
async function select(u) {
 if (props.busy) return;
 lastRow = u.userId; person.value = u; requests.value = []; requestsLoaded.value = false; more.value = false; expanded.value = new Set();
 window.scrollTo({ top:0 }); await nextTick(); heading.value?.focus();
 await run(() => Promise.all([loadPerson(), loadRequests(true)]));
}
// Back where the owner came from: the row they opened keeps the focus, so the keyboard does not start over at the top of the page.
async function back() { person.value = null; viewing.value = null; await nextTick(); document.getElementById(`usage-user-${lastRow}`)?.focus(); }
function loadMore() { return props.action(() => loadRequests(false)); }

const badge = status => BADGES[status] ?? ['warn', LoaderCircle];
const settled = status => status in BADGES;
const channel = c => CHANNELS[c] ?? CHANNELS.Web;
const imageOf = r => `/api/generations/${r.id}/image`;
const alt = r => r.prompt ? (r.prompt.length > 140 ? r.prompt.slice(0, 140).trimEnd() + '…' : r.prompt) : r.templateTitle || props.t('usageImage');
// Only a long prompt or a reply gets the toggle; a short one is already whole.
const long = r => (r.prompt?.length ?? 0) > 180 || (r.prompt?.split('\n').length ?? 0) > 3 || (r.status === 'Answered' && (r.replyText?.length ?? 0) > 120);
function toggle(r) { const next = new Set(expanded.value); if (!next.delete(r.id)) next.add(r.id); expanded.value = next; }
const dailyTime = computed(() => daily.value ? new Date(2000, 0, 1, daily.value.hour).toLocaleTimeString(props.locale, { hour:'2-digit', minute:'2-digit' }) : '');
// Whatever the server kept is put back in the box, so a refused change does not leave the switch showing something that is not so.
async function setDaily(e) {
 const box = e.target, enabled = box.checked;
 await props.action(async () => { daily.value = await props.api('/api/usage/daily-report', 'PUT', { enabled }); });
 box.checked = !!daily.value?.enabled;
}
</script>
<template>
 <div class="page usage-page">
  <div class="page-heading"><div><div class="eyebrow">{{ t('workspace') }} / {{ t('usage') }}</div><h1>{{ t('usageTitle') }}</h1><p>{{ t('usageIntro') }}</p></div></div>
  <div class="toolbar usage-toolbar">
   <h2 class="section-label">{{ t(periodKey) }} <span v-if="summary" class="muted">/ {{ since }}</span></h2>
   <span class="grow"/>
   <div class="segmented" role="group" :aria-label="t('usagePeriod')"><button v-for="[key,label] in PERIODS" :key="key" :class="{active:period===key}" :aria-pressed="period===key" :disabled="busy" @click="choose(key)">{{ t(label) }}</button></div>
   <button class="icon-button" :aria-label="t('usageRefresh')" :title="t('usageRefresh')" :disabled="busy" @click="refresh"><RefreshCw :class="{spin:loading}"/></button>
  </div>

  <div v-if="summary" class="usage-body" :class="{stale:loading}">
   <template v-if="!person">
    <div class="usage-stats"><div v-for="tile in overview" :key="tile.label" class="usage-stat"><span><component :is="tile.icon"/>{{ t(tile.label) }}</span><strong>{{ tile.value }}</strong><small v-if="tile.note" :class="{warn:tile.warn}"><AlertTriangle v-if="tile.warn"/>{{ tile.note }}</small></div></div>
    <div v-if="!summary.requests" class="empty-state usage-empty"><BarChart3/><h2>{{ t('usageEmptyTitle') }}</h2><p>{{ emptyText }}</p><button v-if="period!=='all'" class="primary" :disabled="busy" @click="choose('all')">{{ t('usageWiden') }}</button></div>
    <section v-else aria-labelledby="usage-people">
     <div class="toolbar"><h2 id="usage-people" class="section-label">{{ t('usagePeople') }} <span class="muted">/ {{ summary.users.length.toLocaleString(locale, { minimumIntegerDigits:2 }) }}</span></h2><span class="grow"/><small class="muted">{{ t('usagePeopleHint') }}</small></div>
     <div class="usage-table-wrap">
      <table class="usage-table">
       <caption class="sr-only">{{ t('usagePeople') }} — {{ t(periodKey) }}</caption>
       <thead><tr>
        <th scope="col">{{ t('usageUser') }}</th>
        <th v-for="[key,label] in COLUMNS" :key="key" scope="col" :aria-sort="ariaSort(key)"><button class="usage-sort" @click="sortBy(key)">{{ t(label) }}<component :is="sort.desc?ArrowDown:ArrowUp" v-if="sort.key===key"/></button></th>
        <th scope="col">{{ t('usageChannels') }}</th>
        <th scope="col" :aria-sort="ariaSort('lastActive')"><button class="usage-sort" @click="sortBy('lastActive')">{{ t('usageLastActive') }}<component :is="sort.desc?ArrowDown:ArrowUp" v-if="sort.key==='lastActive'"/></button></th>
       </tr></thead>
       <tbody>
        <tr v-for="u in people" :key="u.userId" :class="{idle:!u.requests}" @click="select(u)">
         <th scope="row"><button :id="`usage-user-${u.userId}`" class="usage-user" @click.stop="select(u)"><strong dir="auto">{{ u.username }}</strong><span v-if="u.role==='Owner'" class="badge"><ShieldCheck/>{{ t('Owner') }}</span><span v-if="u.isDisabled" class="badge"><UserX/>{{ t('usageDisabled') }}</span></button></th>
         <td :data-label="t('usageRequests')">{{ n(u.requests) }}</td>
         <td :data-label="t('usageImagesCol')">{{ n(u.images) }}</td>
         <td :data-label="t('usageFailed')" :class="{danger:u.failed}">{{ n(u.failed) }}</td>
         <td :data-label="t('usageFollowUps')">{{ n(u.followUps) }}</td>
         <td :data-label="t('usageCreditsCol')">{{ n(u.creditsSpent) }}</td>
         <td :data-label="t('usageChannels')"><span class="usage-channels"><template v-for="(c,name) in CHANNELS" :key="name"><span v-if="u[c[2]]" :title="t(c[1])"><component :is="c[0]" aria-hidden="true"/><span class="sr-only">{{ t(c[1]) }}</span>{{ n(u[c[2]]) }}</span></template><span v-if="!u.requests" class="muted">—</span></span></td>
         <td :data-label="t('usageLastActive')"><time v-if="u.lastActive" :datetime="u.lastActive" :title="dateTime(u.lastActive)">{{ ago(u.lastActive) }}</time><span v-else class="muted">{{ t('never') }}</span></td>
        </tr>
       </tbody>
      </table>
     </div>
    </section>
   </template>

   <section v-else class="usage-person" aria-labelledby="usage-person-name">
    <button class="usage-back" @click="back"><ArrowLeft class="directional"/>{{ t('usageAllUsers') }}</button>
    <div class="usage-person-head">
     <h2 id="usage-person-name" ref="heading" tabindex="-1" dir="auto">{{ person.username }}</h2>
     <span class="badge"><ShieldCheck v-if="person.role==='Owner'"/>{{ t(person.role) }}</span><span v-if="person.isDisabled" class="badge"><UserX/>{{ t('usageDisabled') }}</span>
     <span class="grow"/>
     <small>{{ t('usageLastActive') }}: <time v-if="person.lastActive" :datetime="person.lastActive">{{ dateTime(person.lastActive) }}</time><template v-else>{{ t('never') }}</template></small>
    </div>
    <div class="usage-stats"><div v-for="tile in figures" :key="tile.label" class="usage-stat"><span><component :is="tile.icon"/>{{ t(tile.label) }}</span><strong>{{ tile.value }}</strong><small v-if="tile.note" :class="{warn:tile.warn}"><AlertTriangle v-if="tile.warn"/>{{ tile.note }}</small></div></div>
    <div class="usage-breakdown">
     <div v-for="g in groups" :key="g.title" class="usage-group"><h3 class="section-label">{{ t(g.title) }}</h3><dl><template v-for="[label,v] in g.rows" :key="label"><dt>{{ t(label) }}</dt><dd>{{ n(v) }}</dd></template></dl></div>
     <div class="usage-group"><h3 class="section-label">{{ t('usageTopTemplates') }}</h3><dl v-if="person.templates.length"><template v-for="tp in person.templates" :key="tp.title"><dt dir="auto">{{ tp.title }}</dt><dd>{{ fill('usageTimes', { n:n(tp.count) }) }}</dd></template></dl><p v-else class="usage-none">{{ t('usageNoTemplates') }}</p></div>
    </div>

    <section aria-labelledby="usage-requests-title">
     <div class="toolbar"><h3 id="usage-requests-title" class="section-label">{{ t('usageRequestsList') }}</h3><span class="grow"/><small class="muted">{{ t('usageRequestsHelp') }}</small></div>
     <div v-if="requestsLoaded&&!requests.length" class="accounts-empty"><MessagesSquare/><span>{{ t('usageNoRequests') }}</span></div>
     <ol v-else-if="requests.length" class="usage-requests">
      <li v-for="r in requests" :key="r.id" class="usage-request" :class="{open:expanded.has(r.id)}">
       <button v-if="r.hasImage" class="usage-thumb" :aria-label="t('usageOpenImage')" :title="t('usageOpenImage')" @click="viewing=r"><img :src="imageOf(r)" :alt="alt(r)" loading="lazy"></button>
       <span v-else class="usage-thumb empty" :class="badge(r.status)[0]" aria-hidden="true"><component :is="badge(r.status)[1]" :class="{spin:!settled(r.status)}"/></span>
       <div class="usage-request-body">
        <div class="usage-request-meta"><time :datetime="r.createdAt">{{ dateTime(r.createdAt) }}</time><span aria-hidden="true">·</span><span class="usage-project"><FolderOpen/><bdi class="truncate">{{ r.projectTitle }}</bdi></span><span aria-hidden="true">·</span><span>{{ t(r.operation) }}</span></div>
        <p v-if="r.prompt" class="prompt-text" dir="auto">{{ r.prompt }}</p>
        <p v-if="r.status==='Answered'&&r.replyText" class="usage-reply" dir="auto">{{ r.replyText }}</p>
        <p v-if="r.status==='Failed'&&r.errorMessage" class="error-text usage-error">{{ t(r.errorMessage) }} <code v-if="t(r.errorMessage)!==r.errorMessage" dir="ltr">{{ r.errorMessage }}</code></p>
        <footer>
         <span class="badge" :class="badge(r.status)[0]"><component :is="badge(r.status)[1]" :class="{spin:!settled(r.status)}"/>{{ t(r.status) }}</span>
         <span class="badge"><component :is="channel(r.channel)[0]"/>{{ t(channel(r.channel)[1]) }}</span>
         <span v-if="r.templateTitle" class="template-chip"><LayoutTemplate/><span>{{ t('templateLabel') }}: <bdi>{{ r.templateTitle }}</bdi></span></span>
         <span class="usage-cost"><Layers/>{{ fill('usageCost', { n:n(r.creditCost) }) }}</span>
         <span class="grow"/>
         <button v-if="long(r)" class="usage-toggle" :aria-expanded="expanded.has(r.id)" @click="toggle(r)"><ChevronDown/>{{ t(expanded.has(r.id)?'usageLess':'usageMore') }}</button>
        </footer>
       </div>
      </li>
     </ol>
     <div v-if="more" class="usage-more"><button :disabled="busy" @click="loadMore">{{ t('usageLoadMore') }}</button></div>
    </section>
   </section>
  </div>

  <section v-if="daily&&!person" class="settings-panel usage-daily">
   <h2>{{ t('usageDaily') }}</h2><p>{{ t('usageDailyHelp') }}</p>
   <label class="check-row"><input type="checkbox" role="switch" :checked="daily.enabled" :disabled="busy" @change="setDaily">{{ fill('usageDailySwitch', { time:dailyTime }) }}</label>
   <p v-if="!daily.telegram" class="dev-note"><Send/><span>{{ t('usageDailyNoBot') }}</span></p>
  </section>

  <Modal v-if="viewing" :title="viewing.projectTitle" :close-label="t('close')" @close="viewing=null">
   <img class="preview-full" :src="imageOf(viewing)" :alt="alt(viewing)">
   <p v-if="viewing.prompt" class="usage-modal-prompt" dir="auto">{{ viewing.prompt }}</p>
   <footer class="modal-actions"><a class="button" :href="imageOf(viewing)" :download="`loomi-${viewing.id}`"><Download/>{{ t('download') }}</a></footer>
  </Modal>
 </div>
</template>
