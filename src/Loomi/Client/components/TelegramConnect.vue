<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import { Send, ExternalLink, Copy, Check, LoaderCircle, CircleCheck, AlertTriangle, Bot } from 'lucide-vue-next';
import { t, tf, api, locale, isOwner, go, when } from '../core';
// The person's own link to the bot. Linking is a single press: the code rides in a t.me deep link, so Telegram opens on the bot with the code
// already filled in, the person presses Start, and this page notices by itself. The bot itself is the owner's, set up under Management → Bot.
const emit = defineEmits(['state']);
// On the bot page itself the setup is right above, so there is no button pointing there.
const props = defineProps({ onBotPage: Boolean });
const tg = ref(null), busy = ref(false), error = ref(''), copied = ref(false), confirming = ref(false);
let timer = null, alive = true;
const waiting = computed(() => !!tg.value && !tg.value.linked && !!tg.value.code);
const deepLink = computed(() => tg.value?.botUsername && tg.value?.code ? `https://t.me/${tg.value.botUsername}?start=${tg.value.code}` : null);
const botName = computed(() => tg.value?.botUsername ? '@' + tg.value.botUsername : t('telegramBot'));
const since = computed(() => tg.value?.linkedAt ? when(tg.value.linkedAt).toLocaleDateString(locale.value, { day: 'numeric', month: 'long', year: 'numeric' }) : '');
const until = computed(() => tg.value?.expiresAt ? when(tg.value.expiresAt).toLocaleTimeString(locale.value, { hour: '2-digit', minute: '2-digit' }) : '');
async function run(fn) { if (busy.value) return; busy.value = true; error.value = ''; try { await fn(); } catch (e) { error.value = e.message; } finally { busy.value = false; } }
// While a code is out, look every few seconds: the link happens in Telegram, and nothing else would tell this page.
async function load() {
 const next = await api('/api/telegram/link');
 if (!alive) return;
 tg.value = next; emit('state', next);
 clearTimeout(timer); if (waiting.value) timer = setTimeout(() => load().catch(() => {}), 3000);
}
async function connect() {
 // Opened inside the click itself, so no popup blocker stands in the way; pointed at the bot once the code exists, closed again if it never does.
 const tab = window.open('', '_blank');
 let url = null;
 await run(async () => { const issued = await api('/api/telegram/link', 'POST'); if (issued.botUsername) url = `https://t.me/${issued.botUsername}?start=${issued.code}`; await load(); });
 if (tab) { if (url) tab.location.href = url; else tab.close(); }
}
// Without a secure origin there is no clipboard; the code stays on screen to be typed instead.
async function copyCode() { try { await navigator.clipboard.writeText(`/start ${tg.value.code}`); copied.value = true; setTimeout(() => copied.value = false, 2000); } catch { /* shown on screen anyway */ } }
async function unlink() { await run(async () => { await api('/api/telegram/link', 'DELETE'); confirming.value = false; await load(); }); }
onMounted(() => load().catch(e => error.value = e.message));
onBeforeUnmount(() => { alive = false; clearTimeout(timer); });
</script>

<template>
 <div class="tgc">
  <p class="muted-text">{{ t('telegramHelp') }}</p>

  <div v-if="!tg && !error" class="skel" style="height:64px"></div>

  <template v-else-if="tg?.linked">
   <div class="state ok">
    <CircleCheck/>
    <span class="grow"><strong>{{ t('tgConnected') }}</strong><small>{{ tf('tgConnectedSince', since) }}{{ t('adSep') }}<bdi dir="ltr">{{ botName }}</bdi></small></span>
   </div>
   <div v-if="confirming" class="notice notice-warn"><AlertTriangle/><span class="grow">{{ t('unlinkConfirm') }}</span></div>
   <div class="dlg-actions">
    <template v-if="confirming"><button class="btn" :disabled="busy" @click="confirming=false">{{ t('cancel') }}</button><button class="btn btn-danger btn-primary" :disabled="busy" @click="unlink">{{ t('tgUnlinkYes') }}</button></template>
    <template v-else>
     <a v-if="tg.botUsername" class="btn" :href="`https://t.me/${tg.botUsername}`" target="_blank" rel="noopener noreferrer"><ExternalLink/>{{ t('openBot') }}</a>
     <button class="btn btn-danger" :disabled="busy" @click="confirming=true">{{ t('unlink') }}</button>
    </template>
   </div>
  </template>

  <template v-else-if="waiting">
   <div class="state" role="status">
    <LoaderCircle class="spin"/>
    <span class="grow"><strong>{{ t('tgWaiting') }}</strong><small>{{ t('tgWaitingHelp') }}</small></span>
   </div>
   <div class="manual">
    <span>{{ tf('tgManual', botName) }}</span>
    <span class="code"><code dir="ltr">/start {{ tg.code }}</code><button class="btn btn-ghost btn-icon btn-sm" :aria-label="t('tgCopy')" :title="t('tgCopy')" @click="copyCode"><Check v-if="copied"/><Copy v-else/></button></span>
    <small class="faint-text">{{ tf('tgValidUntil', until) }}</small>
   </div>
   <div class="dlg-actions"><a v-if="deepLink" class="btn btn-primary" :href="deepLink" target="_blank" rel="noopener noreferrer"><Send/>{{ t('tgOpenAgain') }}</a></div>
  </template>

  <template v-else-if="tg?.configured">
   <p class="field-help">{{ t('tgConnectHelp') }}</p>
   <div class="dlg-actions"><button class="btn btn-primary" :disabled="busy" @click="connect"><Send/>{{ t('tgConnect') }}</button></div>
  </template>

  <template v-else-if="tg">
   <p class="notice notice-warn"><AlertTriangle/><span>{{ t(onBotPage ? 'tcNoBotHere' : isOwner ? 'tcNoBotOwner' : 'telegramOff') }}</span></p>
   <div v-if="isOwner && !onBotPage" class="dlg-actions"><button class="btn" @click="go('admin', 'bot')"><Bot/>{{ t('tcSetUpBot') }}</button></div>
  </template>

  <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
 </div>
</template>

<style scoped>
.tgc{display:flex;flex-direction:column;gap:12px}
.state{display:flex;align-items:center;gap:12px;padding:12px 14px;border-radius:var(--r2);background:var(--s2)}
.state>svg{color:var(--accent)}
.state.ok{background:var(--ok-soft)}
.state.ok>svg{color:var(--ok)}
.state strong{display:block;font-size:14px}
.state small{display:block;color:var(--muted);font-size:12.5px}
.manual{display:flex;flex-direction:column;gap:6px;font-size:13px;color:var(--muted)}
.code{display:flex;align-items:center;gap:6px;padding-block:4px;padding-inline:12px 4px;border-radius:var(--r2);border:1px solid var(--line);background:var(--bg);align-self:flex-start;max-width:100%}
.code code{color:var(--text);font-size:14px;overflow-wrap:anywhere}
.dlg-actions{margin-top:0}
</style>
