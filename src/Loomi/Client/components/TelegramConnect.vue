<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import { Send, ExternalLink, Copy, Check, Bot, Trash2, ShieldCheck, LoaderCircle, AlertTriangle, CircleCheck, KeyRound } from 'lucide-vue-next';
// One place for everything Telegram: the person's own link, and for the owner the bot behind it. Linking is a single press: the code rides in a
// t.me deep link, so Telegram opens on the bot with the code already filled in, the person presses Start, and this page notices by itself.
const props = defineProps({ t:Function, locale:String, isOwner:Boolean, api:Function });
const tg = ref({ linked:false, configured:false }), token = ref(''), busy = ref(false), error = ref(''), copied = ref(false), confirming = ref(null);
let timer = null;
const waiting = computed(() => !tg.value.linked && !!tg.value.code);
const deepLink = computed(() => tg.value.botUsername && tg.value.code ? `https://t.me/${tg.value.botUsername}?start=${tg.value.code}` : null);
const botName = computed(() => tg.value.botUsername ? '@' + tg.value.botUsername : props.t('telegramBot'));
const since = computed(() => tg.value.linkedAt ? new Date(tg.value.linkedAt).toLocaleDateString(props.locale) : '');
const until = computed(() => tg.value.expiresAt ? new Date(tg.value.expiresAt).toLocaleTimeString(props.locale, { hour:'2-digit', minute:'2-digit' }) : '');
async function run(fn) { if (busy.value) return; busy.value = true; error.value = ''; try { await fn(); } catch (e) { error.value = e.message; } finally { busy.value = false; } }
// While a code is out, look every few seconds: the link happens in Telegram, and nothing else would tell this page.
async function load() { tg.value = await props.api('/api/telegram/link'); clearTimeout(timer); if (waiting.value) timer = setTimeout(() => load().catch(() => {}), 3000); }
async function connect() {
 // Opened inside the click itself, so no popup blocker stands in the way; pointed at the bot once the code exists, closed again if it never does.
 const tab = window.open('', '_blank');
 let url = null;
 await run(async () => { const issued = await props.api('/api/telegram/link', 'POST'); if (issued.botUsername) url = `https://t.me/${issued.botUsername}?start=${issued.code}`; await load(); });
 if (tab) { if (url) tab.location.href = url; else tab.close(); }
}
async function copyCode() { await navigator.clipboard.writeText(`/start ${tg.value.code}`); copied.value = true; setTimeout(() => copied.value = false, 2000); }
async function unlink() { await run(async () => { await props.api('/api/telegram/link', 'DELETE'); confirming.value = null; await load(); }); }
async function saveBot() { await run(async () => { await props.api('/api/telegram/bot', 'PUT', { token: token.value.trim() }); token.value = ''; await load(); }); }
async function removeBot() { await run(async () => { await props.api('/api/telegram/bot', 'DELETE'); confirming.value = null; await load(); }); }
onMounted(() => load().catch(e => error.value = e.message));
onBeforeUnmount(() => clearTimeout(timer));
</script>
<template>
 <section class="settings-panel telegram-panel">
  <header class="tg-head"><span class="tg-mark"><Send/></span><span><h2>{{ t('telegram') }}</h2><p>{{ t('telegramHelp') }}</p></span></header>

  <div v-if="tg.linked" class="tg-state tg-ok">
   <CircleCheck/><span><strong>{{ t('tgConnected') }}</strong><small>{{ t('tgConnectedSince').replace('{0}', since) }} · <bdi dir="ltr">{{ botName }}</bdi></small></span>
   <span class="grow"/>
   <a v-if="tg.botUsername" class="button" :href="`https://t.me/${tg.botUsername}`" target="_blank" rel="noopener noreferrer"><ExternalLink/>{{ t('openBot') }}</a>
   <template v-if="confirming==='unlink'"><button :disabled="busy" @click="confirming=null">{{ t('cancel') }}</button><button class="danger" :disabled="busy" @click="unlink">{{ t('tgUnlinkYes') }}</button></template>
   <button v-else class="danger" :disabled="busy" @click="confirming='unlink'">{{ t('unlink') }}</button>
  </div>

  <div v-else-if="waiting" class="tg-state tg-wait" role="status">
   <LoaderCircle class="spin"/>
   <span><strong>{{ t('tgWaiting') }}</strong><small>{{ t('tgWaitingHelp') }}</small></span>
   <span class="grow"/>
   <a v-if="deepLink" class="button primary" :href="deepLink" target="_blank" rel="noopener noreferrer"><Send/>{{ t('tgOpenAgain') }}</a>
  </div>
  <div v-if="waiting" class="tg-manual">
   <span>{{ t('tgManual').replace('{0}', botName) }}</span>
   <code dir="ltr">/start {{ tg.code }}</code>
   <button class="icon-button" :aria-label="t('tgCopy')" @click="copyCode"><Check v-if="copied"/><Copy v-else/></button>
   <small>{{ t('tgValidUntil').replace('{0}', until) }}</small>
  </div>

  <div v-else-if="!tg.linked && tg.configured" class="tg-connect">
   <button class="primary tg-connect-button" :disabled="busy" @click="connect"><Send/>{{ t('tgConnect') }}</button>
   <p>{{ t('tgConnectHelp') }}</p>
  </div>

  <p v-else-if="!tg.linked && !tg.configured && !isOwner" class="dev-note account-note"><AlertTriangle/><span>{{ t('telegramOff') }}</span></p>

  <div v-if="isOwner" class="tg-setup">
   <h3><Bot/>{{ t('tgSetupTitle') }}</h3>
   <div v-if="tg.configured" class="tg-state">
    <span class="status-dot on"></span>
    <span><strong><bdi dir="ltr">{{ botName }}</bdi></strong><small v-if="tg.botSource==='app'">{{ t('botFromApp') }}<template v-if="tg.tokenHint"> · <code dir="ltr">{{ tg.tokenHint }}</code></template></small><small v-else-if="tg.botSource==='config'">{{ t('botFromConfig') }}</small></span>
    <span class="grow"/>
    <template v-if="tg.botSource==='app'">
     <template v-if="confirming==='bot'"><button :disabled="busy" @click="confirming=null">{{ t('cancel') }}</button><button class="danger" :disabled="busy" @click="removeBot">{{ t('tgRemoveYes') }}</button></template>
     <button v-else class="danger" :disabled="busy" @click="confirming='bot'"><Trash2/>{{ t('removeBot') }}</button>
    </template>
   </div>
   <ol v-if="!tg.configured" class="tg-steps"><li>{{ t('tgStep1') }}</li><li>{{ t('tgStep2') }}</li><li>{{ t('tgStep3') }}</li></ol>
   <form v-if="!tg.configured || tg.botSource==='app'" class="tg-token" @submit.prevent="saveBot">
    <label for="tg-token">{{ t(tg.configured ? 'botTokenReplace' : 'botToken') }}</label>
    <div class="inline-field"><input id="tg-token" v-model="token" dir="ltr" type="password" autocomplete="off" spellcheck="false" required maxlength="200" :placeholder="t('botTokenPlaceholder')"><button class="primary" :disabled="busy || !token.trim()"><KeyRound/>{{ t('saveBot') }}</button></div>
   </form>
   <p class="privacy-note"><ShieldCheck/>{{ t('tgTokenPrivacy') }}</p>
  </div>

  <p v-if="error" role="alert" class="error-text">{{ t(error) }}</p>
 </section>
</template>
