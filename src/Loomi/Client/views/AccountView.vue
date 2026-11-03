<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import { Send, Languages, LockKeyhole, Code, LogOut, ChevronRight, LayoutDashboard, Images, Moon, Sun, ShieldCheck, Package, History } from 'lucide-vue-next';
import Modal from '../components/Modal.vue';
import TelegramConnect from '../components/TelegramConnect.vue';
import ChangePassword from '../components/ChangePassword.vue';
import DevTools from './account/DevTools.vue';
import PackageRow from './admin/PackageRow.vue';
import { t, tf, n, lang, theme, me, isOwner, quota, packages, activePackages, payingPackage, api, action, logout, go, ago, when, onTick } from '../core';
import { pkgName, bySpendOrder, dayMonth, totalDays, days, entryText, signed, initial, bidi } from './admin/kit';
// My account, for everyone: what there is to spend, the packages with their days left, what moved and why, and the personal settings in one short page.
const open = ref(null), tg = ref(null), tokens = ref(null), ledger = ref(null), everything = ref(false);
const member = computed(() => me.value?.role === 'Member');

// ---- The wallet: regular credits, and how today's images are paid ----
const paying = computed(() => payingPackage.value);
const free = computed(() => paying.value && paying.value.kind !== 'Credits');
const today = computed(() => {
 const p = paying.value;
 if (p && p.kind !== 'Credits') return { value: t('acFree'), tone: 'free', line: tf('acFreeVia', bidi(pkgName(p))) + t('adSep') + (p.amount ? tf('shTodayOf', p.usedToday, p.amount) : tf('shTodayCount', p.usedToday)) };
 if (p) return { value: t('acFromPackage'), tone: '', line: tf('acPackageLeft', p.remaining ?? p.amount, bidi(pkgName(p)), dayMonth(p.endsAt)) };
 if (activePackages.value.length) return { value: t('acFromCredits'), tone: '', line: t('acPackagesSpent') };
 return { value: t('acFromCredits'), tone: '', line: (me.value?.credits ?? 0) > 0 ? t('acEachImage') : t('acNoCredits') };
});
const quotaUsed = computed(() => quota.value ? Math.min(100, Math.round(quota.value.dailyUsed / Math.max(1, quota.value.dailyQuota) * 100)) : 0);

// ---- The packages: running ones in the order they are spent, then those waiting, then the recently ended ----
const mine = computed(() => [...packages.value].sort(bySpendOrder));

// ---- History: the credit ledger and the packages' own beginnings and ends, newest first ----
async function loadLedger() { try { ledger.value = (await api('/api/credits')).entries || []; } catch { ledger.value ??= []; } }
const history = computed(() => {
 const rows = (ledger.value || []).map(e => ({ key: e.id, at: e.createdAt, text: entryText(e), amount: signed(e.amount), tone: e.amount > 0 ? 'plus' : e.packageName ? 'pkg' : '' }));
 for (const p of packages.value) {
  const queued = when(p.startsAt) - when(p.createdAt) > 300000;
  rows.push({ key: p.id + ':start', at: p.createdAt, text: tf(queued ? 'acPkgQueued' : 'acPkgStarted', bidi(pkgName(p)), dayMonth(p.startsAt)), amount: days(totalDays(p)), tone: 'ex' });
  if (p.state === 'Ended') rows.push({ key: p.id + ':end', at: p.endsAt, text: tf(p.endedEarly ? 'acPkgClosed' : 'acPkgFinished', bidi(pkgName(p))), amount: '', tone: 'ex' });
 }
 return rows.sort((a, b) => when(b.at) - when(a.at));
});
const shown = computed(() => everything.value ? history.value : history.value.slice(0, 8));

// ---- Settings: one line of state under each, so the page says how things stand before anything is opened ----
const tgLine = computed(() => !tg.value ? t('shLoading') : tg.value.linked ? t('acTgLinked') : tg.value.configured ? t('acTgNotLinked') : t('acTgOff'));
const devLine = computed(() => tokens.value?.length ? tf('acTokensCount', tokens.value.length) : t('acDevHelp'));
async function loadTokens() { try { tokens.value = await api('/api/tokens'); } catch { tokens.value ??= []; } }
async function loadTg() { try { tg.value = await api('/api/telegram/link'); } catch { /* the row keeps saying it is loading; the sheet itself reports the error */ } }

onMounted(() => { loadLedger(); loadTokens(); loadTg(); });
// The ledger moves when a request is charged or refunded, which happens on the server; the shell's beat keeps it current.
let beat = 0;
const stop = onTick(() => { if (++beat % 3 === 0) loadLedger(); });
onBeforeUnmount(stop);
// Read again on closing: what changed inside the sheet may have finished after it was put away.
const close = () => { open.value = null; loadTg(); loadTokens(); };
const DIALOGS = { telegram: 'telegram', password: 'changePassword', dev: 'acDev' };
</script>

<template>
 <div class="view account">
  <header class="view-head head">
   <span class="av av-lg" aria-hidden="true">{{ initial(me?.username) }}</span>
   <div><span class="eyebrow">{{ t(isOwner ? 'shOwner' : 'shMember') }}</span><h1>{{ t('shAccount') }}</h1><p><bdi>{{ me?.username }}</bdi></p></div>
  </header>

  <div class="cols">
   <div class="main">
    <section v-if="member" class="card wallet" aria-labelledby="ac-wallet">
     <h2 id="ac-wallet" class="sr-only">{{ t('acWallet') }}</h2>
     <div class="bal">
      <div><small>{{ t('acRegular') }}</small><strong class="num">{{ n(me.credits) }}</strong><em>{{ t('acRegularHelp') }}</em></div>
      <div><small>{{ t('acToday') }}</small><strong class="word" :class="today.tone">{{ today.value }}</strong><em>{{ today.line }}</em></div>
     </div>
     <div v-if="quota" class="quota">
      <div class="quota-line"><span>{{ t('acQuota') }}</span><span class="num">{{ tf('shTodayOf', quota.dailyUsed, quota.dailyQuota) }}</span></div>
      <div class="bar" :class="{ 'bar-warn': quota.dailyRemaining === 0 }" role="meter" aria-valuemin="0" :aria-valuemax="quota.dailyQuota" :aria-valuenow="quota.dailyUsed" :aria-label="t('acQuota')"><i :style="{ width: quotaUsed + '%' }"></i></div>
      <small class="faint-text">{{ quota.dailyRemaining === 0 ? t('acQuotaDone') : tf('acQuotaLeft', quota.dailyRemaining) }}</small>
     </div>
    </section>
    <section v-else class="card wallet owner">
     <p class="notice notice-ok"><ShieldCheck/><span>{{ t('shOwnerFree') }}</span></p>
    </section>

    <section v-if="member" class="card pkgs" aria-labelledby="ac-pkgs">
     <div class="card-head"><h2 id="ac-pkgs"><Package/>{{ t('acPackages') }}</h2><span class="grow"/><small class="faint-text">{{ t('acPackagesOrder') }}</small></div>
     <p v-if="!mine.length" class="faint-text">{{ t('acNoPackages') }}</p>
     <div v-else class="pkg-list"><PackageRow v-for="p in mine" :key="p.id" :pkg="p"/></div>
    </section>

    <section class="card hist" aria-labelledby="ac-hist">
     <div class="card-head"><h2 id="ac-hist"><History/>{{ t('acHistory') }}</h2></div>
     <div v-if="ledger === null" class="skel" style="height:120px"></div>
     <p v-else-if="!history.length" class="faint-text">{{ t('acNoHistory') }}</p>
     <ol v-else class="ledger">
      <li v-for="r in shown" :key="r.key"><time class="faint-text" :datetime="r.at">{{ ago(r.at) }}</time><span>{{ r.text }}</span><b class="num" :class="r.tone">{{ r.amount }}</b></li>
     </ol>
     <button v-if="history.length > 8" class="btn btn-ghost btn-sm more" @click="everything=!everything">{{ t(everything ? 'acShowLess' : 'acShowAll') }}</button>
    </section>
   </div>

   <div class="side">
    <button v-if="isOwner" class="card entry" @click="go('admin')">
     <span class="ic"><LayoutDashboard/></span>
     <span class="grow txt"><b>{{ t('shAdmin') }}</b><small>{{ t('acAdminHelp') }}</small></span>
     <ChevronRight class="chev"/>
    </button>

    <section class="card settings" aria-labelledby="ac-settings">
     <h2 id="ac-settings">{{ t('settings') }}</h2>
     <div class="set">
      <button class="srow" @click="open='telegram'"><span class="ic"><Send/></span><span class="txt"><b>{{ t('telegram') }}</b><small>{{ tgLine }}</small></span><ChevronRight class="chev"/></button>
      <div class="srow static">
       <span class="ic"><Languages/></span>
       <span class="txt"><b>{{ t('acLookFeel') }}</b>
        <span class="prefs">
         <span class="seg" role="group" :aria-label="t('shLanguage')"><button :aria-pressed="lang==='fa'" @click="lang='fa'">فارسی</button><button :aria-pressed="lang==='en'" @click="lang='en'">English</button></span>
         <span class="seg" role="group" :aria-label="t('shTheme')"><button :aria-pressed="theme==='dark'" @click="theme='dark'"><Moon/>{{ t('shDark') }}</button><button :aria-pressed="theme==='light'" @click="theme='light'"><Sun/>{{ t('shLight') }}</button></span>
        </span>
       </span>
      </div>
      <button class="srow" @click="open='password'"><span class="ic"><LockKeyhole/></span><span class="txt"><b>{{ t('acPassword') }}</b><small>{{ t(me?.hasPassword ? 'acPasswordHelp' : 'suSetPasswordRow') }}</small></span><ChevronRight class="chev"/></button>
      <button class="srow" @click="open='dev'"><span class="ic"><Code/></span><span class="txt"><b>{{ t('acDev') }}</b><small>{{ devLine }}</small></span><ChevronRight class="chev"/></button>
      <button class="srow phone-only" @click="go('gallery')"><span class="ic"><Images/></span><span class="txt"><b>{{ t('shMyImages') }}</b><small>{{ t('acImagesHelp') }}</small></span><ChevronRight class="chev"/></button>
      <button class="srow out" @click="action(logout)"><span class="ic"><LogOut/></span><span class="txt"><b>{{ t('shLogout') }}</b><small>{{ t('acLogoutHelp') }}</small></span><ChevronRight class="chev"/></button>
     </div>
    </section>
   </div>
  </div>

  <Modal v-if="open" :title="t(open==='password' && !me?.hasPassword ? 'suSetPassword' : DIALOGS[open])" :close-label="t('close')" :size="open==='dev' ? 'lg' : 'md'" @close="close">
   <TelegramConnect v-if="open==='telegram'" @state="tg=$event"/>
   <ChangePassword v-else-if="open==='password'" cancellable @cancel="close"/>
   <DevTools v-else-if="open==='dev'" :tokens="tokens" @reload="loadTokens"/>
  </Modal>
 </div>
</template>

<style scoped>
.head{justify-content:flex-start;align-items:center;gap:14px}
.head>div{gap:2px}
.cols{display:grid;grid-template-columns:minmax(0,1.5fr) minmax(0,1fr);gap:18px;align-items:start}
.main,.side{display:flex;flex-direction:column;gap:18px;min-width:0}
.card h2{font-size:15.5px}
.card-head h2 svg{color:var(--accent)}
.bal{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:16px}
.bal>div{display:flex;flex-direction:column;gap:2px;min-width:0}
.bal small{color:var(--muted);font-size:12.5px}
.bal strong{font-size:30px;font-weight:700;line-height:1.25}
.bal strong.word{font-size:21px;line-height:1.75}
.bal strong.free{color:var(--ok)}
.bal em{font-style:normal;font-size:12.5px;color:var(--muted);line-height:1.6}
.quota{display:flex;flex-direction:column;gap:6px;padding-top:14px;border-top:1px solid var(--line)}
.quota-line{display:flex;justify-content:space-between;gap:10px;font-size:13.5px}
.owner .notice{margin:0}
.pkg-list{display:flex;flex-direction:column}
.pkg-list>*+*{border-top:1px solid var(--line);margin-top:12px;padding-top:12px}
.ledger{list-style:none;margin:0;padding:0;display:flex;flex-direction:column}
.ledger li{display:grid;grid-template-columns:96px minmax(0,1fr) auto;gap:10px;align-items:baseline;padding:9px 0;border-top:1px solid var(--line);font-size:13.5px}
.ledger li:first-child{border-top:0;padding-top:0}
.ledger time{font-size:12px}
.ledger b{font-weight:600;text-align:end;white-space:nowrap}
.ledger .plus{color:var(--ok)}
.ledger .ex,.ledger .pkg{color:var(--muted);font-weight:500}
.more{align-self:flex-start}
.entry{flex-direction:row;align-items:center;gap:14px;text-align:start;cursor:pointer;border-color:var(--accent-line);background:linear-gradient(135deg,var(--accent-soft),var(--s1) 70%);color:var(--text)}
.entry:hover{border-color:var(--accent)}
.entry .ic{width:42px;height:42px;border-radius:var(--r2);background:var(--accent);color:var(--accent-ink);display:grid;place-items:center;flex:none}
.txt{display:flex;flex-direction:column;min-width:0;line-height:1.45}
.txt b{font-size:14px;font-weight:600}
.txt small{color:var(--muted);font-size:12.5px}
.chev{color:var(--faint);flex:none}
:root[dir=rtl] .chev{transform:scaleX(-1)}
.set{display:flex;flex-direction:column;margin-top:-4px}
.srow{display:grid;grid-template-columns:36px minmax(0,1fr) 18px;gap:12px;align-items:center;padding:11px 0;border:0;border-top:1px solid var(--line);background:none;color:var(--text);text-align:start;width:100%;font:inherit}
.set>.srow:first-child{border-top:0}
button.srow:hover .ic{background:var(--s3);color:var(--text)}
button.srow:hover b{color:var(--accent)}
.srow .ic{width:36px;height:36px;border-radius:var(--r2);background:var(--s2);color:var(--muted);display:grid;place-items:center;transition:background .15s,color .15s}
.srow.static{grid-template-columns:36px minmax(0,1fr);align-items:start}
.prefs{display:flex;flex-wrap:wrap;gap:8px;margin-top:8px}
.out b{color:var(--bad)}
button.srow.out:hover b{color:var(--bad)}
.phone-only{display:none}
@media (max-width:860px){
 .cols{display:flex;flex-direction:column;gap:16px}
 .main,.side{display:contents}
 .wallet{order:1}.pkgs{order:2}.entry{order:3}.settings{order:4}.hist{order:5}
 .phone-only{display:grid}
 .ledger li{grid-template-columns:minmax(0,1fr) auto}
 .ledger time{grid-column:1/-1;margin-bottom:-6px}
}
@media (max-width:520px){
 .bal{grid-template-columns:1fr;gap:14px}
}
</style>
