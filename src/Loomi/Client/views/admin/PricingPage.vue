<script setup>
import { ref, computed, onMounted } from 'vue';
import { Save, CircleCheck, Info } from 'lucide-vue-next';
import { t, api, action, busy, PROVIDERS, OPERATIONS } from '../../core';
// What one request costs, per provider and per action. A box left empty has no rule of its own, so the provider's default (or nothing) applies.
const prices = ref({}), loaded = ref(false), failed = ref(false), saved = ref(false), base = ref({});
const key = (p, o) => `${p}|${o || ''}`;
async function load() {
 try {
  const next = {};
  for (const r of await api('/api/pricing')) next[key(r.provider, r.operation)] = String(r.cost);
  prices.value = next; base.value = next; failed.value = false;
 } catch { failed.value = true; }
 finally { loaded.value = true; }
}
onMounted(load);
const valid = v => String(v ?? '').trim() === '' || (Number.isInteger(Number(v)) && Number(v) >= 0 && Number(v) <= 10000);
const bad = computed(() => Object.values(prices.value).some(v => !valid(v)));
// Compared as the rules they would save, so typing a price and deleting it again does not count as a change.
const rules = obj => JSON.stringify(Object.entries(obj).filter(([, v]) => String(v ?? '').trim() !== '').map(([k, v]) => [k, Number(v)]).sort());
const dirty = computed(() => rules(prices.value) !== rules(base.value));
function set(p, o, v) { prices.value = { ...prices.value, [key(p, o)]: v }; saved.value = false; }
// The list is replaced whole, so dropping a rule is simply leaving its box empty.
async function save() {
 await action(async () => {
  const rules = Object.entries(prices.value).filter(([, v]) => String(v ?? '').trim() !== '').map(([k, v]) => { const [provider, operation] = k.split('|'); return { provider, operation: operation || null, cost: Number(v) }; });
  await api('/api/pricing', 'PUT', rules); await load(); saved.value = true;
 });
}
const label = o => o ? t(o) : t('defaultPrice');
</script>

<template>
 <div class="view pricing">
  <header class="view-head">
   <div><h1>{{ t('shPricing') }}</h1><p>{{ t('pricingHelp') }}</p></div>
  </header>

  <section class="card" aria-labelledby="pr-title">
   <div class="card-head"><h2 id="pr-title">{{ t('pricingRow') }}</h2></div>
   <div v-if="!loaded" class="skel" style="height:120px"></div>
   <p v-else-if="failed" class="faint-text">{{ t('adCouldNotLoad') }}</p>
   <template v-else>
    <div class="grid" role="table" :aria-label="t('pricingRow')">
     <div class="hrow" role="row"><span role="columnheader">{{ t('provider') }}</span><span v-for="o in OPERATIONS" :key="o" role="columnheader">{{ label(o) }}</span></div>
     <div v-for="p in PROVIDERS" :key="p" class="prow" role="row">
      <b role="rowheader">{{ p }}</b>
      <label v-for="o in OPERATIONS" :key="o" class="cell" role="cell">
       <span class="lbl">{{ label(o) }}</span>
       <input class="input num" type="number" min="0" max="10000" inputmode="numeric" :value="prices[key(p, o)] ?? ''" :placeholder="o ? t('adPriceInherit') : t('adPriceNone')" :aria-label="`${p} · ${label(o)}`" :aria-invalid="!valid(prices[key(p, o)])" @input="set(p, o, $event.target.value)">
      </label>
     </div>
    </div>
    <p class="notice"><Info/><span>{{ t('pricingBlank') }} {{ t('adPricePackages') }}</span></p>
    <p v-if="bad" class="field-error">{{ t('InvalidCost') }}</p>
    <div class="dlg-actions">
     <span v-if="saved && !dirty" class="ok" role="status"><CircleCheck/>{{ t('saved') }}</span>
     <button class="btn btn-primary" :disabled="busy || bad || !dirty" @click="save"><Save/>{{ t('savePricing') }}</button>
    </div>
   </template>
  </section>
 </div>
</template>

<style scoped>
.card h2{font-size:15.5px}
.grid{display:flex;flex-direction:column}
.hrow,.prow{display:grid;grid-template-columns:140px repeat(4,minmax(0,1fr));gap:12px;align-items:center}
.hrow{font-size:12.5px;color:var(--faint);padding-bottom:8px}
.prow{padding:12px 0;border-top:1px solid var(--line)}
.prow b{font-size:14px}
.lbl{display:none}
.ok{display:inline-flex;align-items:center;gap:6px;color:var(--ok);font-size:13.5px;margin-inline-end:auto}
.input[aria-invalid=true]{border-color:color-mix(in srgb,var(--bad) 60%,var(--line))}
@media (max-width:760px){
 .hrow{display:none}
 .prow{grid-template-columns:repeat(2,minmax(0,1fr))}
 .prow b{grid-column:1/-1}
 .cell{display:flex;flex-direction:column;gap:4px}
 .lbl{display:block;font-size:12.5px;color:var(--muted)}
}
</style>
