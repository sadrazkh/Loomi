<script setup>
import { AlertTriangle } from 'lucide-vue-next';
import Modal from '../Modal.vue';
import { t, busy } from '../../core';
// A yes-or-no question before something that cannot be undone. note is an extra warning under the question, for the owner's wider reach.
defineProps({ title: String, body: String, note: String, confirmLabel: String, danger: { type: Boolean, default: true } });
const emit = defineEmits(['confirm', 'close']);
</script>
<template>
 <Teleport to="body"><Modal :title="title" :close-label="t('stClose')" @close="emit('close')">
  <slot/>
  <p class="body">{{ body }}</p>
  <p v-if="note" class="notice notice-warn"><AlertTriangle/><span>{{ note }}</span></p>
  <div class="dlg-actions">
   <button type="button" class="btn" @click="emit('close')">{{ t('stCancel') }}</button>
   <button type="button" class="btn btn-primary" :class="{ 'btn-danger': danger }" :disabled="busy" @click="emit('confirm')">{{ confirmLabel || t('stConfirm') }}</button>
  </div>
 </Modal></Teleport>
</template>
<style scoped>
.body{color:var(--muted);line-height:1.8}
</style>
