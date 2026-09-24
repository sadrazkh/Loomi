<script setup>
import { ref, computed, watch, onMounted, onBeforeUnmount } from 'vue';
import { LayoutTemplate, SlidersHorizontal, Plus, Trash2, EyeOff, ImageOff, LoaderCircle, AlertTriangle, Layers, WandSparkles } from 'lucide-vue-next';
import Modal from './Modal.vue';
import PhotoPicker from './PhotoPicker.vue';
import TemplateEditor from './TemplateEditor.vue';
import { photoHint, photoSpan, sampleSrc } from './templates.js';
// Busy and error belong to the shell: every call goes through its action(), so failures surface in the same toast as the rest of the app.
const props = defineProps({ t:Function, locale:String, isOwner:Boolean, api:Function, upload:Function, postForm:Function, action:Function, busy:Boolean, error:String, credits:Number });
const emit = defineEmits(['open']);
const n = v => Number(v).toLocaleString(props.locale);
const catalog = ref([]), loaded = ref(false), filter = ref(null), manageOn = ref(false);
// The role is re-read every few seconds, so someone who stops being owner mid-session drops out of manage mode with it.
const managing = computed(() => manageOn.value && props.isOwner);
const manage = ref({ categories:[], templates:[] });
const dialog = ref(null), chosen = ref(null), photos = ref([]), note = ref('');
const editingId = ref(null), editorDefaults = ref({});
const categoryTarget = ref(null), categoryName = ref(''), categoryOrder = ref('');
const byOrder = (a, b) => a.order - b.order || String(a.name ?? a.title).localeCompare(String(b.name ?? b.title), props.locale);
async function loadCatalog() { catalog.value = await props.api('/api/templates'); }
async function loadManage() { const m = await props.api('/api/templates/manage'); manage.value = { categories:[...m.categories].sort(byOrder), templates:m.templates }; }
onMounted(async () => { await props.action(loadCatalog); loaded.value = true; });
async function toggleManage() { manageOn.value = !manageOn.value; if (managing.value) await props.action(loadManage); }
// The public list arrives in order and holds only what people may use; the owner's list holds everything, empty categories included.
const publicSections = computed(() => catalog.value.filter(c => c.templates?.length));
const manageSections = computed(() => manage.value.categories.map(c => ({ ...c, templates:manage.value.templates.filter(x => x.categoryId === c.id).sort(byOrder) })));
const sections = computed(() => managing.value ? manageSections.value : publicSections.value);
const current = computed(() => sections.value.some(s => s.id === filter.value) ? filter.value : null);
const shown = computed(() => current.value ? sections.value.filter(s => s.id === current.value) : sections.value);
const total = computed(() => sections.value.reduce((sum, s) => sum + s.templates.length, 0));
// A sample is made in the background; the list is asked again only while one is, and the public list once it lands.
const generating = computed(() => manage.value.templates.some(x => x.sampleState === 'generating'));
let poll = null;
watch(generating, (on, was) => { clearInterval(poll); poll = null; if (on) poll = setInterval(() => loadManage().catch(() => {}), 5000); else if (was) loadCatalog().catch(() => {}); });
function discard(list) { for (const p of list) props.api(`/api/uploads/${p.id}`, 'DELETE').catch(() => {}); }
onBeforeUnmount(() => { clearInterval(poll); discard(photos.value); });
function choose(tpl) { chosen.value = tpl; photos.value = []; note.value = ''; dialog.value = 'use'; }
function close() { if (dialog.value === 'use') { discard(photos.value); photos.value = []; } dialog.value = null; }
const hint = computed(() => chosen.value ? photoHint(chosen.value.minInputs, chosen.value.maxInputs, props.t, props.locale) : '');
const complete = computed(() => !!chosen.value && photos.value.length >= chosen.value.minInputs && photos.value.length <= chosen.value.maxInputs);
const priceLine = computed(() => {
 const price = chosen.value?.price; if (price == null) return '';
 const line = props.t('templatePrice').replace('{n}', n(price));
 return props.credits != null ? `${line} · ${props.t('creditsLeft')}: ${n(props.credits)}` : line;
});
async function make() {
 const tpl = chosen.value; if (!complete.value || props.busy) return;
 let created = null;
 await props.action(async () => { created = await props.api(`/api/templates/${tpl.id}/use`, 'POST', { note:note.value.trim() || null, inputs:photos.value.map(p => ({ uploadId:p.id })) }); });
 if (!created) return;
 // The photos now belong to the generation; dropping them from here keeps close() from deleting them.
 photos.value = []; dialog.value = null;
 emit('open', { projectId:created.projectId, title:tpl.title });
}
const editing = computed(() => editingId.value ? manage.value.templates.find(x => x.id === editingId.value) ?? null : null);
function edit(tpl) { editingId.value = tpl.id; dialog.value = 'editor'; }
function create(categoryId) {
 const id = categoryId ?? current.value ?? manage.value.categories[0]?.id;
 editorDefaults.value = { categoryId:id, order:manage.value.templates.filter(x => x.categoryId === id).reduce((m, x) => Math.max(m, x.order + 1), 0) };
 editingId.value = null; dialog.value = 'editor';
}
function saved(tpl) { const list = manage.value.templates, i = list.findIndex(x => x.id === tpl.id); if (i >= 0) list[i] = tpl; else list.push(tpl); editingId.value = tpl.id; loadCatalog().catch(() => {}); }
function deleted(id) { manage.value.templates = manage.value.templates.filter(x => x.id !== id); dialog.value = null; editingId.value = null; loadCatalog().catch(() => {}); }
function sampleStarted(id) { const tpl = manage.value.templates.find(x => x.id === id); if (tpl) tpl.sampleState = 'generating'; loadManage().catch(() => {}); }
function openProject(target) { dialog.value = null; emit('open', target); }
const nextOrder = computed(() => manage.value.categories.reduce((m, c) => Math.max(m, c.order + 1), 0));
const countIn = c => manage.value.templates.filter(x => x.categoryId === c.id).length;
async function addCategory() {
 const name = categoryName.value.trim(); if (!name || props.busy) return;
 const order = categoryOrder.value === '' ? nextOrder.value : Number(categoryOrder.value);
 await props.action(async () => { await props.api('/api/templates/categories', 'POST', { name, order }); categoryName.value = ''; categoryOrder.value = ''; await loadManage(); });
}
// Saved as soon as the box is left, like a user's quota in Settings; whatever the server kept is put back in the box either way.
async function updateCategory(c, field, e) {
 const box = e.target, raw = box.value, next = { name:c.name, order:c.order };
 next[field] = field === 'order' ? Number(raw) : raw.trim();
 const fine = field === 'order' ? raw !== '' && Number.isInteger(next.order) : next.name.length >= 1 && next.name.length <= 64;
 if (fine && next[field] !== c[field]) await props.action(async () => {
  const updated = await props.api(`/api/templates/categories/${c.id}`, 'PUT', next);
  const list = manage.value.categories, i = list.findIndex(x => x.id === c.id); if (i >= 0) list[i] = updated; list.sort(byOrder);
  loadCatalog().catch(() => {});
 });
 box.value = (manage.value.categories.find(x => x.id === c.id) ?? c)[field];
}
function askDelete(c) { categoryTarget.value = c; dialog.value = 'deleteCategory'; }
async function deleteCategory() { const c = categoryTarget.value; await props.action(async () => { await props.api(`/api/templates/categories/${c.id}`, 'DELETE'); dialog.value = null; await loadManage(); loadCatalog().catch(() => {}); }); }
</script>
<template>
 <div class="page templates-page">
  <div class="page-heading"><div><div class="eyebrow">{{ t('templates') }} / {{ String(total).padStart(2,'0') }}</div><h1>{{ t('templatesTitle') }}</h1><p>{{ t('templatesIntro') }}</p></div><button v-if="isOwner" :class="{primary:managing}" :aria-pressed="managing" @click="toggleManage"><SlidersHorizontal/>{{ t('manageTemplates') }}</button></div>
  <template v-if="managing">
   <section class="settings-panel"><h2>{{ t('categories') }}</h2><p>{{ t('categoriesHelp') }}</p>
    <div v-if="!manage.categories.length" class="accounts-empty"><LayoutTemplate/><span>{{ t('noCategories') }}</span></div>
    <div v-for="c in manage.categories" :key="c.id" class="account-row category-row"><input class="category-name" :value="c.name" dir="auto" maxlength="64" :aria-label="t('categoryName')" :disabled="busy" @change="updateCategory(c,'name',$event)"><small class="count">{{ t('templateCount').replace('{n}', n(countIn(c))) }}</small><label class="account-cap">{{ t('order') }}<input type="number" step="1" :value="c.order" :disabled="busy" @change="updateCategory(c,'order',$event)"></label><button class="icon-button danger" :aria-label="t('deleteCategory')" :title="t('deleteCategory')" :disabled="busy" @click="askDelete(c)"><Trash2/></button></div>
    <form class="accounts-footer category-row" @submit.prevent="addCategory"><input v-model="categoryName" class="category-name" dir="auto" maxlength="64" :placeholder="t('categoryName')" :aria-label="t('categoryName')"><label class="account-cap">{{ t('order') }}<input v-model="categoryOrder" type="number" step="1" :placeholder="String(nextOrder)"></label><button class="primary" :disabled="busy||!categoryName.trim()"><Plus/>{{ t('addCategory') }}</button></form>
   </section>
   <div class="toolbar"><span class="section-label">{{ t('allTemplates') }} <span class="muted">/ {{ manage.templates.length }}</span></span><span class="grow"/><small v-if="!manage.categories.length" class="muted">{{ t('needCategory') }}</small><button class="primary" :disabled="busy||!manage.categories.length" @click="create()"><Plus/>{{ t('newTemplate') }}</button></div>
  </template>
  <div v-if="sections.length>1" class="chips" role="group" :aria-label="t('categories')"><button :class="{active:!current}" :aria-pressed="!current" @click="filter=null">{{ t('allCategories') }}</button><button v-for="s in sections" :key="s.id" :class="{active:current===s.id}" :aria-pressed="current===s.id" dir="auto" @click="filter=s.id">{{ s.name }}</button></div>
  <div v-if="!managing&&loaded&&!total" class="empty-state"><LayoutTemplate/><h2>{{ t('noTemplates') }}</h2><p>{{ isOwner?t('noTemplatesOwner'):t('noTemplatesBody') }}</p><button v-if="isOwner" class="primary" @click="toggleManage"><SlidersHorizontal/>{{ t('manageTemplates') }}</button></div>
  <section v-for="s in shown" :key="s.id" class="template-section" :aria-labelledby="`category-${s.id}`">
   <h2 :id="`category-${s.id}`" class="section-label"><bdi>{{ s.name }}</bdi></h2>
   <div class="template-grid">
    <template v-if="managing">
     <button v-for="tpl in s.templates" :key="tpl.id" class="project-card template-card" :class="{off:!tpl.isActive}" @click="edit(tpl)"><div class="template-cover"><img v-if="tpl.sampleUrl" :src="sampleSrc(tpl)" alt="" loading="lazy"><div v-else class="cover-empty"><ImageOff/></div><span class="badges"><span v-if="!tpl.isActive" class="badge"><EyeOff/>{{ t('inactive') }}</span><span v-if="!tpl.sampleUrl&&tpl.sampleState!=='generating'" class="badge warn"><ImageOff/>{{ t('noSample') }}</span><span v-if="tpl.sampleState==='generating'" class="badge warn"><LoaderCircle class="spin"/>{{ t('sampleGenerating') }}</span><span v-else-if="tpl.sampleState==='failed'" class="badge bad"><AlertTriangle/>{{ t('sampleFailed') }}</span></span></div><div class="template-card-body"><h3 dir="auto">{{ tpl.title }}</h3><p class="template-meta">{{ tpl.provider }} <span>·</span> {{ t('photosMeta').replace('{n}', photoSpan(tpl.minInputs, tpl.maxInputs, locale)) }} <span>·</span> {{ t('order') }}: {{ n(tpl.order) }}</p></div></button>
     <button class="add-project template-add" :disabled="busy" @click="create(s.id)"><Plus/><span>{{ t('newTemplate') }}</span></button>
    </template>
    <template v-else>
     <button v-for="tpl in s.templates" :key="tpl.id" class="project-card template-card" @click="choose(tpl)"><div class="template-cover"><img :src="tpl.sampleUrl" alt="" loading="lazy"></div><div class="template-card-body"><h3 dir="auto">{{ tpl.title }}</h3><p v-if="tpl.instructions" dir="auto">{{ tpl.instructions }}</p></div></button>
    </template>
   </div>
  </section>
  <Modal v-if="dialog==='use'&&chosen" class="wide" :title="chosen.title" :close-label="t('close')" @close="close">
   <form class="template-use" @submit.prevent="make">
    <img class="template-sample" :src="chosen.sampleUrl" :alt="chosen.title">
    <div class="template-use-body">
     <p v-if="chosen.instructions" class="template-instructions" dir="auto">{{ chosen.instructions }}</p>
     <PhotoPicker v-if="chosen.maxInputs>0" id="template-photos" v-model="photos" :max="chosen.maxInputs" :label="t('yourPhotos')" :hint="hint" :t="t" :locale="locale" :api="api" :upload="upload" :action="action" :busy="busy"/>
     <label for="template-note">{{ t('note') }}</label>
     <textarea id="template-note" v-model="note" dir="auto" rows="3" maxlength="1000" :placeholder="t('templateNotePlaceholder')"></textarea>
     <p v-if="priceLine" class="dev-note"><Layers/><span>{{ priceLine }}</span></p>
    </div>
    <footer class="modal-actions"><button type="button" @click="close">{{ t('cancel') }}</button><button class="primary" :disabled="busy||!complete"><WandSparkles/>{{ busy?t('processing'):t('makeIt') }}</button></footer>
   </form>
   <p v-if="error" role="alert" class="error-text">{{ t(error) }}</p>
  </Modal>
  <Modal v-else-if="dialog==='deleteCategory'" :title="t('deleteCategory')" :close-label="t('close')" @close="dialog=null">
   <p dir="auto"><strong>{{ categoryTarget?.name }}</strong></p><p>{{ t('deleteCategoryConfirm') }}</p>
   <footer class="modal-actions"><button @click="dialog=null">{{ t('cancel') }}</button><button class="danger" :disabled="busy" @click="deleteCategory">{{ t('confirm') }}</button></footer>
   <p v-if="error" role="alert" class="error-text">{{ t(error) }}</p>
  </Modal>
  <TemplateEditor v-else-if="dialog==='editor'" :template="editing" :categories="manage.categories" :defaults="editorDefaults" :t="t" :locale="locale" :api="api" :upload="upload" :post-form="postForm" :action="action" :busy="busy" :error="error" @saved="saved" @deleted="deleted" @generating="sampleStarted" @open="openProject" @close="dialog=null"/>
 </div>
</template>
