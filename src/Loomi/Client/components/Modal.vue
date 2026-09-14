<script setup>
import { onMounted, onBeforeUnmount, ref } from 'vue';
import { X } from 'lucide-vue-next';
defineProps({ title:String, closeLabel:String });
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
onMounted(() => { previous = document.activeElement; document.body.style.overflow = 'hidden'; panel.value.querySelector('input,textarea,button')?.focus(); document.addEventListener('keydown', key); });
onBeforeUnmount(() => { document.body.style.overflow = ''; document.removeEventListener('keydown', key); previous?.focus(); });
</script>
<template><div class="modal-backdrop" @mousedown.self="emit('close')"><section ref="panel" class="modal" role="dialog" aria-modal="true" aria-labelledby="modal-title"><header><h2 id="modal-title">{{ title }}</h2><button class="icon-button" :aria-label="closeLabel" @click="emit('close')"><X/></button></header><slot/></section></div></template>
