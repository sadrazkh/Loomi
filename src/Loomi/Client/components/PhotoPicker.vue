<script setup>
import { ref } from 'vue';
import { ImagePlus, X } from 'lucide-vue-next';
// Each photo is uploaded the moment it is picked, like the composer's attachments, so sending later only names ids.
const props = defineProps({ modelValue:Array, max:Number, id:String, label:String, hint:String, t:Function, locale:String, api:Function, upload:Function, action:Function, busy:Boolean });
const emit = defineEmits(['update:modelValue']);
const input = ref(null);
const n = v => Number(v).toLocaleString(props.locale);
async function pick(e) {
 const files = [...e.target.files].slice(0, Math.max(props.max - props.modelValue.length, 0)); e.target.value = '';
 if (!files.length) return;
 await props.action(async () => { const added = await props.upload(files); emit('update:modelValue', [...props.modelValue, ...added.map(u => ({ id:u.id, url:`/api/uploads/${u.id}` }))]); });
}
// Best effort, as in the composer: a file nobody sends is swept after a day anyway.
function remove(photo) { emit('update:modelValue', props.modelValue.filter(p => p.id !== photo.id)); props.api(`/api/uploads/${photo.id}`, 'DELETE').catch(() => {}); }
</script>
<template>
 <div class="photo-picker" role="group" :aria-labelledby="`${id}-label`" :aria-describedby="hint ? `${id}-hint` : undefined">
  <div class="field-label"><span :id="`${id}-label`">{{ label }}</span><span class="grow"/><span class="count">{{ n(modelValue.length) }} / {{ n(max) }}</span></div>
  <p v-if="hint" :id="`${id}-hint`" class="field-hint">{{ hint }}</p>
  <div class="attachments picker-photos">
   <span v-for="p in modelValue" :key="p.id" class="attachment"><img :src="p.url" alt=""><button type="button" class="icon-button" :aria-label="t('removeAttachment')" @click="remove(p)"><X/></button></span>
   <button v-if="modelValue.length < max" type="button" class="attachment add-photo" :disabled="busy" :aria-label="t('addPhotos')" :title="t('addPhotos')" @click="input?.click()"><ImagePlus/></button>
  </div>
  <input ref="input" type="file" accept="image/png,image/jpeg,image/webp" :multiple="max - modelValue.length > 1" hidden tabindex="-1" @change="pick">
 </div>
</template>
