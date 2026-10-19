<script setup>
import { ref, computed, onMounted } from 'vue';
import { Bot, KeyRound, Trash2, ShieldCheck, Send, Moon, BellRing, Users, AlertTriangle } from 'lucide-vue-next';
import TelegramConnect from '../../components/TelegramConnect.vue';
import { t, api, action, busy, locale } from '../../core';
// The workspace's Telegram bot: setting it up, the owner's own chat with it, the nightly summary, and what the bot sends to whom.
const tg = ref(null), token = ref(''), confirming = ref(false), saving = ref(false), error = ref(''), daily = ref(null);
async function load() { try { tg.value = await api('/api/telegram/link'); } catch (e) { error.value = e.message; } }
async function loadDaily() { try { daily.value = await api('/api/usage/daily-report'); } catch { daily.value = false; } }
onMounted(() => { load(); loadDaily(); });
const botName = computed(() => tg.value?.botUsername ? '@' + tg.value.botUsername : t('telegramBot'));
// The token is checked with Telegram before it is kept, so a mistyped one is refused here with a reason.
async function saveBot() {
 if (saving.value) return; saving.value = true; error.value = '';
 try { await api('/api/telegram/bot', 'PUT', { token: token.value.trim() }); token.value = ''; await load(); loadDaily(); }
 catch (e) { error.value = e.message; } finally { saving.value = false; }
}
async function removeBot() {
 if (saving.value) return; saving.value = true; error.value = '';
 try { await api('/api/telegram/bot', 'DELETE'); confirming.value = false; await load(); loadDaily(); }
 catch (e) { error.value = e.message; } finally { saving.value = false; }
}
const dailyTime = computed(() => daily.value ? new Date(2000, 0, 1, daily.value.hour).toLocaleTimeString(locale.value, { hour: '2-digit', minute: '2-digit' }) : '');
// Whatever the server kept is put back in the box, so a refused change does not leave the switch showing something that is not so.
async function setDaily(e) {
 const box = e.target, enabled = box.checked;
 await action(async () => { daily.value = await api('/api/usage/daily-report', 'PUT', { enabled }); });
 box.checked = !!daily.value?.enabled;
}
const OWNER_SENDS = ['btOwnerAlerts', 'btOwnerReport', 'btOwnerCommands'];
const MEMBER_SENDS = ['btMemberMake', 'btMemberReminders', 'btMemberGranted', 'btMemberBalance'];
</script>

<template>
 <div class="view bot">
  <header class="view-head">
   <div><h1>{{ t('shBot') }}</h1><p>{{ t('btIntro') }}</p></div>
  </header>

  <div class="cols">
   <div class="main">
    <section class="card" aria-labelledby="bt-setup">
     <div class="card-head"><h2 id="bt-setup"><Bot/>{{ t('telegramBot') }}</h2></div>
     <div v-if="!tg && !error" class="skel" style="height:64px"></div>
     <template v-else-if="tg">
      <div v-if="tg.configured" class="state">
       <span class="dot"></span>
       <span class="grow txt"><strong><bdi dir="ltr">{{ botName }}</bdi></strong>
        <small v-if="tg.botSource === 'app'">{{ t('botFromApp') }}<template v-if="tg.tokenHint">{{ t('adSep') }}<code dir="ltr">{{ tg.tokenHint }}</code></template></small>
        <small v-else-if="tg.botSource === 'config'">{{ t('botFromConfig') }}</small>
       </span>
       <template v-if="tg.botSource === 'app'">
        <template v-if="confirming"><button class="btn btn-sm" :disabled="saving" @click="confirming = false">{{ t('cancel') }}</button><button class="btn btn-sm btn-danger btn-primary" :disabled="saving" @click="removeBot">{{ t('tgRemoveYes') }}</button></template>
        <button v-else class="btn btn-sm btn-danger" @click="confirming = true"><Trash2/>{{ t('removeBot') }}</button>
       </template>
      </div>
      <p v-if="confirming" class="notice notice-warn"><AlertTriangle/><span>{{ t('removeBotConfirm') }}</span></p>
      <template v-if="!tg.configured">
       <p class="faint-text">{{ t('botNotSet') }}</p>
       <ol class="steps"><li>{{ t('tgStep1') }}</li><li>{{ t('tgStep2') }}</li><li>{{ t('btStep3') }}</li></ol>
      </template>
      <form v-if="!tg.configured || tg.botSource === 'app'" class="token" @submit.prevent="saveBot">
       <div class="field grow"><label for="bt-token">{{ t(tg.configured ? 'botTokenReplace' : 'botToken') }}</label><input id="bt-token" v-model="token" class="input" dir="ltr" type="password" autocomplete="off" spellcheck="false" required maxlength="200" :placeholder="t('botTokenPlaceholder')"></div>
       <button class="btn btn-primary" :disabled="saving || !token.trim()"><KeyRound/>{{ saving ? t('processing') : t('saveBot') }}</button>
      </form>
      <p class="field-help privacy"><ShieldCheck/>{{ t('tgTokenPrivacy') }}</p>
     </template>
     <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
    </section>

    <section class="card" aria-labelledby="bt-mine">
     <div class="card-head"><h2 id="bt-mine"><Send/>{{ t('btYourChat') }}</h2></div>
     <p class="field-help">{{ t('btYourChatHelp') }}</p>
     <TelegramConnect :key="tg?.botUsername || 'none'" on-bot-page/>
    </section>
   </div>

   <div class="side">
    <section class="card" aria-labelledby="bt-daily">
     <div class="card-head"><h2 id="bt-daily"><Moon/>{{ t('usageDaily') }}</h2></div>
     <p class="muted-text">{{ t('usageDailyHelp') }}</p>
     <div v-if="daily === null" class="skel" style="height:40px"></div>
     <p v-else-if="daily === false" class="faint-text">{{ t('adCouldNotLoad') }}</p>
     <template v-else>
      <label class="switch"><input type="checkbox" role="switch" :checked="daily.enabled" :disabled="busy" @change="setDaily"><span>{{ t('usageDailySwitch').replace('{time}', dailyTime) }}</span></label>
      <p v-if="!daily.telegram" class="notice notice-warn"><Send/><span>{{ t('btDailyNoBot') }}</span></p>
     </template>
    </section>

    <section class="card" aria-labelledby="bt-what">
     <div class="card-head"><h2 id="bt-what"><BellRing/>{{ t('btWhat') }}</h2></div>
     <div class="who"><h3>{{ t('btToYou') }}</h3><ul><li v-for="k in OWNER_SENDS" :key="k">{{ t(k) }}</li></ul></div>
     <div class="who"><h3><Users/>{{ t('btToMembers') }}</h3><ul><li v-for="k in MEMBER_SENDS" :key="k">{{ t(k) }}</li></ul></div>
    </section>
   </div>
  </div>
 </div>
</template>

<style scoped>
.cols{display:grid;grid-template-columns:minmax(0,1.3fr) minmax(0,1fr);gap:18px;align-items:start}
.main,.side{display:flex;flex-direction:column;gap:18px;min-width:0}
.card h2{font-size:15.5px}
.card-head h2 svg{color:var(--accent)}
.state{display:flex;align-items:center;gap:12px;flex-wrap:wrap;padding:12px 14px;border-radius:var(--r2);background:var(--s2)}
.dot{width:9px;height:9px;border-radius:50%;background:var(--ok);flex:none}
.txt{display:flex;flex-direction:column;min-width:0}
.txt small{color:var(--muted);font-size:12.5px}
.steps{margin:0;padding-inline-start:20px;display:grid;gap:6px;font-size:13.5px;color:var(--muted)}
.token{display:flex;align-items:flex-end;gap:10px;flex-wrap:wrap}
.privacy{display:flex;gap:8px;align-items:flex-start}
.privacy svg{width:15px;height:15px;margin-top:2px;color:var(--faint)}
.switch{align-items:flex-start}
.switch input{flex:none;margin-top:2px}
.who h3{display:flex;align-items:center;gap:8px;font-size:13.5px;margin-bottom:6px}
.who h3 svg{width:16px;height:16px;color:var(--muted)}
.who ul{margin:0;padding-inline-start:18px;display:grid;gap:6px;color:var(--muted);font-size:13px;line-height:1.7}
@media (max-width:960px){.cols{grid-template-columns:minmax(0,1fr)}}
</style>
