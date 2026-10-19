<script setup>
import { ref, nextTick } from 'vue';
import { Plus, KeyRound, Copy, Check, ShieldCheck, ExternalLink, Trash2 } from 'lucide-vue-next';
import ApiGuide from '../../components/ApiGuide.vue';
import { t, isOwner, api, ago, date } from '../../core';
// Personal API tokens and the guide to using them. Everything happens in this one sheet: naming a token, the one look at its secret, and revoking.
const props = defineProps({ tokens: { type: Array, default: null } });
const emit = defineEmits(['reload']);
const naming = ref(false), name = ref(''), minted = ref(''), copied = ref(false), revoking = ref(null), busy = ref(false), error = ref(''), nameInput = ref(null), secret = ref(null);
async function run(fn) { if (busy.value) return; busy.value = true; error.value = ''; try { await fn(); } catch (e) { error.value = e.message; } finally { busy.value = false; } }
async function startNaming() { naming.value = true; name.value = ''; minted.value = ''; await nextTick(); nameInput.value?.focus(); }
// The secret lives only in this one response; once it is put away there is nothing left anywhere to copy.
async function create() { await run(async () => { const made = await api('/api/tokens', 'POST', { name: name.value.trim() }); minted.value = made.token; copied.value = false; naming.value = false; emit('reload'); }); }
async function copy() {
 try { await navigator.clipboard.writeText(minted.value); copied.value = true; }
 catch { if (secret.value) window.getSelection()?.selectAllChildren(secret.value); }
}
async function revoke(k) { await run(async () => { await api(`/api/tokens/${k.id}`, 'DELETE'); revoking.value = null; emit('reload'); }); }
</script>

<template>
 <div class="dev">
  <section class="stack" aria-labelledby="dev-tokens">
   <div class="card-head"><h3 id="dev-tokens"><KeyRound/>{{ t('tokens') }}</h3><span class="grow"/><button v-if="!naming && !minted" class="btn btn-sm btn-primary" :disabled="busy || (tokens?.length ?? 0) >= 20" @click="startNaming"><Plus/>{{ t('newToken') }}</button></div>
   <p class="muted-text">{{ t('tokensHelp') }}</p>

   <form v-if="naming" class="make" @submit.prevent="create">
    <div class="field grow"><label for="token-name">{{ t('tokenName') }}</label><input id="token-name" ref="nameInput" v-model="name" class="input" required maxlength="64"><span class="field-help">{{ t('tokenHelp') }}</span></div>
    <div class="dlg-actions"><button type="button" class="btn" @click="naming=false">{{ t('cancel') }}</button><button class="btn btn-primary" :disabled="busy || !name.trim()">{{ busy ? t('processing') : t('newToken') }}</button></div>
   </form>

   <div v-if="minted" class="minted" role="status">
    <p class="notice notice-ok"><ShieldCheck/><span>{{ t('tokenOnce') }}</span></p>
    <code ref="secret" class="secret" dir="ltr">{{ minted }}</code>
    <p class="field-help">{{ t('apiUsage') }} <code dir="ltr">Authorization: Bearer lm_…</code></p>
    <div class="dlg-actions"><button class="btn" @click="copy"><Check v-if="copied"/><Copy v-else/>{{ t(copied ? 'copied' : 'copy') }}</button><button class="btn btn-primary" @click="minted=''">{{ t('acTokenSaved') }}</button></div>
   </div>

   <div v-if="tokens === null" class="skel" style="height:56px"></div>
   <p v-else-if="!tokens.length" class="faint-text">{{ t('noTokens') }}</p>
   <div v-else class="list">
    <div v-for="k in tokens" :key="k.id" class="row">
     <span class="grow tk">
      <strong><bdi>{{ k.name }}</bdi></strong>
      <small><code dir="ltr">lm_{{ k.prefix }}_…</code>{{ t('adSep') }}{{ t('created') }} {{ date(k.createdAt) }}{{ t('adSep') }}{{ t('lastUsed') }}: {{ k.lastUsedAt ? ago(k.lastUsedAt) : t('never') }}</small>
     </span>
     <template v-if="revoking === k.id">
      <span class="cluster confirm"><span class="field-help">{{ t('revokeTokenConfirm') }}</span><button class="btn btn-sm" :disabled="busy" @click="revoking=null">{{ t('cancel') }}</button><button class="btn btn-sm btn-danger btn-primary" :disabled="busy" @click="revoke(k)">{{ t('revoke') }}</button></span>
     </template>
     <button v-else class="btn btn-sm btn-danger" :disabled="busy" @click="revoking=k.id"><Trash2/>{{ t('revoke') }}</button>
    </div>
   </div>
   <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
   <a v-if="isOwner" class="btn btn-sm openapi" href="/openapi/v1.json" target="_blank" rel="noopener noreferrer"><ExternalLink/>{{ t('apiDocs') }}</a>
  </section>

  <hr>
  <ApiGuide/>
 </div>
</template>

<style scoped>
.dev{display:flex;flex-direction:column;gap:18px;min-width:0}
.dev h3{font-size:16px}
.dev h3 svg{color:var(--accent)}
.make{display:flex;flex-direction:column;gap:10px;padding:14px;border-radius:var(--r);background:var(--s2)}
.minted{display:flex;flex-direction:column;gap:10px;padding:14px;border-radius:var(--r);border:1px solid var(--accent-line);background:var(--s2)}
.secret{display:block;padding:10px 12px;border-radius:var(--r2);background:var(--bg);border:1px solid var(--line);font-size:13px;overflow-wrap:anywhere;user-select:all;text-align:left}
.tk{display:flex;flex-direction:column;min-width:0}
.tk small{color:var(--muted);font-size:12.5px;overflow-wrap:anywhere}
.confirm{flex-basis:100%;justify-content:flex-end}
.list>.row{flex-wrap:wrap}
.openapi{align-self:flex-start}
hr{border:0;border-top:1px solid var(--line);margin:0;width:100%}
</style>
