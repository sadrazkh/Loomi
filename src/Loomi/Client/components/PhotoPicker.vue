<script setup>
import { ref } from 'vue';
import { ImagePlus, X } from 'lucide-vue-next';
import { t, n, api, uploadFiles, action, busy } from '../core';
// Each photo is uploaded the moment it is picked, like the composer's attachments, so sending later only names ids.
const props = defineProps({ modelValue: Array, max: Number, id: String, label: String, hint: String });
const emit = defineEmits(['update:modelValue']);
const input = ref(null), over = ref(false);
async function add(list) {
 const files = [...list].filter(f => f.type.startsWith('image/')).slice(0, Math.max(props.max - props.modelValue.length, 0));
 if (!files.length) return;
 await action(async () => { const added = await uploadFiles(files); emit('update:modelValue', [...props.modelValue, ...added.map(u => ({ id: u.id, url: `/api/uploads/${u.id}` }))]); });
}
function pick(e) { const files = e.target.files; add(files).finally(() => { e.target.value = ''; }); }
function drop(e) { over.value = false; add(e.dataTransfer?.files || []); }
// Best effort, as in the composer: a file nobody sends is swept after a day anyway.
function remove(photo) { emit('update:modelValue', props.modelValue.filter(p => p.id !== photo.id)); api(`/api/uploads/${photo.id}`, 'DELETE').catch(() => {}); }
</script>
<template>
 <div class="picker" :class="{ over }" role="group" :aria-labelledby="`${id}-label`" :aria-describedby="hint ? `${id}-hint` : undefined" @dragover.prevent="over = modelValue.length < max" @dragleave="over = false" @drop.prevent="drop">
  <div class="head"><span :id="`${id}-label`" class="field-label">{{ label }}</span><span class="grow"/><span class="count num">{{ n(modelValue.length) }} / {{ n(max) }}</span></div>
  <p v-if="hint" :id="`${id}-hint`" class="field-help">{{ hint }}</p>
  <div class="photos">
   <span v-for="p in modelValue" :key="p.id" class="photo"><img :src="p.url" alt=""><button type="button" class="drop" :aria-label="t('stRemovePhoto')" :title="t('stRemovePhoto')" @click="remove(p)"><X/></button></span>
   <button v-if="modelValue.length < max" type="button" class="photo add" :disabled="busy" :aria-label="t('stAddPhotos')" :title="t('stAddPhotos')" @click="input?.click()"><ImagePlus/><span>{{ t('stAddPhoto') }}</span></button>
  </div>
  <input ref="input" type="file" accept="image/png,image/jpeg,image/webp" :multiple="max - modelValue.length > 1" hidden tabindex="-1" @change="pick">
 </div>
</template>
<style scoped>
.picker{display:flex;flex-direction:column;gap:6px;border-radius:var(--r);transition:background .15s}
.picker.over{background:var(--accent-soft);outline:2px dashed var(--accent-line);outline-offset:4px}
.head{display:flex;align-items:center;gap:8px}
.count{font-size:12px;color:var(--faint)}
.photos{display:flex;flex-wrap:wrap;gap:10px;margin-top:4px}
.photo{position:relative;width:84px;height:84px;border-radius:var(--r2);overflow:hidden;background:var(--s3);flex:none}
.photo img{width:100%;height:100%;object-fit:cover;display:block}
.drop{position:absolute;top:5px;inset-inline-end:5px;width:24px;height:24px;border-radius:50%;border:0;padding:0;display:grid;place-items:center;background:color-mix(in srgb,var(--bg) 78%,transparent);color:var(--text)}
.drop:hover{background:var(--bad);color:var(--accent-ink)}
.drop svg.lucide{width:14px;height:14px}
.add{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:4px;border:1px dashed var(--line2);background:var(--s2);color:var(--muted);font-size:12px}
.add:hover:not(:disabled){border-color:var(--accent-line);color:var(--text)}
.add svg.lucide{width:22px;height:22px}
</style>
