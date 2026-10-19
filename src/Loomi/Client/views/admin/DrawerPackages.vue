<script setup>
import { ref, computed, watch, onMounted } from 'vue';
import { Plus, CalendarPlus, CalendarX2, ShieldCheck, Package, StickyNote, Coins } from 'lucide-vue-next';
import Modal from '../../components/Modal.vue';
import PackageRow from './PackageRow.vue';
import PackageDialog from './PackageDialog.vue';
import { t, tf, api, date, when } from '../../core';
import { pkgName, MAX_DAYS, bidi } from './kit';
// One person's packages, newest first, each with its own extend and end-early; adding one opens the package dialog.
const props = defineProps({ user: { type: Object, required: true } });
const emit = defineEmits(['changed', 'credits']);
const PRESETS = [7, 30, 90];
const list = ref(null), failed = ref(false), dialog = ref(null), target = ref(null), days = ref(30), busy = ref(false), error = ref('');
const member = computed(() => props.user.role === 'Member');
async function load() {
 const id = props.user.id;
 try { const rows = await api(`/api/users/${id}/packages`); if (id === props.user.id) { list.value = rows; failed.value = false; } }
 catch { if (id === props.user.id) { list.value = []; failed.value = true; } }
}
onMounted(load);
watch(() => props.user.id, () => { list.value = null; load(); });

function extend(p) { target.value = p; days.value = 30; error.value = ''; dialog.value = 'extend'; }
function end(p) { target.value = p; error.value = ''; dialog.value = 'end'; }
// Counted from its end, or from now once it has ended, as the server counts it.
const newEnd = computed(() => {
 const p = target.value, d = Number(days.value); if (!p || !Number.isInteger(d) || d < 1 || d > MAX_DAYS) return null;
 return new Date(Math.max(when(p.endsAt).getTime(), Date.now()) + d * 86400000);
});
async function run(fn) { if (busy.value) return; busy.value = true; error.value = ''; try { await fn(); dialog.value = null; await load(); emit('changed'); } catch (e) { error.value = e.message; } finally { busy.value = false; } }
const doExtend = () => run(() => api(`/api/users/${props.user.id}/packages/${target.value.id}/extend`, 'POST', { days: Number(days.value) }));
const doEnd = () => run(() => api(`/api/users/${props.user.id}/packages/${target.value.id}/end`, 'POST'));
async function created() { dialog.value = null; await load(); emit('changed'); }
</script>

<template>
 <div class="dp">
  <p v-if="!member" class="notice"><ShieldCheck/><span>{{ t('pkOwnerNone') }}</span></p>
  <div v-if="list === null" class="stack"><div v-for="i in 2" :key="i" class="skel" style="height:64px"></div></div>
  <div v-else-if="!list.length" class="quiet"><Package/><span>{{ t(failed ? 'pkNotAvailable' : 'pkNone') }}</span></div>
  <div v-else class="pkgs">
   <div v-for="p in list" :key="p.id" class="one">
    <PackageRow :pkg="p">
     <button class="btn btn-sm" @click="extend(p)"><CalendarPlus/>{{ t('pkExtend') }}</button>
     <button v-if="p.state !== 'Ended'" class="btn btn-sm btn-ghost" @click="end(p)"><CalendarX2/>{{ t('pkEndEarly') }}</button>
    </PackageRow>
    <p v-if="p.note" class="note"><StickyNote/><span dir="auto">{{ p.note }}</span></p>
   </div>
  </div>

  <div v-if="member" class="foot">
   <button class="btn btn-primary btn-lg btn-block" @click="dialog='add'"><Plus/>{{ t('pkAdd') }}</button>
   <button class="btn btn-ghost btn-block" @click="emit('credits')"><Coins/>{{ t('pkAddRegular') }}</button>
  </div>

  <Teleport to="body">
   <PackageDialog v-if="dialog==='add'" :user="user" :packages="list || []" @close="dialog=null" @created="created"/>

   <Modal v-else-if="dialog==='extend'" :title="tf('pkExtendTitle', bidi(pkgName(target)))" :close-label="t('close')" @close="dialog=null">
    <form class="stack" @submit.prevent="doExtend">
     <div class="field">
      <label for="ext-days">{{ t('pkExtendBy') }}</label>
      <div class="cluster"><button v-for="d in PRESETS" :key="d" type="button" class="chip" :class="{ 'chip-on': Number(days) === d }" :aria-pressed="Number(days) === d" @click="days = d">{{ tf('pkDays', d) }}</button></div>
      <input id="ext-days" v-model="days" class="input num" type="number" min="1" :max="MAX_DAYS" required>
     </div>
     <p v-if="newEnd" class="notice"><CalendarPlus/><span>{{ tf(target.state === 'Ended' ? 'pkExtendBack' : 'pkExtendTo', date(newEnd.toISOString())) }}</span></p>
     <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
     <div class="dlg-actions"><button type="button" class="btn" @click="dialog=null">{{ t('cancel') }}</button><button class="btn btn-primary" :disabled="busy || !newEnd">{{ busy ? t('processing') : t('pkExtend') }}</button></div>
    </form>
   </Modal>

   <Modal v-else-if="dialog==='end'" :title="tf('pkEndTitle', bidi(pkgName(target)))" :close-label="t('close')" @close="dialog=null">
    <p>{{ t(target.state === 'Scheduled' ? 'pkEndScheduled' : 'pkEndConfirm') }}</p>
    <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
    <div class="dlg-actions"><button class="btn" @click="dialog=null">{{ t('cancel') }}</button><button class="btn btn-danger btn-primary" :disabled="busy" @click="doEnd">{{ busy ? t('processing') : t('pkEndYes') }}</button></div>
   </Modal>
  </Teleport>
 </div>
</template>

<style scoped>
.dp{display:flex;flex-direction:column;gap:14px;min-height:100%}
.quiet{display:flex;align-items:center;gap:10px;padding:12px 0;color:var(--muted);font-size:13.5px}
.quiet svg{color:var(--faint)}
.pkgs{display:flex;flex-direction:column}
.one{display:flex;flex-direction:column;gap:6px;padding:14px 0;border-top:1px solid var(--line)}
.one:first-child{border-top:0;padding-top:0}
.note{display:flex;gap:8px;align-items:flex-start;margin-inline-start:50px;font-size:12.5px;color:var(--faint)}
.note svg{width:14px;height:14px;margin-top:3px}
.foot{position:sticky;bottom:0;margin-top:auto;display:flex;flex-direction:column;gap:6px;padding-top:12px;background:linear-gradient(transparent,var(--s1) 18px)}
</style>
