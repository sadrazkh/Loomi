<script setup>
import { ref, computed, onMounted } from 'vue';
import { Gift, Send } from 'lucide-vue-next';
import { t, tf, api } from '../../core';
// Whether strangers may sign up in the bot, what they start with, and how many a day: what a gift costs is the ChatGPT accounts' daily capacity, so it is said here.
const MAX_GIFT = 50, MAX_LIMIT = 1000;
const s = ref(null), gift = ref(0), limit = ref(0), saving = ref(false), error = ref('');
onMounted(async () => {
 try { s.value = await api('/api/signup/settings'); gift.value = s.value.giftImages; limit.value = s.value.dailyLimit; }
 catch { s.value = false; }
});
const inRange = (v, max) => String(v).trim() !== '' && Number.isInteger(Number(v)) && Number(v) >= 0 && Number(v) <= max;
const valid = computed(() => inRange(gift.value, MAX_GIFT) && inRange(limit.value, MAX_LIMIT));
const dirty = computed(() => !!s.value && (Number(gift.value) !== s.value.giftImages || Number(limit.value) !== s.value.dailyLimit));
async function save(changes) {
 if (saving.value) return; saving.value = true; error.value = '';
 try { s.value = await api('/api/signup/settings', 'PUT', changes); }
 catch (e) { error.value = e.message; }
 finally { saving.value = false; }
}
// Whatever the server kept is put back in the box, so a refused change does not leave the switch showing something that is not so.
async function setOpen(e) { const box = e.target; await save({ open: box.checked }); box.checked = !!s.value?.open; }
const saveNumbers = () => valid.value && dirty.value && save({ giftImages: Number(gift.value), dailyLimit: Number(limit.value) });
</script>

<template>
 <section class="card" aria-labelledby="bt-signup">
  <div class="card-head"><h2 id="bt-signup"><Gift/>{{ t('suCard') }}</h2></div>
  <p class="muted-text">{{ t('suCardHelp') }}</p>
  <div v-if="s === null" class="skel" style="height:40px"></div>
  <p v-else-if="s === false" class="faint-text">{{ t('adCouldNotLoad') }}</p>
  <template v-else>
   <label class="switch"><input type="checkbox" role="switch" :checked="s.open" :disabled="saving" @change="setOpen"><span>{{ t('suOpen') }}</span></label>
   <p v-if="!s.telegram" class="notice notice-warn"><Send/><span>{{ t('suNoBot') }}</span></p>
   <form class="stack" @submit.prevent="saveNumbers">
    <div class="nums">
     <div class="field"><label for="su-gift">{{ t('suGift') }}</label><input id="su-gift" v-model="gift" class="input num" type="number" min="0" :max="MAX_GIFT" required :aria-invalid="!inRange(gift, MAX_GIFT)"><span class="field-help">{{ tf('suGiftHelp', 0, MAX_GIFT) }}</span></div>
     <div class="field"><label for="su-limit">{{ t('suLimit') }}</label><input id="su-limit" v-model="limit" class="input num" type="number" min="0" :max="MAX_LIMIT" required :aria-invalid="!inRange(limit, MAX_LIMIT)"><span class="field-help">{{ tf('suLimitHelp', 0, MAX_LIMIT) }}</span></div>
    </div>
    <div class="line"><span class="grow faint-text">{{ tf('suToday', s.today, s.dailyLimit) }}</span><button class="btn btn-sm" :class="{ 'btn-primary': dirty }" :disabled="saving || !dirty || !valid">{{ t('save') }}</button></div>
   </form>
   <p class="field-help">{{ tf('suCost', s.giftImages * s.dailyLimit) }}</p>
   <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
  </template>
 </section>
</template>

<style scoped>
.card h2{font-size:15.5px}
.card-head h2 svg{color:var(--accent)}
.switch{align-items:flex-start}
.switch input{flex:none;margin-top:2px}
.nums{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;align-items:start}
.line{display:flex;align-items:center;gap:10px;flex-wrap:wrap}
@media (max-width:420px){.nums{grid-template-columns:minmax(0,1fr)}}
</style>
