<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import { PackagePlus, Plus, Check, WandSparkles, CircleStop, AlertTriangle } from 'lucide-vue-next';
import { t, tf, busy, error, api, action, onTick } from '../core';
// The starter pack on the owner's side of the templates page: adding it, then making the samples it has none of. Prominent while there is something to
// decide, one quiet line once there is not. Starting is the owner's choice because each sample spends an image, so the number is said right beside the button.
const emit = defineEmits(['changed']);
const status = ref(null), added = ref(null);
async function load() {
 const was = status.value?.on;
 status.value = await api('/api/templates/samples/status');
 // While a run is on the cards below change too, and once more as it ends.
 if (was || status.value.on) emit('changed');
}
onMounted(() => load().catch(e => { error.value = e.message; }));
onBeforeUnmount(onTick(() => { load().catch(() => {}); }));
const missing = computed(() => status.value ? status.value.packTotal - status.value.packAdded : 0);
const fresh = computed(() => status.value?.packAdded === 0);
const samples = computed(() => !!status.value && (status.value.on || status.value.withoutSample > 0));
const addedLine = computed(() => {
 const a = added.value; if (!a) return '';
 if (!a.added) return t('spNothingToAdd');
 return [tf('spAdded', a.added), a.categories ? tf('spAddedCategories', a.categories) : '', a.existing ? tf('spAddedExisting', a.existing) : ''].filter(Boolean).join(' ');
});
const percent = computed(() => { const s = status.value; return s?.total ? Math.round((s.done + s.failed) / s.total * 100) : 0; });
const now = computed(() => {
 const s = status.value;
 if (s?.drawing) return tf('spDrawing', s.drawing);
 return t(s?.waiting === 'people' ? 'spWaitPeople' : s?.waiting === 'capacity' ? 'spWaitCapacity' : 'spNextSoon');
});
async function addPack() { await action(async () => { added.value = await api('/api/templates/starter', 'POST'); await load(); emit('changed'); }); }
async function start() { await action(async () => { status.value = await api('/api/templates/samples/start', 'POST'); emit('changed'); }); }
async function stop() { await action(async () => { status.value = await api('/api/templates/samples/stop', 'POST'); emit('changed'); }); }
</script>
<template>
 <section v-if="status" class="card starter" :class="{ quiet: !fresh && !samples }" aria-labelledby="starter-title">
  <div class="card-head">
   <h2 id="starter-title"><PackagePlus/>{{ t('spTitle') }}</h2>
   <span class="grow"/>
   <span v-if="!missing" class="pill pill-ok"><Check/>{{ tf('spPackIn', status.packAdded) }}</span>
   <button v-else type="button" class="btn" :class="fresh ? 'btn-primary' : 'btn-sm'" :disabled="busy" @click="addPack"><Plus/>{{ t('spAdd') }}</button>
  </div>
  <p v-if="fresh" class="field-help">{{ tf('spIntro', status.packTotal) }}</p>
  <p v-else-if="missing" class="field-help">{{ tf('spSomeMissing', missing) }}</p>
  <p v-if="added" class="notice notice-ok" role="status"><Check/><span>{{ addedLine }}</span></p>

  <div v-if="samples" class="samples">
   <template v-if="status.on">
    <div class="line"><b>{{ tf('spProgress', status.done, status.total) }}</b><small v-if="status.failed" class="failed">{{ tf('spProgressFailed', status.failed) }}</small></div>
    <div class="bar" role="progressbar" :aria-valuenow="status.done + status.failed" aria-valuemin="0" :aria-valuemax="status.total" :aria-label="tf('spProgress', status.done, status.total)"><i :style="{ width: percent + '%' }"></i></div>
    <p class="now" role="status" dir="auto">{{ now }}</p>
    <p v-if="status.imagesLeftToday === null" class="notice notice-warn"><AlertTriangle/><span>{{ t('spNoAccounts') }}</span></p>
    <div class="line"><p class="field-help">{{ t('spStopHelp') }}</p><button type="button" class="btn btn-sm" :disabled="busy" @click="stop"><CircleStop/>{{ t('spStop') }}</button></div>
   </template>
   <template v-else>
    <p>{{ tf('spNoSample', status.withoutSample) }}</p>
    <p class="notice notice-warn"><AlertTriangle/><span>{{ tf('spWarn', status.withoutSample) }} {{ status.imagesLeftToday === null ? t('spNoAccounts') : tf('spLeftToday', status.imagesLeftToday) }}</span></p>
    <p class="field-help">{{ t('spHow') }}</p>
    <p v-if="status.failed" class="field-help">{{ tf('spFailedBefore', status.failed) }}</p>
    <div class="cluster"><button type="button" class="btn btn-primary" :disabled="busy" @click="start"><WandSparkles/>{{ t('spStart') }}</button></div>
   </template>
  </div>
 </section>
</template>
<style scoped>
.starter.quiet{padding-block:10px}
.card-head h2{font-size:15px}
.card-head h2 svg.lucide{color:var(--accent)}
.samples{display:flex;flex-direction:column;gap:12px;padding-top:14px;border-top:1px solid var(--line);font-size:13.5px;line-height:1.7}
.line{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap}
.line b{font-weight:600}
.failed{color:var(--bad);font-size:12.5px}
.now{color:var(--muted);overflow-wrap:anywhere}
</style>
