<script setup>
import { onMounted, onBeforeUnmount, ref } from 'vue';
import { X } from 'lucide-vue-next';
// size: 'md' (default, 520px), 'lg' (720px) or 'full' (an image viewer). On a phone every dialog rises from the bottom as a sheet.
defineProps({ title: String, closeLabel: String, size: { type: String, default: 'md' } });
const emit = defineEmits(['close']);
const panel = ref();
let previous;
function key(e) {
 if (e.key === 'Escape') emit('close');
 if (e.key !== 'Tab') return;
 const list = [...panel.value.querySelectorAll('button:not(:disabled),input,textarea,a[href],select')];
 const first = list[0], last = list.at(-1);
 if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last?.focus(); }
 else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus(); }
}
onMounted(() => { previous = document.activeElement; document.body.style.overflow = 'hidden'; (panel.value.querySelector('[autofocus]') || panel.value.querySelector('input,textarea,select') || panel.value.querySelector('button'))?.focus(); document.addEventListener('keydown', key); });
onBeforeUnmount(() => { document.body.style.overflow = ''; document.removeEventListener('keydown', key); previous?.focus(); });
</script>
<template>
 <div class="dlg-backdrop" @mousedown.self="emit('close')">
  <section ref="panel" class="dlg" :class="'dlg-' + size" role="dialog" aria-modal="true" aria-labelledby="dlg-title">
   <header class="dlg-head"><h2 id="dlg-title" dir="auto">{{ title }}</h2><button class="btn btn-ghost btn-icon btn-sm" :aria-label="closeLabel" @click="emit('close')"><X/></button></header>
   <div class="dlg-body"><slot/></div>
  </section>
 </div>
</template>
<style scoped>
.dlg-backdrop{position:fixed;inset:0;z-index:50;background:var(--dim);display:grid;place-items:center;padding:24px;overflow-y:auto}
.dlg{width:min(520px,100%);max-height:calc(100vh - 48px);display:flex;flex-direction:column;background:var(--s1);border:1px solid var(--line);border-radius:var(--r3);box-shadow:var(--shadow);overflow:hidden}
.dlg-lg{width:min(720px,100%)}
.dlg-full{width:min(1100px,100%)}
.dlg-head{display:flex;align-items:center;gap:12px;padding:18px 20px 0}
.dlg-head h2{flex:1;min-width:0;font-size:18px}
.dlg-body{padding:16px 20px 20px;overflow-y:auto;display:flex;flex-direction:column;gap:14px}
@media (prefers-reduced-motion:no-preference){.dlg{animation:rise .18s ease-out}}
@keyframes rise{from{transform:translateY(8px);opacity:0}to{transform:none;opacity:1}}
@media (max-width:640px){
 .dlg-backdrop{place-items:end stretch;padding:0}
 .dlg{width:100%;max-height:92vh;border-radius:var(--r3) var(--r3) 0 0;padding-bottom:env(safe-area-inset-bottom,0px)}
}
</style>
<style>
/* Footer row of a dialog's own buttons; global because it lives in the slotted content. */
.dlg-actions{display:flex;flex-wrap:wrap;justify-content:flex-end;gap:8px;margin-top:4px}
</style>
