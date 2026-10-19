<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import { Plus, RefreshCw, ExternalLink, Power, RotateCcw, Trash2, KeyRound, ShieldCheck, AlertTriangle, Monitor, Activity } from 'lucide-vue-next';
import Modal from '../../components/Modal.vue';
import { t, tf, api, action, busy, connected, refresh, ago, PROVIDERS, onTick } from '../../core';
// The ChatGPT and Gemini accounts that do the work: how each stands, its daily cap, and everything the old accounts panel and connection dialog did.
const accounts = ref(null), dialog = ref(null), target = ref(null), form = ref({}), saving = ref(false), error = ref('');
async function load() { try { accounts.value = await api('/api/auth/accounts'); } catch { accounts.value ??= []; } }
onMounted(load);
const stop = onTick(load);
onBeforeUnmount(stop);

// The desktop only ever shows an account's own browser window: offered with none started, it would open onto an empty screen.
const browserAccounts = computed(() => (connected.value.providers || []).some(p => p.provider === 'ChatGPT' && p.accounts > 0));
const browserOpen = computed(() => (connected.value.providers || []).some(p => p.provider === 'ChatGPT' && p.open > 0));
const tone = a => !a.isEnabled ? 'off' : a.busy ? 'busy' : a.state === 'Connected' && !a.spentToday ? 'ok' : a.state === 'Connected' ? 'warn' : 'bad';
const stateText = a => !a.isEnabled ? t('usageDisabled') : a.busy ? t('busy') : t(a.state);
const statePill = a => ({ ok: 'pill-ok', warn: 'pill-warn', bad: 'pill-bad', busy: '', off: 'pill-muted' })[tone(a)];
// Built as one string: separators between optional parts are fragile in a template, where trailing whitespace is condensed away.
const line = a => [a.keyHint && `${t('keyLabel')}: ${a.keyHint}`, `${t('lastUsed')}: ${a.lastUsedAt ? ago(a.lastUsedAt) : t('never')}`].filter(Boolean).join(t('adSep'));
const replace = updated => { const i = accounts.value.findIndex(x => x.id === updated.id); if (i >= 0) accounts.value[i] = { ...accounts.value[i], ...updated }; };

const connect = a => action(async () => { await api(`/api/auth/accounts/${a.id}/connect`, 'POST'); await load(); refresh().catch(() => {}); });
const check = a => action(async () => replace({ id: a.id, ...await api(`/api/auth/accounts/${a.id}/status`) }));
const update = (a, changes) => action(async () => { replace(await api(`/api/auth/accounts/${a.id}`, 'PATCH', changes)); refresh().catch(() => {}); });
function setCap(a, e) { const v = Number(e.target.value); if (!Number.isInteger(v) || v < 0 || v > 500) { e.target.value = a.dailyCap; return; } if (v !== a.dailyCap) update(a, { dailyCap: v }); }
function ask(kind, a) { target.value = a; dialog.value = kind; }
const confirmReset = () => action(async () => { await api(`/api/auth/accounts/${target.value.id}/reset`, 'POST'); dialog.value = null; await load(); });
const confirmDelete = () => action(async () => { await api(`/api/auth/accounts/${target.value.id}`, 'DELETE'); dialog.value = null; await load(); refresh().catch(() => {}); });

function add() { form.value = { label: '', provider: 'ChatGPT', key: '' }; error.value = ''; dialog.value = 'add'; }
// The key only travels one way: the box is emptied once it is accepted, and the server hands back no more than a hint of it.
async function create() {
 if (saving.value) return; saving.value = true; error.value = '';
 try {
  const f = form.value;
  await api('/api/auth/accounts', 'POST', { label: f.label.trim(), provider: f.provider, apiKey: f.provider === 'Gemini' ? f.key.trim() : null });
  form.value.key = ''; dialog.value = null; await load(); refresh().catch(() => {});
 } catch (e) { error.value = e.message; } finally { saving.value = false; }
}
</script>

<template>
 <div class="view accounts">
  <header class="view-head">
   <div><h1>{{ t('shAccounts') }}</h1><p>{{ t('accountsHelp') }}</p></div>
   <button class="btn btn-primary" @click="add"><Plus/>{{ t('addAccount') }}</button>
  </header>

  <section class="card status" aria-labelledby="acc-status">
   <div class="card-head"><h2 id="acc-status"><Activity/>{{ t('adPool') }}</h2><span class="grow"/><button class="btn btn-sm btn-ghost" :disabled="busy" @click="action(refresh)"><RefreshCw/>{{ t('usageRefresh') }}</button></div>
   <div class="providers">
    <div v-for="p in connected.providers" :key="p.provider" class="prov"><span class="dot" :class="p.connected ? 'ok' : 'bad'"></span><b>{{ p.provider }}</b><span class="num muted-text">{{ tf('adConnectedOf', p.connected, p.accounts) }}</span></div>
    <p v-if="!(connected.providers || []).length" class="faint-text">{{ t('noAccounts') }}</p>
   </div>
   <p v-if="connected.stalled" class="notice notice-warn"><AlertTriangle/><span>{{ t(connected.stalled) }}</span></p>
   <div v-if="connected.desktopUrl && browserOpen" class="cluster"><a class="btn" :href="connected.desktopUrl" target="_blank" rel="noopener noreferrer"><ExternalLink/>{{ t('openBrowser') }}</a><small class="faint-text">{{ t('connectionHelp') }}</small></div>
   <p v-else-if="connected.desktopUrl && !browserAccounts" class="notice"><Monitor/><span>{{ t('adDesktopNoAccount') }}</span></p>
   <p v-else-if="connected.desktopUrl" class="notice"><Monitor/><span>{{ t('adDesktopNotStarted') }}</span></p>
   <p v-else-if="(connected.providers || []).length" class="notice"><Monitor/><span>{{ t('desktopWindow') }}</span></p>
   <p class="field-help privacy"><ShieldCheck/>{{ t('sessionPrivacy') }}</p>
  </section>

  <div v-if="accounts === null" class="stack"><div v-for="i in 2" :key="i" class="skel" style="height:120px"></div></div>
  <div v-else-if="!accounts.length" class="blank"><Monitor/><h2>{{ t('noAccounts') }}</h2><p>{{ t('accountHelp') }}</p><div class="cluster"><button class="btn btn-primary" @click="add"><Plus/>{{ t('addAccount') }}</button></div></div>
  <div v-else class="stack">
   <article v-for="a in accounts" :key="a.id" class="card acc" :class="{ off: !a.isEnabled }">
    <div class="top">
     <i class="dot" :class="tone(a)" aria-hidden="true"></i>
     <div class="name"><b><bdi>{{ a.label }}</bdi></b><small>{{ line(a) }}</small></div>
     <span class="tag">{{ a.provider }}</span>
     <span class="pill" :class="statePill(a)">{{ stateText(a) }}</span>
    </div>
    <div class="ctrl">
     <label class="cap"><span>{{ t('dailyCap') }}</span><input class="input num" type="number" min="0" max="500" :value="a.dailyCap" :aria-label="t('dailyCap')" :disabled="busy" @change="setCap(a, $event)"><small v-if="a.usedToday != null" class="faint-text">{{ tf('ovAccUse', a.usedToday, a.dailyCap) }}</small></label>
     <span class="grow"/>
     <div class="cluster acts">
      <button class="btn btn-sm" :class="{ 'btn-primary': a.state !== 'Connected' && a.isEnabled }" :disabled="busy || a.busy" @click="connect(a)"><RefreshCw/>{{ a.kind === 'ApiKey' ? t('checkKey') : a.state === 'Connected' ? t('reconnect') : t('adConnect') }}</button>
      <button class="btn btn-sm" :disabled="busy" @click="check(a)">{{ t('refresh') }}</button>
      <a v-if="a.kind !== 'ApiKey' && a.desktopUrl && a.state !== 'Disconnected'" class="btn btn-sm" :href="a.desktopUrl" target="_blank" rel="noopener noreferrer"><ExternalLink/>{{ t('openBrowser') }}</a>
      <button class="btn btn-sm" :disabled="busy" @click="update(a, { isEnabled: !a.isEnabled })"><Power/>{{ a.isEnabled ? t('disable') : t('enable') }}</button>
      <button v-if="a.kind !== 'ApiKey'" class="btn btn-sm btn-ghost" :disabled="busy || a.busy" @click="ask('reset', a)"><RotateCcw/>{{ t('adReset') }}</button>
      <button class="btn btn-sm btn-ghost btn-icon del" :aria-label="t('accountDelete')" :title="t('accountDelete')" :disabled="busy || a.busy" @click="ask('delete', a)"><Trash2/></button>
     </div>
    </div>
    <p v-if="a.kind === 'ApiKey' && a.state === 'Disconnected'" class="notice"><KeyRound/><span>{{ t('apiAccountHelp') }}</span></p>
    <p v-else-if="a.state === 'VerificationRequired'" class="notice notice-warn"><ShieldCheck/><span>{{ t('verifyHelp') }}</span></p>
    <p v-else-if="a.state === 'LoginRequired' && a.kind !== 'ApiKey'" class="notice notice-warn"><AlertTriangle/><span>{{ t('adLoginHelp') }}</span></p>
    <p v-else-if="a.state === 'LoginRequired'" class="notice notice-warn"><KeyRound/><span>{{ t('adKeyRefused') }}</span></p>
    <p v-else-if="a.spentToday" class="notice notice-warn"><AlertTriangle/><span>{{ t('adSpentHelp') }}</span></p>
   </article>
  </div>

  <Modal v-if="dialog === 'add'" :title="t('addAccount')" :close-label="t('close')" @close="dialog = null">
   <form class="stack" @submit.prevent="create">
    <p class="muted-text">{{ t('accountHelp') }}</p>
    <div class="field"><label for="acc-label">{{ t('accountLabel') }}</label><input id="acc-label" v-model="form.label" class="input" required maxlength="64"></div>
    <div class="field"><span id="acc-prov" class="field-label">{{ t('accountProvider') }}</span><div class="seg" role="group" aria-labelledby="acc-prov"><button v-for="p in PROVIDERS" :key="p" type="button" :aria-pressed="form.provider === p" @click="form.provider = p">{{ p }}</button></div></div>
    <template v-if="form.provider === 'Gemini'">
     <div class="field"><label for="acc-key">{{ t('apiKey') }}</label><input id="acc-key" v-model="form.key" class="input" dir="ltr" type="password" autocomplete="off" spellcheck="false" required maxlength="400"></div>
     <p class="notice"><ShieldCheck/><span>{{ t('apiKeyHelp') }}</span></p>
    </template>
    <p v-else class="notice"><Monitor/><span>{{ t('browserAccountHelp') }}</span></p>
    <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
    <div class="dlg-actions"><button type="button" class="btn" @click="dialog = null">{{ t('cancel') }}</button><button class="btn btn-primary" :disabled="saving || !form.label.trim() || (form.provider === 'Gemini' && !form.key.trim())">{{ saving ? t('processing') : t('addAccount') }}</button></div>
   </form>
  </Modal>

  <Modal v-else-if="dialog" :title="t(dialog === 'reset' ? 'accountReset' : 'accountDelete')" :close-label="t('close')" @close="dialog = null">
   <p><strong><bdi>{{ target?.label }}</bdi></strong></p>
   <p>{{ t(dialog === 'reset' ? 'resetAccountConfirm' : 'deleteAccountConfirm') }}</p>
   <div class="dlg-actions"><button class="btn" @click="dialog = null">{{ t('cancel') }}</button><button class="btn btn-danger btn-primary" :disabled="busy" @click="dialog === 'reset' ? confirmReset() : confirmDelete()">{{ t(dialog === 'reset' ? 'adResetYes' : 'adDeleteYes') }}</button></div>
  </Modal>
 </div>
</template>

<style scoped>
.card h2{font-size:15.5px}
.card-head h2 svg{color:var(--accent)}
.providers{display:flex;flex-wrap:wrap;gap:10px}
.prov{display:flex;align-items:center;gap:8px;padding:8px 12px;border-radius:var(--r2);background:var(--s2);font-size:13.5px}
.privacy{display:flex;gap:8px;align-items:flex-start}
.privacy svg{width:15px;height:15px;margin-top:2px;color:var(--faint)}
.dot{width:9px;height:9px;border-radius:50%;background:var(--faint);flex:none}
.dot.ok{background:var(--ok)}
.dot.warn{background:var(--warn)}
.dot.bad{background:var(--bad)}
.dot.busy{background:var(--accent);box-shadow:0 0 0 4px var(--accent-soft)}
.acc{gap:12px}
.acc.off .top,.acc.off .cap{opacity:.6}
.top{display:flex;align-items:center;gap:12px;flex-wrap:wrap}
.name{flex:1;display:flex;flex-direction:column;min-width:160px}
.name b{font-size:15px;overflow-wrap:anywhere}
.name small{color:var(--muted);font-size:12.5px}
.ctrl{display:flex;align-items:center;gap:12px;flex-wrap:wrap;padding-top:12px;border-top:1px solid var(--line)}
.cap{display:flex;align-items:center;gap:8px;font-size:13px;color:var(--muted)}
.cap .input{width:84px;min-height:32px;padding-block:4px}
.acts{justify-content:flex-end}
.del:hover:not(:disabled){color:var(--bad)}
.notice{margin:0}
@media (max-width:860px){
 .acts{justify-content:flex-start}
 .ctrl>.grow{display:none}
}
</style>
