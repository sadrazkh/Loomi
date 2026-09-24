<script setup>
import { ref, computed, onBeforeUnmount } from 'vue';
import { Trash2, Upload, WandSparkles, LoaderCircle, Check, ImageOff, FolderOpen } from 'lucide-vue-next';
import Modal from './Modal.vue';
import PhotoPicker from './PhotoPicker.vue';
import { sampleSrc } from './templates.js';
// template is the manage shape, or null for one not saved yet; it is refreshed from outside while a sample is being made, the form is not.
const props = defineProps({ template:Object, categories:Array, defaults:Object, t:Function, locale:String, api:Function, upload:Function, postForm:Function, action:Function, busy:Boolean, error:String });
const emit = defineEmits(['saved','deleted','generating','open','close']);
// The server's own rules, mirrored so Save is offered only for what it will accept.
const PROVIDERS = ['ChatGPT','Gemini'], MAX_PHOTOS = 4, MAX_WITH_SAMPLE = 3, MAX_TITLE = 80, MAX_PROMPT = 8000, MAX_INSTRUCTIONS = 400, MAX_SAMPLE_BYTES = 20 * 1024 * 1024;
const fields = x => ({ categoryId:x.categoryId ?? '', title:x.title ?? '', instructions:x.instructions ?? '', prompt:x.prompt ?? '', provider:x.provider ?? 'ChatGPT', minInputs:x.minInputs ?? 1, maxInputs:x.maxInputs ?? 1, sendSample:!!x.sendSample, isActive:x.isActive ?? true, order:x.order ?? 0 });
const form = ref(fields(props.template ?? { categoryId:props.defaults?.categoryId ?? props.categories?.[0]?.id, order:props.defaults?.order }));
const n = v => Number(v).toLocaleString(props.locale);
const limit = computed(() => form.value.sendSample ? MAX_WITH_SAMPLE : MAX_PHOTOS);
const problem = computed(() => {
 const { minInputs:min, maxInputs:max, sendSample } = form.value;
 if (!Number.isInteger(min) || !Number.isInteger(max) || min < 0 || max > MAX_PHOTOS) return 'photosBounds';
 if (min > max) return 'minOverMax';
 if (sendSample && max > MAX_WITH_SAMPLE) return 'sampleMaxPhotos';
 return '';
});
const body = computed(() => ({ ...form.value, title:form.value.title.trim(), instructions:form.value.instructions.trim(), prompt:form.value.prompt.trim() }));
const valid = computed(() => { const b = body.value; return !!b.categoryId && !!b.title && b.title.length <= MAX_TITLE && !!b.prompt && b.prompt.length <= MAX_PROMPT && b.instructions.length <= MAX_INSTRUCTIONS && Number.isInteger(b.order) && !problem.value; });
const dirty = computed(() => !props.template || JSON.stringify(fields(props.template)) !== JSON.stringify(form.value));
const justSaved = ref(false), confirming = ref(false);
async function save() {
 if (!valid.value || props.busy) return;
 let result = null;
 await props.action(async () => { result = props.template ? await props.api(`/api/templates/${props.template.id}`, 'PUT', body.value) : await props.api('/api/templates', 'POST', body.value); });
 if (!result) return;
 form.value = fields(result); justSaved.value = true; emit('saved', result);
}
async function remove() {
 const id = props.template?.id; if (!id) return;
 let done = false;
 await props.action(async () => { await props.api(`/api/templates/${id}`, 'DELETE'); done = true; });
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
 await props.action(async () => {
  if (file.size > MAX_SAMPLE_BYTES) throw new Error('FileTooLarge');
  const data = new FormData(); data.append('file', file);
  result = await props.postForm(`/api/templates/${id}/sample`, data);
 });
 if (result) emit('saved', result);
}
async function generate() {
 const id = props.template?.id; if (!id) return;
 let result = null;
 await props.action(async () => { result = await props.api(`/api/templates/${id}/sample/generate`, 'POST', { inputs:testPhotos.value.slice(0, testMax.value).map(p => ({ uploadId:p.id })) }); });
 if (!result) return;
 testPhotos.value = []; sampleProject.value = result.projectId; emit('generating', id);
}
onBeforeUnmount(() => { for (const p of testPhotos.value) props.api(`/api/uploads/${p.id}`, 'DELETE').catch(() => {}); });
</script>
<template>
 <Modal class="wide" :title="t(template ? 'editTemplate' : 'newTemplate')" :close-label="t('close')" @close="emit('close')">
  <form class="template-editor" @submit.prevent="save">
   <div class="field-grid">
    <div><label for="tpl-category">{{ t('category') }}</label><select id="tpl-category" v-model="form.categoryId" required><option v-for="c in categories" :key="c.id" :value="c.id">{{ c.name }}</option></select></div>
    <div><label for="tpl-title">{{ t('templateTitle') }}</label><input id="tpl-title" v-model="form.title" dir="auto" required :maxlength="MAX_TITLE"></div>
   </div>
   <label for="tpl-instructions">{{ t('templateInstructions') }}</label>
   <textarea id="tpl-instructions" v-model="form.instructions" dir="auto" rows="2" :maxlength="MAX_INSTRUCTIONS" :placeholder="t('instructionsPlaceholder')"></textarea>
   <label for="tpl-prompt">{{ t('templatePrompt') }}</label>
   <textarea id="tpl-prompt" v-model="form.prompt" class="prompt-field" dir="auto" rows="9" required :maxlength="MAX_PROMPT" spellcheck="false" aria-describedby="tpl-prompt-hint"></textarea>
   <p id="tpl-prompt-hint" class="field-hint split"><span>{{ t('templatePromptHint') }}</span><span class="count">{{ n(form.prompt.length) }} / {{ n(MAX_PROMPT) }}</span></p>
   <div class="field-grid four">
    <div><label for="tpl-provider">{{ t('provider') }}</label><select id="tpl-provider" v-model="form.provider"><option v-for="p in PROVIDERS" :key="p" :value="p">{{ p }}</option></select></div>
    <div><label for="tpl-min">{{ t('minPhotos') }}</label><input id="tpl-min" v-model.number="form.minInputs" type="number" min="0" :max="limit" step="1" required></div>
    <div><label for="tpl-max">{{ t('maxPhotos') }}</label><input id="tpl-max" v-model.number="form.maxInputs" type="number" min="0" :max="limit" step="1" required></div>
    <div><label for="tpl-order">{{ t('order') }}</label><input id="tpl-order" v-model.number="form.order" type="number" step="1" required></div>
   </div>
   <label class="check-row"><input v-model="form.sendSample" type="checkbox" aria-describedby="tpl-send-help"><span>{{ t('sendSample') }}</span></label>
   <p id="tpl-send-help" class="field-hint check-help">{{ t('sendSampleHelp') }}</p>
   <label class="check-row"><input v-model="form.isActive" type="checkbox"><span>{{ t('activeTemplate') }}</span></label>
   <p v-if="problem" class="error-text" role="status">{{ t(problem) }}</p>
   <section class="editor-section" aria-labelledby="tpl-sample-heading">
    <h3 id="tpl-sample-heading">{{ t('sample') }}</h3>
    <p v-if="!template" class="field-hint">{{ t('sampleSaveFirst') }}</p>
    <div v-else class="sample-box">
     <div class="sample-preview"><img v-if="template.sampleUrl" :src="sampleSrc(template)" :alt="t('sample')"><div v-else class="cover-empty"><ImageOff/><span>{{ t('noSample') }}</span></div><span v-if="state==='generating'" class="sample-busy"><LoaderCircle class="spin"/></span></div>
     <div class="sample-actions">
      <div class="account-actions"><button type="button" :disabled="busy" @click="sampleInput?.click()"><Upload/>{{ t('uploadSample') }}</button></div>
      <input ref="sampleInput" type="file" accept="image/png,image/jpeg,image/webp" hidden tabindex="-1" @change="uploadSample">
      <PhotoPicker v-if="testMax>0" id="tpl-test" v-model="testPhotos" :max="testMax" :label="t('testPhotos')" :hint="t('testPhotosHelp')" :t="t" :locale="locale" :api="api" :upload="upload" :action="action" :busy="busy"/>
      <div class="account-actions"><button type="button" :disabled="busy||dirty||state==='generating'" @click="generate"><WandSparkles/>{{ t('generateSample') }}</button><button v-if="sampleProject" type="button" @click="emit('open',{projectId:sampleProject,title:template.title})"><FolderOpen/>{{ t('openProject') }}</button></div>
      <p v-if="dirty" class="field-hint">{{ t('saveBeforeSample') }}</p>
      <p v-if="state==='generating'" class="dev-note" role="status"><LoaderCircle class="spin"/><span>{{ t('sampleGeneratingHelp') }}</span></p>
      <p v-else-if="state==='failed'" class="error-text" role="status">{{ t('sampleFailedHelp') }}</p>
     </div>
    </div>
   </section>
   <p v-if="justSaved&&!dirty" class="dev-note" role="status"><Check/><span>{{ t('saved') }}</span></p>
   <footer v-if="confirming" class="modal-actions confirm-actions"><p>{{ t('deleteTemplateConfirm') }}</p><button type="button" @click="confirming=false">{{ t('cancel') }}</button><button type="button" class="danger" :disabled="busy" @click="remove">{{ t('confirm') }}</button></footer>
   <footer v-else class="modal-actions"><button v-if="template" type="button" class="danger" :disabled="busy" @click="confirming=true"><Trash2/>{{ t('deleteTemplate') }}</button><span class="grow"/><button type="button" @click="emit('close')">{{ t('close') }}</button><button class="primary" :disabled="busy||!valid||!dirty">{{ busy?t('processing'):t('save') }}</button></footer>
  </form>
  <p v-if="error" role="alert" class="error-text">{{ t(error) }}</p>
 </Modal>
</template>
