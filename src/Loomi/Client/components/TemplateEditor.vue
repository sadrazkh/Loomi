<script setup>
import { ref, computed, onBeforeUnmount } from 'vue';
import { Trash2, Upload, WandSparkles, LoaderCircle, Check, ImageOff, MessagesSquare, AlertTriangle } from 'lucide-vue-next';
import Modal from './Modal.vue';
import PhotoPicker from './PhotoPicker.vue';
import { sampleSrc } from './templates.js';
import { sized, THUMB } from '../images.js';
import { t, n, PROVIDERS, busy, api, action, postForm, openCreated } from '../core';
// template is the manage shape, or null for one not saved yet; it is refreshed from outside while a sample is being made, the form is not.
const props = defineProps({ template: Object, categories: Array, defaults: Object });
const emit = defineEmits(['saved', 'deleted', 'generating', 'close']);
// The server's own rules, mirrored so Save is offered only for what it will accept.
const MAX_PHOTOS = 4, MAX_WITH_SAMPLE = 3, MAX_TITLE = 80, MAX_PROMPT = 8000, MAX_INSTRUCTIONS = 400, MAX_SAMPLE_BYTES = 20 * 1024 * 1024;
const fields = x => ({ categoryId: x.categoryId ?? '', title: x.title ?? '', instructions: x.instructions ?? '', prompt: x.prompt ?? '', provider: x.provider ?? 'ChatGPT', minInputs: x.minInputs ?? 1, maxInputs: x.maxInputs ?? 1, sendSample: !!x.sendSample, isActive: x.isActive ?? true, order: x.order ?? 0 });
const form = ref(fields(props.template ?? { categoryId: props.defaults?.categoryId ?? props.categories?.[0]?.id, order: props.defaults?.order }));
const limit = computed(() => form.value.sendSample ? MAX_WITH_SAMPLE : MAX_PHOTOS);
const problem = computed(() => {
 const { minInputs: min, maxInputs: max, sendSample } = form.value;
 if (!Number.isInteger(min) || !Number.isInteger(max) || min < 0 || max > MAX_PHOTOS) return 'stPhotosBounds';
 if (min > max) return 'stMinOverMax';
 if (sendSample && max > MAX_WITH_SAMPLE) return 'stSampleMaxPhotos';
 return '';
});
const body = computed(() => ({ ...form.value, title: form.value.title.trim(), instructions: form.value.instructions.trim(), prompt: form.value.prompt.trim() }));
const valid = computed(() => { const b = body.value; return !!b.categoryId && !!b.title && b.title.length <= MAX_TITLE && !!b.prompt && b.prompt.length <= MAX_PROMPT && b.instructions.length <= MAX_INSTRUCTIONS && Number.isInteger(b.order) && !problem.value; });
const dirty = computed(() => !props.template || JSON.stringify(fields(props.template)) !== JSON.stringify(form.value));
const justSaved = ref(false), confirming = ref(false);
async function save() {
 if (!valid.value || busy.value) return;
 let result = null;
 await action(async () => { result = props.template ? await api(`/api/templates/${props.template.id}`, 'PUT', body.value) : await api('/api/templates', 'POST', body.value); });
 if (!result) return;
 form.value = fields(result); justSaved.value = true; emit('saved', result);
}
async function remove() {
 const id = props.template?.id; if (!id) return;
 let done = false;
 await action(async () => { await api(`/api/templates/${id}`, 'DELETE'); done = true; });
 if (done) emit('deleted', id);
}
const sampleInput = ref(null), testPhotos = ref([]), sampleProject = ref(null);
const state = computed(() => props.template?.sampleState ?? 'none');
// Test photos stand in for what people will send, so they follow the saved template's own ceiling.
const testMax = computed(() => Math.min(props.template?.maxInputs ?? 0, MAX_PHOTOS));
async function uploadSample(e) {
 const file = e.target.files[0]; e.target.value = '';
 const id = props.template?.id; if (!file || !id) return;
 let result = null;
 await action(async () => {
  if (file.size > MAX_SAMPLE_BYTES) throw new Error('FileTooLarge');
  const data = new FormData(); data.append('file', file);
  result = await postForm(`/api/templates/${id}/sample`, data);
 });
 if (result) emit('saved', result);
}
async function generate() {
 const id = props.template?.id; if (!id) return;
 let result = null;
 await action(async () => { result = await api(`/api/templates/${id}/sample/generate`, 'POST', { inputs: testPhotos.value.slice(0, testMax.value).map(p => ({ uploadId: p.id })) }); });
 if (!result) return;
 testPhotos.value = []; sampleProject.value = result.projectId; emit('generating', id);
}
// The sample is made in a chat of the owner's own, which can be opened to watch it or to try again by hand.
function openSampleChat() { const projectId = sampleProject.value; emit('close'); openCreated({ projectId }); }
onBeforeUnmount(() => { for (const p of testPhotos.value) api(`/api/uploads/${p.id}`, 'DELETE').catch(() => {}); });
</script>
<template>
 <Teleport to="body"><Modal size="lg" :title="t(template ? 'stEditTemplate' : 'stNewTemplate')" :close-label="t('stClose')" @close="emit('close')">
  <form class="editor" @submit.prevent="save">
   <div class="two">
    <div class="field"><label for="tpl-category">{{ t('stCategory') }}</label><select id="tpl-category" v-model="form.categoryId" class="input" required><option v-for="c in categories" :key="c.id" :value="c.id">{{ c.name }}</option></select></div>
    <div class="field"><label for="tpl-title">{{ t('stTemplateName') }}</label><input id="tpl-title" v-model="form.title" class="input" dir="auto" required :maxlength="MAX_TITLE"></div>
   </div>
   <div class="field"><label for="tpl-instructions">{{ t('stInstructions') }}</label><textarea id="tpl-instructions" v-model="form.instructions" class="input" dir="auto" rows="2" :maxlength="MAX_INSTRUCTIONS" :placeholder="t('stInstructionsPlaceholder')"></textarea></div>
   <div class="field">
    <label for="tpl-prompt">{{ t('stPrompt') }}</label>
    <textarea id="tpl-prompt" v-model="form.prompt" class="input prompt" dir="auto" rows="8" required :maxlength="MAX_PROMPT" spellcheck="false" aria-describedby="tpl-prompt-hint"></textarea>
    <p id="tpl-prompt-hint" class="field-help split"><span>{{ t('stPromptHint') }}</span><span class="num">{{ n(form.prompt.length) }} / {{ n(MAX_PROMPT) }}</span></p>
   </div>
   <div class="four">
    <div class="field"><label for="tpl-provider">{{ t('stProvider') }}</label><select id="tpl-provider" v-model="form.provider" class="input"><option v-for="p in PROVIDERS" :key="p" :value="p">{{ p }}</option></select></div>
    <div class="field"><label for="tpl-min">{{ t('stMinPhotos') }}</label><input id="tpl-min" v-model.number="form.minInputs" class="input num" type="number" min="0" :max="limit" step="1" required></div>
    <div class="field"><label for="tpl-max">{{ t('stMaxPhotos') }}</label><input id="tpl-max" v-model.number="form.maxInputs" class="input num" type="number" min="0" :max="limit" step="1" required></div>
    <div class="field"><label for="tpl-order">{{ t('stOrder') }}</label><input id="tpl-order" v-model.number="form.order" class="input num" type="number" step="1" required></div>
   </div>
   <div class="checks">
    <label class="check"><input v-model="form.sendSample" type="checkbox" aria-describedby="tpl-send-help"><span>{{ t('stSendSample') }}</span></label>
    <p id="tpl-send-help" class="field-help indent">{{ t('stSendSampleHelp') }}</p>
    <label class="check"><input v-model="form.isActive" type="checkbox"><span>{{ t('stActiveTemplate') }}</span></label>
   </div>
   <p v-if="problem" class="field-error" role="status">{{ t(problem) }}</p>

   <section class="sample" aria-labelledby="tpl-sample-heading">
    <h3 id="tpl-sample-heading">{{ t('stSample') }}</h3>
    <p v-if="!template" class="field-help">{{ t('stSampleSaveFirst') }}</p>
    <div v-else class="sample-box">
     <div class="preview">
      <img v-if="template.sampleUrl" :src="sized(sampleSrc(template), THUMB)" :alt="t('stSample')">
      <span v-else class="empty"><ImageOff/><small>{{ t('stNoSample') }}</small></span>
      <span v-if="state === 'generating'" class="busy-veil"><LoaderCircle class="spin"/></span>
     </div>
     <div class="sample-side">
      <div class="cluster"><button type="button" class="btn btn-sm" :disabled="busy" @click="sampleInput?.click()"><Upload/>{{ t('stUploadSample') }}</button></div>
      <input ref="sampleInput" type="file" accept="image/png,image/jpeg,image/webp" hidden tabindex="-1" @change="uploadSample">
      <PhotoPicker v-if="testMax > 0" id="tpl-test" v-model="testPhotos" :max="testMax" :label="t('stTestPhotos')" :hint="t('stTestPhotosHelp')"/>
      <div class="cluster">
       <button type="button" class="btn btn-sm" :disabled="busy || dirty || state === 'generating'" @click="generate"><WandSparkles/>{{ t('stGenerateSample') }}</button>
       <button v-if="sampleProject" type="button" class="btn btn-ghost btn-sm" @click="openSampleChat"><MessagesSquare/>{{ t('stOpenChat') }}</button>
      </div>
      <p v-if="dirty" class="field-help">{{ t('stSaveBeforeSample') }}</p>
      <p v-if="state === 'generating'" class="notice" role="status"><LoaderCircle class="spin"/><span>{{ t('stSampleGeneratingHelp') }}</span></p>
      <p v-else-if="state === 'failed'" class="notice notice-bad" role="status"><AlertTriangle/><span>{{ t('stSampleFailedHelp') }}</span></p>
     </div>
    </div>
   </section>

   <p v-if="justSaved && !dirty" class="notice notice-ok" role="status"><Check/><span>{{ t('stSaved') }}</span></p>
   <div v-if="confirming" class="dlg-actions confirm"><p>{{ t('stDeleteTemplateBody') }}</p><button type="button" class="btn" @click="confirming = false">{{ t('stCancel') }}</button><button type="button" class="btn btn-primary btn-danger" :disabled="busy" @click="remove">{{ t('stDeleteTemplate') }}</button></div>
   <div v-else class="dlg-actions">
    <button v-if="template" type="button" class="btn btn-danger" :disabled="busy" @click="confirming = true"><Trash2/>{{ t('stDeleteTemplate') }}</button>
    <span class="grow"/>
    <button type="button" class="btn" @click="emit('close')">{{ t('stClose') }}</button>
    <button class="btn btn-primary" :disabled="busy || !valid || !dirty">{{ busy ? t('stWorking') : t('stSave') }}</button>
   </div>
  </form>
 </Modal></Teleport>
</template>
<style scoped>
.editor{display:flex;flex-direction:column;gap:16px}
.two{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1.6fr);gap:14px}
.four{display:grid;grid-template-columns:minmax(0,1.3fr) repeat(3,minmax(0,1fr));gap:14px}
.prompt{font-size:13.5px;line-height:1.8}
.split{display:flex;justify-content:space-between;gap:12px}
.checks{display:flex;flex-direction:column;gap:8px}
.indent{padding-inline-start:28px;margin-top:-4px}
.sample{display:flex;flex-direction:column;gap:10px;padding-top:16px;border-top:1px solid var(--line)}
.sample h3{font-size:15px}
.sample-box{display:grid;grid-template-columns:160px minmax(0,1fr);gap:18px;align-items:start}
.preview{position:relative;aspect-ratio:4/5;border-radius:var(--r);overflow:hidden;background:var(--s3);display:grid;place-items:center;color:var(--faint)}
.preview img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover}
.empty{display:flex;flex-direction:column;align-items:center;gap:6px;font-size:12px}
.empty svg.lucide{width:26px;height:26px}
.busy-veil{position:absolute;inset:0;display:grid;place-items:center;background:color-mix(in srgb,var(--bg) 55%,transparent);color:var(--accent)}
.busy-veil svg.lucide{width:28px;height:28px}
.sample-side{display:flex;flex-direction:column;gap:12px;min-width:0}
.confirm{align-items:center}
.confirm p{flex:1 1 220px;color:var(--muted);font-size:13.5px}
@media (max-width:640px){
 .two{grid-template-columns:1fr}
 .four{grid-template-columns:1fr 1fr}
 .sample-box{grid-template-columns:110px minmax(0,1fr);gap:12px}
}
</style>
