<script setup>
import { ref, computed, onMounted, onUnmounted } from 'vue';
import { Megaphone, Send, Users, History } from 'lucide-vue-next';
import { t, tf, api, action, busy, onTick } from '../../core';
import { dateTime } from './kit';
// The switches for the news the bot sends on its own, whom each would reach right now, and when the last digest went out.
const KINDS = [
 { key: 'newTemplates', label: 'ntNewTemplates', help: 'ntNewTemplatesHelp' },
 { key: 'winBack', label: 'ntWinBack', help: 'ntWinBackHelp' },
 { key: 'lowCredit', label: 'ntLowCredit', help: 'ntLowCreditHelp' },
];
const state = ref(null);
// A look that fails later keeps what is on screen; only a first one with nothing to show says so.
async function load() { try { state.value = await api('/api/notifications/settings'); } catch { if (!state.value) state.value = false; } }
onMounted(load);
// Every third beat, as the other management pages do: the numbers move as people use Loomi, and a bot set up on this same page clears the hint below.
let beat = 0;
const stop = onTick(() => { if (++beat % 3 === 0) load(); });
onUnmounted(stop);
// Whatever the server kept is put back in the box, so a refused change does not leave the switch showing something that is not so.
async function set(key, e) {
 const box = e.target, on = box.checked;
 await action(async () => { state.value = await api('/api/notifications/settings', 'PUT', { [key]: on }); });
 box.checked = !!state.value?.[key];
}
const counted = (count, none, one, many) => count === 0 ? t(none) : count === 1 ? t(one) : tf(many, count);
const people = count => counted(count, 'ntReachNone', 'ntReachOne', 'ntReach');
const waiting = computed(() => counted(state.value?.reach?.waitingTemplates ?? 0, 'ntWaitingNone', 'ntWaitingOne', 'ntWaiting'));
const lastDigest = computed(() => state.value?.reach?.lastDigestAt ? tf('ntLastDigest', dateTime(state.value.reach.lastDigestAt)) : t('ntNeverSent'));
</script>

<template>
 <section class="card" aria-labelledby="nt-title">
  <div class="card-head"><h2 id="nt-title"><Megaphone/>{{ t('ntTitle') }}</h2></div>
  <p class="muted-text">{{ t('ntIntro') }}</p>
  <div v-if="state === null" class="skel" style="height:160px"></div>
  <p v-else-if="state === false" class="faint-text">{{ t('adCouldNotLoad') }}</p>
  <template v-else>
   <p v-if="!state.telegram" class="notice notice-warn"><Send/><span>{{ t('ntNoBot') }}</span></p>
   <div v-for="k in KINDS" :key="k.key" class="kind">
    <label class="switch"><input type="checkbox" role="switch" :checked="state[k.key]" :disabled="busy" @change="set(k.key, $event)"><span>{{ t(k.label) }}</span></label>
    <p class="field-help">{{ t(k.help) }}</p>
    <p class="fact"><Users/><span>{{ people(state.reach[k.key]) }}<template v-if="k.key === 'newTemplates'">{{ t('adSep') }}{{ waiting }}</template></span></p>
   </div>
   <p class="fact"><History/><span>{{ lastDigest }}</span></p>
  </template>
 </section>
</template>

<style scoped>
.card h2{font-size:15.5px}
.card-head h2 svg{color:var(--accent)}
.kind{display:flex;flex-direction:column;gap:6px;padding-block-start:14px;border-block-start:1px solid var(--line)}
.switch{align-items:flex-start}
.switch input{flex:none;margin-top:2px}
.fact{display:flex;align-items:flex-start;gap:8px;font-size:12.5px;line-height:1.6;color:var(--muted)}
.fact svg.lucide{width:15px;height:15px;margin-top:2px;color:var(--faint)}
</style>
