<script setup>
import { computed } from 'vue';
import { Download, MessagesSquare } from 'lucide-vue-next';
import Modal from '../Modal.vue';
import { t, current, go } from '../../core';
// One picture at full size, with its prompt and a download; from the gallery it also leads back to the chat it was made in.
const props = defineProps({ generation: Object, chatLink: Boolean });
const emit = defineEmits(['close']);
const title = computed(() => props.generation.projectTitle || current.value?.title || props.generation.templateTitle || t('stImage'));
function openChat() { const id = props.generation.projectId; emit('close'); go('chat', id); }
</script>
<template>
 <Teleport to="body"><Modal size="full" :title="title" :close-label="t('stClose')" @close="emit('close')">
  <div class="frame"><img :src="generation.imageUrl" :alt="generation.prompt || generation.templateTitle || ''"></div>
  <p v-if="generation.prompt" class="caption" dir="auto">{{ generation.prompt }}</p>
  <div class="dlg-actions">
   <button v-if="chatLink" type="button" class="btn" @click="openChat"><MessagesSquare/>{{ t('stOpenChat') }}</button>
   <a class="btn btn-primary" :href="generation.imageUrl" :download="`loomi-${generation.id}`"><Download/>{{ t('stDownload') }}</a>
  </div>
 </Modal></Teleport>
</template>
<style scoped>
.frame{display:grid;place-items:center;background:var(--bg);border-radius:var(--r);overflow:hidden;min-height:200px}
.frame img{display:block;max-width:100%;max-height:calc(100vh - 260px);object-fit:contain}
.caption{color:var(--muted);white-space:pre-wrap;overflow-wrap:anywhere;max-height:7.5em;overflow-y:auto}
@media (max-width:640px){.frame img{max-height:calc(92vh - 230px)}}
</style>
