<script setup>
import { ref, computed, watch, nextTick, onMounted, onBeforeUnmount } from 'vue';
import { X, ShieldCheck, LockKeyhole, KeyRound, Trash2, Coins, CircleCheck, AlertTriangle, Gauge } from 'lucide-vue-next';
import Modal from '../../components/Modal.vue';
import DrawerPackages from './DrawerPackages.vue';
import RequestList from './RequestList.vue';
import { t, tf, n, api, action, ago, date } from '../../core';
import { isYou, limited, percent, initial, entryText, signed, bidi } from './kit';
// Everything about one person in a side sheet at the inline end, full screen on a phone: their figures and settings, packages, credits, requests
// and security. It stays open over the table, so the owner can move from one person to the next without losing the list.
const props = defineProps({ user: { type: Object, required: true }, tab: { type: String, default: 'summary' } });
const emit = defineEmits(['close', 'changed', 'deleted']);
const TABS = [['summary', 'drSummary'], ['packages', 'drPackages'], ['credits', 'drCredits'], ['usage', 'drUsage'], ['security', 'drSecurity']];
const current = ref(props.tab), panel = ref(null), closer = ref(null), tablist = ref(null);
const u = computed(() => props.user), you = computed(() => isYou(u.value)), owner = computed(() => u.value.role === 'Owner');

// ---- Summary: the last 30 days' figures, and the account's own settings ----
const usage = ref(null), quota = ref(0);
async function loadUsage() { const id = u.value.id; try { const x = await api(`/api/usage/users/${id}?period=month`); if (id === u.value.id) usage.value = x; } catch { if (id === u.value.id) usage.value = false; } }
const figures = computed(() => {
 const x = usage.value; if (!x) return [];
 return [
  { label: 'usageRequests', value: n(x.requests), note: x.failed ? tf('ovFailedShare', x.failed, percent(x.failed, x.requests)) : x.requests ? t('usageNoneFailed') : '' },
  { label: 'usageImages', value: n(x.images), note: x.requests ? tf('ovImagesShare', percent(x.images, x.requests)) : '' },
  { label: 'usageCreditsSpent', value: n(x.creditsSpent), note: x.creditsLeft != null ? tf('drCreditsLeft', x.creditsLeft) : t('usageNotCharged') },
 ];
});
const groups = computed(() => {
 const x = usage.value; if (!x) return [];
 const running = x.requests - x.images - x.answers - x.failed - x.cancelled;
 return [
  { title: 'usageOutcome', rows: [['usageImagesCol', x.images], ['usageAnswers', x.answers], ['usageFailed', x.failed], ['usageCancelled', x.cancelled], ...(running > 0 ? [['usageInProgress', running]] : [])] },
  { title: 'usageChannels', rows: [['channelWeb', x.web], ['channelBot', x.bot], ['channelApi', x.api]] },
 ];
});
// Every change goes through the shell's action(), so a refusal (the last owner, say) lands in the same toast as the rest of the app.
async function update(changes) {
 await action(async () => { await api(`/api/users/${u.value.id}`, 'PATCH', changes); emit('changed'); });
}
async function setEnabled(e) { const box = e.target, on = box.checked; await update({ isDisabled: !on }); box.checked = !u.value.isDisabled; }
const quotaDirty = computed(() => String(quota.value) !== String(u.value.dailyQuota));
const quotaOk = computed(() => Number.isInteger(Number(quota.value)) && Number(quota.value) >= 0 && Number(quota.value) <= 10000 && String(quota.value).trim() !== '');
const saveQuota = () => quotaOk.value && quotaDirty.value && update({ dailyQuota: Number(quota.value) });

// ---- Credits: the balance and the ledger, and a grant or correction ----
const credits = ref(null), grant = ref(false), amount = ref(100), note = ref(''), busy = ref(false), error = ref('');
async function loadCredits() { const id = u.value.id; try { const x = await api(`/api/users/${id}/credits`); if (id === u.value.id) credits.value = x; } catch { if (id === u.value.id) credits.value = { balance: u.value.credits, entries: [] }; } }
function openGrant() { amount.value = 100; note.value = ''; error.value = ''; grant.value = true; }
async function local(fn) { if (busy.value) return false; busy.value = true; error.value = ''; try { await fn(); return true; } catch (e) { error.value = e.message; return false; } finally { busy.value = false; } }
async function doGrant() { if (await local(() => api(`/api/users/${u.value.id}/credits`, 'POST', { amount: Number(amount.value), note: note.value.trim() || null }))) { grant.value = false; loadCredits(); emit('changed'); } }

// ---- Security: a new password, their tokens, and deleting the account ----
const password = ref(''), passwordDone = ref(false), passwordError = ref(''), tokens = ref(null), revoking = ref(null), deleting = ref(false);
async function loadTokens() { const id = u.value.id; try { const x = await api(`/api/users/${id}/tokens`); if (id === u.value.id) tokens.value = x; } catch { if (id === u.value.id) tokens.value = []; } }
async function resetPassword() {
 passwordDone.value = false; passwordError.value = '';
 try { busy.value = true; await api(`/api/users/${u.value.id}/password`, 'POST', { password: password.value }); password.value = ''; passwordDone.value = true; }
 catch (e) { passwordError.value = e.message; } finally { busy.value = false; }
}
async function revoke(k) { await action(async () => { await api(`/api/users/${u.value.id}/tokens/${k.id}`, 'DELETE'); revoking.value = null; await loadTokens(); }); }
async function doDelete() { if (await local(() => api(`/api/users/${u.value.id}`, 'DELETE'))) { deleting.value = false; emit('deleted'); } }

// ---- Loading by tab, and starting over when another person is opened ----
const loaders = { summary: loadUsage, credits: loadCredits, security: loadTokens };
function reset() { usage.value = null; credits.value = null; tokens.value = null; quota.value = u.value.dailyQuota; password.value = ''; passwordDone.value = false; passwordError.value = ''; revoking.value = null; }
// The month's figures feed the summary and the header's middle figure, so they are read whichever tab opens first.
function start() { reset(); loadUsage(); if (current.value !== 'summary') loaders[current.value]?.(); }
watch(() => u.value.id, () => { current.value = props.tab; start(); });
watch(() => u.value.dailyQuota, q => { quota.value = q; });
watch(current, tab => { if (tab !== 'summary') loaders[tab]?.(); });
const mini = computed(() => [
 { label: 'shToday', value: limited(u.value) ? tf('adOf', u.value.usedToday, u.value.dailyQuota) : n(u.value.usedToday) },
 { label: 'drMonth', value: usage.value ? n(usage.value.requests) : '…' },
 { label: 'shCredits', value: owner.value ? '—' : n(u.value.credits) },
]);
function onTabKey(e) {
 const i = TABS.findIndex(([k]) => k === current.value), step = (e.key === 'ArrowLeft') === (document.documentElement.dir === 'rtl') ? 1 : -1;
 if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
 e.preventDefault(); current.value = TABS[(i + step + TABS.length) % TABS.length][0];
 nextTick(() => tablist.value?.querySelector('[aria-selected=true]')?.focus());
}

// ---- The sheet itself: Escape closes it, Tab stays inside, and a dialog opened from it handles its own keys ----
function key(e) {
 if (document.querySelector('.dlg-backdrop')) return;
 if (e.key === 'Escape') { emit('close'); return; }
 if (e.key !== 'Tab' || !panel.value) return;
 const list = [...panel.value.querySelectorAll('button:not(:disabled),input:not(:disabled),textarea,a[href],select,[tabindex="0"]')].filter(x => x.offsetParent !== null);
 const first = list[0], last = list.at(-1);
 if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last?.focus(); }
 else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus(); }
}
onMounted(() => { start(); document.documentElement.style.overflow = 'hidden'; document.addEventListener('keydown', key); closer.value?.focus(); });
onBeforeUnmount(() => { document.documentElement.style.overflow = ''; document.removeEventListener('keydown', key); });
</script>

<template>
 <Teleport to="body">
  <div class="dim" @mousedown.self="emit('close')">
   <aside ref="panel" class="drawer" role="dialog" aria-modal="true" aria-labelledby="dr-name">
    <header class="head">
     <span class="av av-lg" aria-hidden="true">{{ initial(u.username) }}</span>
     <span class="who"><b id="dr-name"><bdi>{{ u.username }}</bdi></b><small>{{ t(owner ? 'shOwner' : 'shMember') }}{{ t('adSep') }}{{ tf('drSince', date(u.createdAt)) }}<template v-if="u.isDisabled">{{ t('adSep') }}{{ t('usageDisabled') }}</template></small></span>
     <button ref="closer" class="btn btn-ghost btn-icon btn-sm" :aria-label="t('close')" @click="emit('close')"><X/></button>
    </header>
    <div class="mini"><div v-for="m in mini" :key="m.label"><small>{{ t(m.label) }}</small><b class="num">{{ m.value }}</b></div></div>
    <div ref="tablist" class="tabs" role="tablist" :aria-label="u.username" @keydown="onTabKey">
     <button v-for="[k, label] in TABS" :id="`dr-tab-${k}`" :key="k" role="tab" :aria-selected="current === k" :aria-controls="`dr-panel-${k}`" :tabindex="current === k ? 0 : -1" @click="current = k">{{ t(label) }}</button>
    </div>

    <div :id="`dr-panel-${current}`" class="body" role="tabpanel" :aria-labelledby="`dr-tab-${current}`">
     <!-- Summary -->
     <div v-if="current === 'summary'" class="stack gap">
      <div v-if="usage === null" class="skel" style="height:90px"></div>
      <p v-else-if="usage === false" class="faint-text">{{ t('adCouldNotLoad') }}</p>
      <template v-else>
       <p class="faint-text period">{{ t('drLast30') }}</p>
       <div class="figs"><div v-for="f in figures" :key="f.label" class="tile"><small>{{ t(f.label) }}</small><strong>{{ f.value }}</strong><em>{{ f.note }}</em></div></div>
       <div class="groups">
        <div v-for="g in groups" :key="g.title" class="grp"><span class="gt">{{ t(g.title) }}</span><dl><div v-for="[label, v] in g.rows" :key="label" class="dr"><dt>{{ t(label) }}</dt><dd class="num">{{ n(v) }}</dd></div></dl></div>
        <div class="grp"><span class="gt">{{ t('usageTopTemplates') }}</span><dl v-if="usage.templates?.length"><div v-for="tp in usage.templates" :key="tp.title" class="dr"><dt><bdi>{{ tp.title }}</bdi></dt><dd class="num">{{ tf('drTimes', tp.count) }}</dd></div></dl><p v-else class="faint-text none">{{ t('usageNoTemplates') }}</p></div>
       </div>
      </template>

      <section class="ctl" aria-labelledby="dr-settings">
       <h3 id="dr-settings">{{ t('drAccount') }}</h3>
       <p v-if="you" class="notice"><ShieldCheck/><span>{{ t('drYourself') }}</span></p>
       <div class="line"><span>{{ t('drRole') }}</span><span class="seg" role="group" :aria-label="t('drRole')"><button :aria-pressed="!owner" :disabled="you" @click="owner && update({ role: 'Member' })">{{ t('shMember') }}</button><button :aria-pressed="owner" :disabled="you" @click="!owner && update({ role: 'Owner' })">{{ t('shOwner') }}</button></span></div>
       <label class="line switch"><span class="grow">{{ t('drEnabled') }}<small>{{ t(u.isDisabled ? 'drEnabledOff' : 'drEnabledOn') }}</small></span><input type="checkbox" role="switch" :checked="!u.isDisabled" :disabled="you" @change="setEnabled"></label>
       <form class="line quota" @submit.prevent="saveQuota">
        <label for="dr-quota" class="grow">{{ t('dailyQuota') }}<small>{{ t('adQuotaHelp') }}</small></label>
        <input id="dr-quota" v-model="quota" class="input num" type="number" min="0" max="10000" :aria-invalid="!quotaOk">
        <button class="btn btn-sm" :class="{ 'btn-primary': quotaDirty }" :disabled="!quotaDirty || !quotaOk"><Gauge/>{{ t('save') }}</button>
       </form>
      </section>
     </div>

     <!-- Packages -->
     <DrawerPackages v-else-if="current === 'packages'" :user="u" @changed="emit('changed')" @credits="current = 'credits'; openGrant()"/>

     <!-- Credits -->
     <div v-else-if="current === 'credits'" class="stack gap">
      <div class="bal"><div><small>{{ t('drBalance') }}</small><strong class="num">{{ credits ? n(credits.balance) : '…' }}</strong></div><button class="btn btn-primary" @click="openGrant"><Coins/>{{ t('addCredits') }}</button></div>
      <p v-if="owner" class="notice"><ShieldCheck/><span>{{ t('usageNotCharged') }}</span></p>
      <div v-if="credits === null" class="skel" style="height:100px"></div>
      <p v-else-if="!credits.entries.length" class="faint-text">{{ t('acNoHistory') }}</p>
      <ol v-else class="ledger">
       <li v-for="e in credits.entries" :key="e.id"><time class="faint-text" :datetime="e.createdAt">{{ ago(e.createdAt) }}</time><span>{{ entryText(e) }}</span><b class="num" :class="{ plus: e.amount > 0, pkg: e.packageName }">{{ signed(e.amount) }}</b></li>
      </ol>
     </div>

     <!-- Usage -->
     <RequestList v-else-if="current === 'usage'" :user-id="u.id"/>

     <!-- Security -->
     <div v-else class="stack gap">
      <form class="sec" @submit.prevent="resetPassword">
       <h3><LockKeyhole/>{{ t('resetPassword') }}</h3>
       <p class="muted-text">{{ t('resetPasswordHelp') }}</p>
       <div class="field"><label for="dr-pass">{{ t('newPassword') }}</label><input id="dr-pass" v-model="password" class="input" dir="ltr" type="password" autocomplete="new-password" minlength="8" maxlength="512"><span class="field-help">{{ t('acPasswordRule') }}</span></div>
       <p v-if="passwordDone" class="notice notice-ok" role="status"><CircleCheck/><span>{{ t('drPasswordSet') }}</span></p>
       <p v-if="passwordError" role="alert" class="field-error">{{ t(passwordError) }}</p>
       <div class="dlg-actions"><button class="btn btn-primary" :disabled="busy || password.length < 8">{{ t('drSetPassword') }}</button></div>
      </form>

      <section class="sec">
       <h3><KeyRound/>{{ t('userTokens') }}</h3>
       <p class="muted-text">{{ t('userTokensHelp') }}</p>
       <div v-if="tokens === null" class="skel" style="height:48px"></div>
       <p v-else-if="!tokens.length" class="faint-text">{{ t('noTokens') }}</p>
       <div v-else class="list">
        <div v-for="k in tokens" :key="k.id" class="row">
         <span class="grow tk"><strong><bdi>{{ k.name }}</bdi></strong><small><code dir="ltr">lm_{{ k.prefix }}_…</code>{{ t('adSep') }}{{ t('lastUsed') }}: {{ k.lastUsedAt ? ago(k.lastUsedAt) : t('never') }}</small></span>
         <template v-if="revoking === k.id"><button class="btn btn-sm" @click="revoking = null">{{ t('cancel') }}</button><button class="btn btn-sm btn-danger btn-primary" @click="revoke(k)">{{ t('revoke') }}</button></template>
         <button v-else class="btn btn-sm btn-danger" @click="revoking = k.id">{{ t('revoke') }}</button>
        </div>
       </div>
      </section>

      <section class="sec danger">
       <h3><Trash2/>{{ t('userDelete') }}</h3>
       <p class="muted-text">{{ t(you ? 'CannotDeleteSelf' : 'drDeleteHelp') }}</p>
       <div class="dlg-actions"><button class="btn btn-danger" :disabled="you" @click="error = ''; deleting = true"><Trash2/>{{ t('userDelete') }}</button></div>
      </section>
     </div>
    </div>
   </aside>
  </div>

  <Modal v-if="grant" :title="tf('drGrantTitle', bidi(u.username))" :close-label="t('close')" @close="grant = false">
   <form class="stack" @submit.prevent="doGrant">
    <p class="muted-text">{{ t('creditsHelp') }}</p>
    <div class="field"><label for="gr-amount">{{ t('amount') }}</label><input id="gr-amount" v-model="amount" class="input num" type="number" min="-1000000" max="1000000" required></div>
    <div class="cluster"><button v-for="v in [100, 500, 1000]" :key="v" type="button" class="chip" :class="{ 'chip-on': Number(amount) === v }" @click="amount = v">+{{ n(v) }}</button></div>
    <div class="field"><label for="gr-note">{{ t('note') }}</label><input id="gr-note" v-model="note" class="input" maxlength="400"></div>
    <p v-if="Number(amount) < 0" class="notice notice-warn"><AlertTriangle/><span>{{ tf('drCorrection', -Number(amount)) }}</span></p>
    <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
    <div class="dlg-actions"><button type="button" class="btn" @click="grant = false">{{ t('cancel') }}</button><button class="btn btn-primary" :disabled="busy || !Number(amount)">{{ busy ? t('processing') : t(Number(amount) < 0 ? 'drCorrect' : 'addCredits') }}</button></div>
   </form>
  </Modal>

  <Modal v-if="deleting" :title="t('userDelete')" :close-label="t('close')" @close="deleting = false">
   <p><strong><bdi>{{ u.username }}</bdi></strong></p>
   <p>{{ t('deleteUserConfirm') }}</p>
   <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
   <div class="dlg-actions"><button class="btn" @click="deleting = false">{{ t('cancel') }}</button><button class="btn btn-danger btn-primary" :disabled="busy" @click="doDelete">{{ busy ? t('processing') : t('drDeleteYes') }}</button></div>
  </Modal>
 </Teleport>
</template>

<style scoped>
.dim{position:fixed;inset:0;z-index:40;display:flex;justify-content:flex-end;background:var(--dim)}
.drawer{--from:24px;width:min(480px,100%);height:100%;display:flex;flex-direction:column;background:var(--s1);border-inline-start:1px solid var(--line);box-shadow:var(--shadow);min-height:0}
:root[dir=rtl] .drawer{--from:-24px}
@media (prefers-reduced-motion:no-preference){.drawer{animation:slide .2s ease-out}}
@keyframes slide{from{opacity:0;translate:var(--from) 0}to{opacity:1;translate:0 0}}
.head{display:flex;align-items:center;gap:12px;padding:20px 20px 0}
.who{flex:1;display:flex;flex-direction:column;min-width:0;line-height:1.4}
.who b{font-size:18px;overflow-wrap:anywhere}
.who small{color:var(--muted);font-size:12.5px}
.mini{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px;padding:16px 20px 0}
.mini>div{display:flex;flex-direction:column;padding:10px 12px;border-radius:var(--r2);background:var(--s2);line-height:1.35;min-width:0}
.mini small{color:var(--muted);font-size:12px}
.mini b{font-size:17px}
.tabs{display:flex;gap:2px;margin:14px 20px 0;border-bottom:1px solid var(--line);overflow-x:auto;scrollbar-width:none}
.tabs button{flex:none;padding:9px 10px;border:0;border-bottom:2px solid transparent;margin-bottom:-1px;background:none;color:var(--muted);font-size:13.5px}
.tabs button:hover{color:var(--text)}
.tabs button[aria-selected=true]{color:var(--text);border-bottom-color:var(--accent);font-weight:600}
.body{flex:1;min-height:0;overflow-y:auto;padding:18px 20px 20px;display:flex;flex-direction:column}
.gap{gap:16px}
.period{margin-bottom:-6px}
.figs{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px}
.figs .tile{padding:10px 12px}
.figs .tile strong{font-size:22px}
.groups{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:14px}
.grp{display:flex;flex-direction:column;gap:3px;min-width:0}
.grp dl{margin:0;display:flex;flex-direction:column;gap:3px}
.gt{font-size:12px;color:var(--faint);margin-bottom:2px}
.dr{display:flex;justify-content:space-between;gap:8px;font-size:12.5px}
.dr dt{color:var(--muted);min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.dr dd{margin:0}
.none{margin:0;font-size:12.5px}
.ctl{display:flex;flex-direction:column;gap:2px;padding-top:14px;border-top:1px solid var(--line)}
.ctl h3,.sec h3{font-size:14.5px;display:flex;align-items:center;gap:8px;margin-bottom:6px}
.sec h3 svg{color:var(--muted)}
.ctl .notice{margin-bottom:6px}
.line{display:flex;align-items:center;gap:12px;padding:10px 0;border-top:1px solid var(--line);font-size:14px}
.ctl h3+.line,.ctl .notice+.line{border-top:0}
.line small{display:block;color:var(--faint);font-size:12px}
.line>span:first-child{flex:1}
.switch{cursor:pointer}
.quota .input{width:96px}
.bal{display:flex;align-items:flex-end;justify-content:space-between;gap:12px}
.bal>div{display:flex;flex-direction:column}
.bal small{color:var(--muted);font-size:12.5px}
.bal strong{font-size:30px;font-weight:700;line-height:1.25}
.ledger{list-style:none;margin:0;padding:0;display:flex;flex-direction:column}
.ledger li{display:grid;grid-template-columns:84px minmax(0,1fr) auto;gap:10px;align-items:baseline;padding:9px 0;border-top:1px solid var(--line);font-size:13px}
.ledger li:first-child{border-top:0}
.ledger time{font-size:12px}
.ledger b{font-weight:600;white-space:nowrap}
.ledger .plus{color:var(--ok)}
.ledger .pkg{color:var(--muted)}
.sec{display:flex;flex-direction:column;gap:10px;padding-top:16px;border-top:1px solid var(--line)}
.sec:first-child{border-top:0;padding-top:0}
.danger h3 svg{color:var(--bad)}
.tk{display:flex;flex-direction:column;min-width:0}
.tk small{color:var(--muted);font-size:12.5px;overflow-wrap:anywhere}
.list>.row{flex-wrap:wrap}
@media (max-width:640px){
 .drawer{width:100%;border:0}
 .head{padding:max(16px,env(safe-area-inset-top,0px)) 16px 0}
 .mini,.body{padding-inline:16px}
 .tabs{margin-inline:16px}
 .body{padding-bottom:calc(20px + env(safe-area-inset-bottom,0px))}
 .figs{grid-template-columns:1fr 1fr}
 .groups{grid-template-columns:1fr 1fr}
}
</style>
