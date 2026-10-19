<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import { AlertTriangle, CalendarClock, CircleX, CircleCheck, MonitorSmartphone, BarChart3, ChevronRight } from 'lucide-vue-next';
import DailyChart from './DailyChart.vue';
import { t, tf, n, api, go, date, when, daysLeft, onTick } from '../../core';
import { pendingUser, pkgName, dayMonth, percent, initial, bidi, days } from './kit';
// The first page of management: the period's figures, the last fourteen days, what is waiting on the owner, the accounts and the busiest people.
const PERIODS = [['today', 'usageToday'], ['week', 'usageWeek'], ['month', 'usageMonth'], ['all', 'usageAll']];
const period = ref('today'), summary = ref(null), daily = ref(null), accounts = ref(null), users = ref(null), loading = ref(false);

// Each part degrades on its own: a route this server does not have yet leaves its card empty instead of the page. The shell's beat reloads quietly,
// so the figures do not dim every few seconds; only a change of period shows the old ones dimmed while the new ones come.
async function load(quiet) {
 const p = period.value; if (!quiet) loading.value = true;
 try {
  const [s, d, a, u] = await Promise.all([api(`/api/usage?period=${p}`).catch(() => null), api('/api/usage/daily?days=14').catch(() => []),
   api('/api/auth/accounts').catch(() => []), api('/api/users').catch(() => [])]);
  if (p !== period.value) return;
  summary.value = s; daily.value = Array.isArray(d) ? d : []; accounts.value = a || []; users.value = u || [];
 } finally { if (p === period.value) loading.value = false; }
}
function choose(p) { if (p === period.value) return; period.value = p; load(); }
onMounted(() => load());
let beat = 0;
const stop = onTick(() => { if (++beat % 3 === 0 && !loading.value) load(true); });
onBeforeUnmount(stop);

const heading = computed(() => {
 if (period.value === 'today') return tf('ovTodayIs', date(new Date().toISOString()));
 if (period.value === 'all') return t('usageAllTime');
 return summary.value?.since ? tf('ovSince', date(summary.value.since)) : '';
});
const tiles = computed(() => {
 const s = summary.value; if (!s) return [];
 return [
  { label: 'usageRequests', value: n(s.requests), note: s.failed ? tf('ovFailedShare', s.failed, percent(s.failed, s.requests)) : s.requests ? t('usageNoneFailed') : '', warn: s.failed > 0 },
  { label: 'usageImages', value: n(s.images), note: s.requests ? tf('ovImagesShare', percent(s.images, s.requests)) : '' },
  { label: 'usageActiveUsers', value: n(s.activeUsers), note: tf('ovOfPeople', s.users.length) },
  { label: 'usageCreditsSpent', value: n(s.creditsSpent), note: t('usageNetOfRefunds') },
 ];
});
const chartReady = computed(() => (daily.value || []).length > 0);
const chartTotal = computed(() => (daily.value || []).reduce((sum, d) => sum + d.requests, 0));

function openUser(id, tab) { pendingUser.value = { id, tab }; go('admin', 'users'); }
// Today's failures whatever period is shown: the last day of the chart is today, and with no chart the today summary says it.
const failedToday = computed(() => chartReady.value ? daily.value.at(-1).failed : period.value === 'today' ? summary.value?.failed ?? 0 : 0);
const ACCOUNT_TITLE = { LoginRequired: 'ovAccLogin', VerificationRequired: 'ovAccVerify', Disconnected: 'ovAccOff' };
const attention = computed(() => {
 const items = [];
 if (accounts.value && !accounts.value.length) items.push({ key: 'none', sev: 'bad', icon: MonitorSmartphone, title: t('ovNoAccount'), sub: t('ovNoAccountHelp'), label: t('ovOpenAccounts'), go: () => go('admin', 'accounts') });
 for (const a of accounts.value || []) {
  if (!a.isEnabled || (a.state === 'Connected' && !a.spentToday)) continue;
  const spent = a.state === 'Connected';
  items.push({ key: 'a' + a.id, sev: spent ? 'warn' : 'bad', icon: AlertTriangle, title: tf(spent ? 'ovAccSpent' : ACCOUNT_TITLE[a.state] || 'ovAccOff', bidi(a.label)), sub: t(spent ? 'ovAccSpentHelp' : 'ovAccHelp'), label: t('ovOpenAccounts'), go: () => go('admin', 'accounts') });
 }
 for (const u of users.value || []) for (const p of u.packages || []) {
  if (p.state !== 'Active' || daysLeft(p) > 3) continue;
  const left = daysLeft(p), midnight = new Date(); midnight.setHours(24, 0, 0, 0);
  items.push({ key: 'p' + p.id, sev: 'info', icon: CalendarClock, title: when(p.endsAt) <= midnight ? tf('ovPkgLastDay', bidi(u.username)) : tf('ovPkgEnding', bidi(u.username), days(left)), sub: bidi(pkgName(p)) + t('adSep') + tf('pkUntil', dayMonth(p.endsAt)), label: t('pkExtend'), go: () => openUser(u.id, 'packages') });
 }
 if (failedToday.value) items.push({ key: 'failed', sev: 'bad', icon: CircleX, title: tf('ovFailedToday', failedToday.value), sub: t('ovFailedHelp'), label: t('ovSee'), ghost: true, go: () => go('admin', 'users') });
 return items;
});
const dot = a => !a.isEnabled ? 'off' : a.state === 'Connected' && !a.spentToday ? 'ok' : a.state === 'Connected' ? 'warn' : 'bad';
const accountNote = a => !a.isEnabled ? t('usageDisabled') : a.busy ? t('busy') : a.spentToday ? t('spentToday') : a.state !== 'Connected' ? t(a.state)
 : a.usedToday != null ? tf('ovAccUse', a.usedToday, a.dailyCap) : tf('ovAccCap', a.dailyCap);
// The five busiest of the period, and on request everyone who sent anything, busiest first as the server orders them.
const everyone = ref(false);
const active = computed(() => (summary.value?.users || []).filter(u => u.requests > 0));
const top5 = computed(() => everyone.value ? active.value : active.value.slice(0, 5));
const periodLabel = computed(() => t(PERIODS.find(([k]) => k === period.value)[1]));
</script>

<template>
 <div class="view overview">
  <header class="view-head">
   <div><h1>{{ t('shOverview') }}</h1><p>{{ heading }}</p></div>
   <div class="seg" role="group" :aria-label="t('usagePeriod')"><button v-for="[key, label] in PERIODS" :key="key" :aria-pressed="period===key" @click="choose(key)">{{ t(label) }}</button></div>
  </header>

  <div class="tiles" :class="{ stale: loading && summary }">
   <template v-if="summary"><div v-for="x in tiles" :key="x.label" class="tile"><small>{{ t(x.label) }}</small><strong>{{ x.value }}</strong><em :class="{ warn: x.warn }">{{ x.note }}</em></div></template>
   <template v-else><div v-for="i in 4" :key="i" class="skel" style="height:96px"></div></template>
  </div>

  <div class="grid2">
   <section class="card" aria-labelledby="ov-chart">
    <div class="card-head"><h2 id="ov-chart">{{ t('ovChartTitle') }}</h2><span class="grow"/><small class="faint-text">{{ chartReady ? tf('ovChartTotal', chartTotal) : '' }}</small></div>
    <DailyChart v-if="chartReady" :days="daily"/>
    <div v-else-if="daily" class="quiet"><BarChart3/><span>{{ t('ovNoChart') }}</span></div>
    <div v-else class="skel" style="height:200px"></div>
   </section>

   <section class="card" aria-labelledby="ov-att">
    <div class="card-head"><h2 id="ov-att">{{ t('ovAttention') }}</h2><span class="grow"/><small v-if="attention.length" class="faint-text">{{ tf('ovItems', attention.length) }}</small></div>
    <div v-if="!accounts" class="skel" style="height:120px"></div>
    <div v-else-if="!attention.length" class="quiet ok"><CircleCheck/><span>{{ t('ovAllGood') }}</span></div>
    <ul v-else class="att">
     <li v-for="x in attention" :key="x.key">
      <span class="sev" :class="x.sev"><component :is="x.icon"/></span>
      <span class="txt"><span>{{ x.title }}</span><small>{{ x.sub }}</small></span>
      <button class="btn btn-sm" :class="{ 'btn-ghost': x.ghost }" @click="x.go">{{ x.label }}</button>
     </li>
    </ul>
   </section>
  </div>

  <div class="grid2">
   <section class="card" aria-labelledby="ov-acc">
    <div class="card-head"><h2 id="ov-acc">{{ t('shAccounts') }}</h2><span class="grow"/><button class="btn btn-ghost btn-sm" @click="go('admin', 'accounts')">{{ t('ovManage') }}<ChevronRight class="chev"/></button></div>
    <div v-if="!accounts" class="skel" style="height:80px"></div>
    <p v-else-if="!accounts.length" class="faint-text">{{ t('noAccounts') }}</p>
    <ul v-else class="accs">
     <li v-for="a in accounts" :key="a.id" :class="{ off: !a.isEnabled }">
      <i class="state" :class="dot(a)" aria-hidden="true"></i>
      <span class="name"><bdi>{{ a.label }}</bdi><span v-if="a.usedToday != null && a.dailyCap" class="bar" :class="{ 'bar-warn': a.usedToday >= a.dailyCap }"><i :style="{ width: Math.min(100, a.usedToday / a.dailyCap * 100) + '%' }"></i></span></span>
      <small class="num">{{ accountNote(a) }}</small>
     </li>
    </ul>
   </section>

   <section class="card" aria-labelledby="ov-top">
    <div class="card-head"><h2 id="ov-top">{{ t('ovTop') }}</h2><span class="grow"/><small class="faint-text">{{ periodLabel }}</small></div>
    <div v-if="!summary" class="skel" style="height:80px"></div>
    <p v-else-if="!top5.length" class="faint-text">{{ t('ovNobody') }}</p>
    <ol v-else class="top">
     <li v-for="u in top5" :key="u.userId"><button class="who" @click="openUser(u.userId, 'summary')"><span class="av">{{ initial(u.username) }}</span><span class="grow line"><bdi class="truncate">{{ u.username }}</bdi><small>{{ tf('ovTopLine', u.images, u.failed, u.creditsSpent) }}</small></span><b class="num">{{ n(u.requests) }}</b></button></li>
    </ol>
    <button v-if="active.length > 5" class="btn btn-ghost btn-sm more" @click="everyone = !everyone">{{ everyone ? t('acShowLess') : tf('ovTopAll', active.length) }}</button>
   </section>
  </div>
 </div>
</template>

<style scoped>
.view-head>.seg{flex-direction:row;gap:2px}
.tiles{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;transition:opacity .15s}
.stale{opacity:.6}
.tile em.warn{color:var(--warn)}
.grid2{display:grid;grid-template-columns:minmax(0,1.5fr) minmax(0,1fr);gap:18px;align-items:start}
.card h2{font-size:15.5px}
.quiet{display:flex;align-items:center;gap:10px;padding:18px 4px;color:var(--muted);font-size:13.5px}
.quiet svg{color:var(--faint)}
.quiet.ok svg{color:var(--ok)}
.att,.accs,.top{list-style:none;margin:0;padding:0;display:flex;flex-direction:column}
.att li{display:grid;grid-template-columns:26px minmax(0,1fr) auto;gap:10px;align-items:center;padding:10px 0;border-top:1px solid var(--line);font-size:13.5px}
.att li:first-child{border-top:0;padding-top:0}
.sev{width:26px;height:26px;border-radius:50%;display:grid;place-items:center}
.sev svg{width:15px;height:15px}
.sev.warn{background:var(--warn-soft);color:var(--warn)}
.sev.bad{background:var(--bad-soft);color:var(--bad)}
.sev.info{background:var(--accent-soft);color:var(--accent)}
.txt{display:flex;flex-direction:column;min-width:0;line-height:1.5}
.txt small{color:var(--muted);font-size:12px}
.accs li{display:grid;grid-template-columns:10px minmax(0,1fr) auto;gap:10px;align-items:center;padding:10px 0;border-top:1px solid var(--line);font-size:13.5px}
.accs li:first-child{border-top:0;padding-top:2px}
.accs li.off{color:var(--faint)}
.accs small{color:var(--muted);font-size:12.5px}
.name{display:flex;flex-direction:column;gap:5px;min-width:0}
.state{width:9px;height:9px;border-radius:50%;background:var(--faint)}
.state.ok{background:var(--ok)}
.state.warn{background:var(--warn)}
.state.bad{background:var(--bad)}
.top li+li{border-top:1px solid var(--line)}
.who{display:flex;align-items:center;gap:10px;width:100%;padding:7px 4px;border:0;border-radius:var(--r2);background:none;color:var(--text);text-align:start;font-size:13.5px}
.who:hover{background:var(--s2)}
.who .av{width:28px;height:28px;font-size:12px}
.line{display:flex;flex-direction:column;min-width:0;line-height:1.4}
.line small{color:var(--faint);font-size:12px}
.more{align-self:flex-start}
.chev{width:15px!important;height:15px!important}
:root[dir=rtl] .chev{transform:scaleX(-1)}
@media (max-width:1100px){.tiles{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media (max-width:960px){.grid2{grid-template-columns:minmax(0,1fr)}}
@media (max-width:860px){.view-head .seg{overflow-x:auto;max-width:100%}}
</style>
