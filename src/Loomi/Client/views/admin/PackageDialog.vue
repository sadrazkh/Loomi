<script setup>
import { ref, computed, watch, nextTick } from 'vue';
import { CalendarDays, Sparkles } from 'lucide-vue-next';
import Modal from '../../components/Modal.vue';
import { t, tf, api, date, when } from '../../core';
import { KINDS, KIND_ICON, AMOUNT_LIMITS, MAX_DAYS, defaultName, pkgName, dayMonth, bidi } from './kit';
// Giving one person a package: its kind, how much, for how long, and from when. The sentence at the bottom says in plain words what will happen,
// with the real end date, so the owner reads it before pressing the button rather than after.
const props = defineProps({ user: { type: Object, required: true }, packages: { type: Array, default: () => [] } });
const emit = defineEmits(['close', 'created']);
const DEFAULTS = { Credits: 200, Unlimited: 30, Daily: 10 };
const PRESETS = [7, 30, 90];
const kind = ref('Daily'), amount = ref(DEFAULTS.Daily), days = ref(30), custom = ref(false), later = ref(false), name = ref(''), note = ref(''), notify = ref(true);
const busy = ref(false), error = ref(''), daysInput = ref(null);
watch(kind, k => { amount.value = DEFAULTS[k]; error.value = ''; });

const whole = v => Number.isInteger(Number(v)) && String(v).trim() !== '';
const amountOk = computed(() => { const [lo, hi] = AMOUNT_LIMITS[kind.value]; return whole(amount.value) && Number(amount.value) >= lo && Number(amount.value) <= hi; });
const daysOk = computed(() => whole(days.value) && Number(days.value) >= 1 && Number(days.value) <= MAX_DAYS);
const valid = computed(() => amountOk.value && daysOk.value && name.value.trim().length <= 80 && note.value.trim().length <= 400);

// Starting after the current one queues it behind the latest package of the same kind that has not ended, exactly as the server does.
const current = computed(() => props.packages.filter(p => p.kind === kind.value && p.state !== 'Ended' && when(p.endsAt) > Date.now()).sort((a, b) => when(b.endsAt) - when(a.endsAt))[0] || null);
const queued = computed(() => later.value && !!current.value);
const start = computed(() => queued.value ? when(current.value.endsAt) : new Date());
const end = computed(() => new Date(start.value.getTime() + (daysOk.value ? Number(days.value) : 0) * 86400000));
const who = computed(() => bidi(props.user.username));
const summary = computed(() => {
 if (!valid.value) return '';
 const from = queued.value ? tf('pkFromAfter', dayMonth(start.value.toISOString())) : t('pkFromToday');
 const until = date(end.value.toISOString()), a = Number(amount.value);
 const main = kind.value === 'Credits' ? tf('pkSumCredits', who.value, from, until, a)
  : kind.value === 'Unlimited' ? (a ? tf('pkSumUnlimitedCap', who.value, from, until, a) : tf('pkSumUnlimited', who.value, from, until))
  : tf('pkSumDaily', who.value, from, until, a);
 const quota = props.user.dailyQuota > 0 && props.user.dailyQuota <= 100000 && kind.value !== 'Credits' ? ' ' + tf('pkSumQuota', props.user.dailyQuota) : '';
 return main + quota;
});
const AMOUNT_LABEL = { Credits: 'pkAmountCredits', Unlimited: 'pkAmountCap', Daily: 'pkAmountDaily' };
const AMOUNT_UNIT = { Credits: 'pkUnitCredits', Unlimited: 'pkUnitImages', Daily: 'pkUnitImages' };
function preset(d) { custom.value = false; days.value = d; }
async function other() { custom.value = true; if (PRESETS.includes(Number(days.value))) days.value = ''; await nextTick(); daysInput.value?.focus(); }

async function submit() {
 if (busy.value || !valid.value) return; busy.value = true; error.value = '';
 try {
  const made = await api(`/api/users/${props.user.id}/packages`, 'POST', { kind: kind.value, amount: Number(amount.value), days: Number(days.value), name: name.value.trim() || null, note: note.value.trim() || null, startAfterCurrent: later.value, notify: notify.value });
  emit('created', made);
 } catch (e) { error.value = e.message; }
 finally { busy.value = false; }
}
</script>

<template>
 <Modal :title="tf('pkDialogTitle', who)" :close-label="t('close')" size="lg" @close="emit('close')">
  <form class="pd" @submit.prevent="submit">
   <fieldset class="kinds">
    <legend class="sr-only">{{ t('pkKind') }}</legend>
    <label v-for="k in KINDS" :key="k" class="kind" :class="{ on: kind === k }">
     <input v-model="kind" class="sr-only" type="radio" name="pkg-kind" :value="k">
     <component :is="KIND_ICON[k]"/>
     <b>{{ t('pkKind' + k) }}</b>
     <small>{{ t('pkKind' + k + 'Help') }}</small>
    </label>
   </fieldset>

   <div class="fields">
    <div class="field">
     <label for="pd-amount">{{ t(AMOUNT_LABEL[kind]) }}</label>
     <div class="affix"><input id="pd-amount" v-model="amount" class="input num" type="number" :min="AMOUNT_LIMITS[kind][0]" :max="AMOUNT_LIMITS[kind][1]" required :aria-invalid="!amountOk"><span>{{ t(AMOUNT_UNIT[kind]) }}</span></div>
     <span v-if="kind === 'Unlimited'" class="field-help">{{ t('pkCapHelp') }}</span>
     <span v-if="!amountOk" class="field-error">{{ tf('pkAmountRange', AMOUNT_LIMITS[kind][0], AMOUNT_LIMITS[kind][1]) }}</span>
    </div>
    <div class="field">
     <label for="pd-name">{{ t('pkName') }}</label>
     <input id="pd-name" v-model="name" class="input" maxlength="80" :placeholder="amountOk ? defaultName(kind, Number(amount)) : ''">
    </div>
    <div class="field">
     <span id="pd-days" class="field-label">{{ t('pkDuration') }}</span>
     <div class="cluster" role="group" aria-labelledby="pd-days">
      <button v-for="d in PRESETS" :key="d" type="button" class="chip" :class="{ 'chip-on': !custom && Number(days) === d }" :aria-pressed="!custom && Number(days) === d" @click="preset(d)">{{ tf('pkDays', d) }}</button>
      <button type="button" class="chip" :class="{ 'chip-on': custom }" :aria-pressed="custom" @click="other">{{ t('pkCustom') }}</button>
     </div>
     <div v-if="custom" class="affix"><input ref="daysInput" v-model="days" class="input num" type="number" min="1" :max="MAX_DAYS" required :aria-label="t('pkDaysCount')" :aria-invalid="!daysOk"><span>{{ t('pkUnitDays') }}</span></div>
     <span v-if="custom && !daysOk && days !== ''" class="field-error">{{ tf('pkDaysRange', MAX_DAYS) }}</span>
    </div>
    <div class="field">
     <span id="pd-start" class="field-label">{{ t('pkStart') }}</span>
     <div class="seg" role="group" aria-labelledby="pd-start"><button type="button" :aria-pressed="!later" @click="later = false">{{ t('pkStartNow') }}</button><button type="button" :aria-pressed="later" @click="later = true">{{ t('pkStartAfter') }}</button></div>
     <span v-if="later && current" class="field-help">{{ tf('pkAfterWhich', bidi(pkgName(current)), dayMonth(current.endsAt)) }}</span>
     <span v-else-if="later" class="field-help">{{ t('pkAfterNone') }}</span>
    </div>
    <div class="field wide">
     <label for="pd-note">{{ t('pkNote') }}</label>
     <input id="pd-note" v-model="note" class="input" maxlength="400" :placeholder="t('pkNotePlaceholder')">
    </div>
   </div>

   <p class="notice sum" aria-live="polite"><CalendarDays/><span>{{ summary || t('pkSumInvalid') }}</span></p>
   <label class="check"><input v-model="notify" type="checkbox">{{ tf('pkNotify', who) }}</label>
   <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
   <div class="dlg-actions">
    <button type="button" class="btn" @click="emit('close')">{{ t('cancel') }}</button>
    <button class="btn btn-primary" :disabled="busy || !valid"><Sparkles/>{{ busy ? t('processing') : t(queued ? 'pkSchedule' : 'pkActivate') }}</button>
   </div>
  </form>
 </Modal>
</template>

<style scoped>
.pd{display:flex;flex-direction:column;gap:16px}
.kinds{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px;border:0;margin:0;padding:0;min-width:0}
.kind{display:flex;flex-direction:column;gap:4px;padding:12px;border-radius:var(--r);border:1.5px solid var(--line);cursor:pointer;min-width:0;transition:border-color .15s,background .15s}
.kind:hover{border-color:var(--line2)}
.kind svg{width:20px;height:20px;color:var(--muted)}
.kind b{font-size:14px}
.kind small{font-size:12px;color:var(--muted);line-height:1.55}
.kind.on{border-color:var(--accent);background:var(--accent-soft)}
.kind.on svg{color:var(--accent)}
.kind:has(input:focus-visible){outline:2px solid var(--accent);outline-offset:2px}
.fields{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px 16px}
.wide{grid-column:1/-1}
.affix{position:relative;display:flex;align-items:center}
.affix .input{padding-inline-end:64px}
.affix span{position:absolute;inset-inline-end:12px;color:var(--faint);font-size:13px;pointer-events:none}
.input[aria-invalid=true]{border-color:color-mix(in srgb,var(--bad) 60%,var(--line))}
.sum{margin:0}
@media (max-width:640px){
 .kinds{grid-template-columns:1fr}
 .kind{display:grid;grid-template-columns:22px minmax(0,1fr);column-gap:10px;align-items:center}
 .kind small{grid-column:2}
 .fields{grid-template-columns:1fr}
}
</style>
