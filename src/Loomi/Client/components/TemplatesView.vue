<script setup>
import { ref, computed, watch, onMounted, onBeforeUnmount } from 'vue';
import { LayoutTemplate, SlidersHorizontal, Plus, Trash2, EyeOff, ImageOff, LoaderCircle, AlertTriangle, WandSparkles, Coins } from 'lucide-vue-next';
import Modal from './Modal.vue';
import PhotoPicker from './PhotoPicker.vue';
import TemplateEditor from './TemplateEditor.vue';
import ConfirmDialog from './studio/ConfirmDialog.vue';
import { payLine } from './studio/shared.js';
import { photoHint, photoMeta, sampleSrc, flatten } from './templates.js';
import { sized, CARD, VIEW } from '../images.js';
import { t, tf, n, locale, isOwner, busy, error, api, action, openCreated, go } from '../core';
// Two faces of one list. Without manage, the catalog people choose from: categories, cards, and the dialog that makes one with their photos.
// With manage (the management page), the owner's side only: categories, every template active or not, the editor and the samples.
// Every call goes through core's action(), so failures surface in the same toast as the rest of the app.
const props = defineProps({ manage: Boolean });
const byOrder = (a, b) => a.order - b.order || String(a.name ?? a.title).localeCompare(String(b.name ?? b.title), locale.value);

// ---- The catalog ----
const catalog = ref([]), loaded = ref(false), filter = ref(null);
const chosen = ref(null), photos = ref([]), note = ref('');
async function loadCatalog() { catalog.value = await api('/api/templates'); }
// The public list arrives in order and holds only what people may use.
const publicSections = computed(() => catalog.value.filter(c => c.templates?.length));

// ---- The owner's side ----
const admin = ref({ categories: [], templates: [] }), manageLoaded = ref(false);
const dialog = ref(null), editingId = ref(null), editorDefaults = ref({});
const categoryTarget = ref(null), categoryName = ref(''), categoryOrder = ref('');
async function loadManage() { const m = await api('/api/templates/manage'); admin.value = { categories: [...m.categories].sort(byOrder), templates: m.templates }; }
// The owner's list holds everything, empty categories included.
const manageSections = computed(() => admin.value.categories.map(c => ({ ...c, templates: admin.value.templates.filter(x => x.categoryId === c.id).sort(byOrder) })));

const sections = computed(() => props.manage ? manageSections.value : publicSections.value);
const currentFilter = computed(() => sections.value.some(s => s.id === filter.value) ? filter.value : null);
const shown = computed(() => currentFilter.value ? sections.value.filter(s => s.id === currentFilter.value) : sections.value);
const total = computed(() => sections.value.reduce((sum, s) => sum + s.templates.length, 0));

// A link from the home page names a template in the address (#/templates/<id>); its dialog opens as the list arrives.
// The address is then put back, so a reload shows the list rather than the dialog again.
function openFromAddress() {
 const [page, id] = location.hash.replace(/^#\/?/, '').split('/');
 if (props.manage || page !== 'templates' || !id || !loaded.value) return;
 const tpl = flatten(catalog.value).find(x => x.id === id);
 history.replaceState(null, '', '#/templates');
 if (tpl) choose(tpl);
}
// Not through action(): the list must load even while something else is busy, or the page would claim there are no templates.
onMounted(async () => {
 const load = props.manage ? loadManage : loadCatalog;
 try { await load(); } catch (e) { error.value = e.message; }
 if (props.manage) { manageLoaded.value = true; return; }
 loaded.value = true; openFromAddress();
 window.addEventListener('hashchange', openFromAddress);
});

// A sample is made in the background; the list is asked again only while one is.
const generating = computed(() => admin.value.templates.some(x => x.sampleState === 'generating'));
let poll = null;
watch(generating, on => { clearInterval(poll); poll = on ? setInterval(() => loadManage().catch(() => {}), 5000) : null; });
function discard(list) { for (const p of list) api(`/api/uploads/${p.id}`, 'DELETE').catch(() => {}); }
onBeforeUnmount(() => { clearInterval(poll); discard(photos.value); window.removeEventListener('hashchange', openFromAddress); });

// ---- Using one ----
function choose(tpl) { discard(photos.value); chosen.value = tpl; photos.value = []; note.value = ''; }
function closeUse() { discard(photos.value); photos.value = []; chosen.value = null; }
const hint = computed(() => chosen.value ? photoHint(chosen.value.minInputs, chosen.value.maxInputs) : '');
const complete = computed(() => !!chosen.value && photos.value.length >= chosen.value.minInputs && photos.value.length <= chosen.value.maxInputs);
const pay = computed(() => chosen.value ? payLine(chosen.value.price ?? null) : null);
async function make() {
 const tpl = chosen.value; if (!complete.value || busy.value) return;
 let created = null;
 await action(async () => { created = await api(`/api/templates/${tpl.id}/use`, 'POST', { note: note.value.trim() || null, inputs: photos.value.map(p => ({ uploadId: p.id })) }); });
 if (!created) return;
 // The photos now belong to the request; letting go of them here keeps closeUse() from deleting them.
 photos.value = []; chosen.value = null;
 await openCreated({ projectId: created.projectId });
}

// ---- Managing ----
const editing = computed(() => editingId.value ? admin.value.templates.find(x => x.id === editingId.value) ?? null : null);
function edit(tpl) { editingId.value = tpl.id; dialog.value = 'editor'; }
function create(categoryId) {
 const id = categoryId ?? currentFilter.value ?? admin.value.categories[0]?.id;
 editorDefaults.value = { categoryId: id, order: admin.value.templates.filter(x => x.categoryId === id).reduce((m, x) => Math.max(m, x.order + 1), 0) };
 editingId.value = null; dialog.value = 'editor';
}
function saved(tpl) { const list = admin.value.templates, i = list.findIndex(x => x.id === tpl.id); if (i >= 0) list[i] = tpl; else list.push(tpl); editingId.value = tpl.id; }
function deleted(id) { admin.value.templates = admin.value.templates.filter(x => x.id !== id); dialog.value = null; editingId.value = null; }
function sampleStarted(id) { const tpl = admin.value.templates.find(x => x.id === id); if (tpl) tpl.sampleState = 'generating'; loadManage().catch(() => {}); }
const nextOrder = computed(() => admin.value.categories.reduce((m, c) => Math.max(m, c.order + 1), 0));
const countIn = c => admin.value.templates.filter(x => x.categoryId === c.id).length;
async function addCategory() {
 const name = categoryName.value.trim(); if (!name || busy.value) return;
 const order = categoryOrder.value === '' ? nextOrder.value : Number(categoryOrder.value);
 await action(async () => { await api('/api/templates/categories', 'POST', { name, order }); categoryName.value = ''; categoryOrder.value = ''; await loadManage(); });
}
// Saved as soon as the box is left, like a user's quota; whatever the server kept is put back in the box either way.
async function updateCategory(c, field, e) {
 const box = e.target, raw = box.value, next = { name: c.name, order: c.order };
 next[field] = field === 'order' ? Number(raw) : raw.trim();
 const fine = field === 'order' ? raw !== '' && Number.isInteger(next.order) : next.name.length >= 1 && next.name.length <= 64;
 if (fine && next[field] !== c[field]) await action(async () => {
  const updated = await api(`/api/templates/categories/${c.id}`, 'PUT', next);
  const list = admin.value.categories, i = list.findIndex(x => x.id === c.id); if (i >= 0) list[i] = updated; list.sort(byOrder);
 });
 box.value = (admin.value.categories.find(x => x.id === c.id) ?? c)[field];
}
function askDelete(c) { categoryTarget.value = c; dialog.value = 'deleteCategory'; }
async function deleteCategory() { const c = categoryTarget.value; await action(async () => { await api(`/api/templates/categories/${c.id}`, 'DELETE'); dialog.value = null; await loadManage(); }); }
</script>
<template>
 <!-- The catalog is a page of its own; the owner's side sits inside the management page, which supplies the page around it. -->
 <div :class="manage ? 'tpl-manage' : 'view tpl-page'">
  <div v-if="!manage" class="view-head">
   <div><h1>{{ t('stTemplatesTitle') }}</h1><p>{{ t('stTemplatesIntro') }}</p></div>
   <button v-if="isOwner" type="button" class="btn" @click="go('admin', 'templates')"><SlidersHorizontal/>{{ t('stManageTemplates') }}</button>
  </div>

  <template v-if="manage">
   <section class="card" aria-labelledby="tpl-categories">
    <div class="card-head"><h2 id="tpl-categories">{{ t('stCategories') }}</h2></div>
    <p class="field-help">{{ t('stCategoriesHelp') }}</p>
    <div v-if="manageLoaded && !admin.categories.length" class="none"><LayoutTemplate/><span>{{ t('stNoCategories') }}</span></div>
    <div v-if="admin.categories.length" class="list">
     <div v-for="c in admin.categories" :key="c.id" class="row cat">
      <input class="input" :value="c.name" dir="auto" maxlength="64" :aria-label="t('stCategoryName')" :disabled="busy" @change="updateCategory(c, 'name', $event)">
      <span class="count num">{{ tf('stTemplateCount', countIn(c)) }}</span>
      <label class="order"><span>{{ t('stOrder') }}</span><input class="input num" type="number" step="1" :value="c.order" :disabled="busy" @change="updateCategory(c, 'order', $event)"></label>
      <button type="button" class="btn btn-ghost btn-icon btn-sm del" :aria-label="t('stDeleteCategory')" :title="t('stDeleteCategory')" :disabled="busy" @click="askDelete(c)"><Trash2/></button>
     </div>
    </div>
    <form class="row cat add" @submit.prevent="addCategory">
     <input v-model="categoryName" class="input" dir="auto" maxlength="64" :placeholder="t('stCategoryName')" :aria-label="t('stCategoryName')">
     <label class="order"><span>{{ t('stOrder') }}</span><input v-model="categoryOrder" class="input num" type="number" step="1" :placeholder="n(nextOrder)"></label>
     <button class="btn btn-primary" :disabled="busy || !categoryName.trim()"><Plus/>{{ t('stAddCategory') }}</button>
    </form>
   </section>
   <div class="bar-head">
    <h2>{{ t('stAllTemplates') }} <span class="faint-text num">{{ n(admin.templates.length) }}</span></h2>
    <span class="grow"/>
    <small v-if="manageLoaded && !admin.categories.length" class="faint-text">{{ t('stNeedCategory') }}</small>
    <button type="button" class="btn btn-primary" :disabled="busy || !admin.categories.length" @click="create()"><Plus/>{{ t('stNewTemplate') }}</button>
   </div>
  </template>

  <div v-if="sections.length > 1" class="cluster chips" role="group" :aria-label="t('stCategories')">
   <button type="button" class="chip" :class="{ 'chip-on': !currentFilter }" :aria-pressed="!currentFilter" @click="filter = null">{{ t('stAllCategories') }}</button>
   <button v-for="s in sections" :key="s.id" type="button" class="chip" :class="{ 'chip-on': currentFilter === s.id }" :aria-pressed="currentFilter === s.id" dir="auto" @click="filter = s.id">{{ s.name }}</button>
  </div>

  <div v-if="!manage && !loaded" class="grid" aria-hidden="true"><div v-for="i in 8" :key="i" class="skel cover-skel"></div></div>
  <div v-else-if="!manage && !total" class="blank">
   <LayoutTemplate/><h2>{{ t('stNoTemplates') }}</h2><p>{{ t(isOwner ? 'stNoTemplatesOwner' : 'stNoTemplatesBody') }}</p>
   <div v-if="isOwner" class="cluster"><button type="button" class="btn btn-primary" @click="go('admin', 'templates')"><SlidersHorizontal/>{{ t('stManageTemplates') }}</button></div>
  </div>

  <section v-for="s in shown" :key="s.id" class="section" :aria-labelledby="`category-${s.id}`">
   <h2 :id="`category-${s.id}`" class="section-title"><bdi>{{ s.name }}</bdi></h2>
   <div class="grid">
    <template v-if="manage">
     <button v-for="tpl in s.templates" :key="tpl.id" type="button" class="tcard" :class="{ off: !tpl.isActive }" @click="edit(tpl)">
      <span class="cover">
       <img v-if="tpl.sampleUrl" :src="sized(sampleSrc(tpl), CARD)" alt="" loading="lazy"><span v-else class="no-sample"><ImageOff/></span>
       <span class="badges">
        <span v-if="!tpl.isActive" class="pill pill-muted"><EyeOff/>{{ t('stInactive') }}</span>
        <span v-if="tpl.sampleState === 'generating'" class="pill pill-warn"><LoaderCircle class="spin"/>{{ t('stSampleGenerating') }}</span>
        <span v-else-if="tpl.sampleState === 'failed'" class="pill pill-bad"><AlertTriangle/>{{ t('stSampleFailed') }}</span>
        <span v-else-if="!tpl.sampleUrl" class="pill pill-warn"><ImageOff/>{{ t('stNoSample') }}</span>
       </span>
      </span>
      <b dir="auto">{{ tpl.title }}</b>
      <small>{{ tpl.provider }}{{ t('stSep') }}{{ photoMeta(tpl.minInputs, tpl.maxInputs) }}{{ t('stSep') }}{{ t('stOrder') }} {{ n(tpl.order) }}</small>
     </button>
     <button type="button" class="tcard add" :disabled="busy" @click="create(s.id)"><span class="cover"><Plus/><span>{{ t('stNewTemplate') }}</span></span></button>
    </template>
    <template v-else>
     <button v-for="tpl in s.templates" :key="tpl.id" type="button" class="tcard" @click="choose(tpl)">
      <span class="cover"><img :src="sized(tpl.sampleUrl, CARD)" alt="" loading="lazy"></span>
      <b dir="auto">{{ tpl.title }}</b>
      <small>{{ photoMeta(tpl.minInputs, tpl.maxInputs) }}</small>
     </button>
    </template>
   </div>
  </section>

  <Teleport v-if="chosen" to="body"><Modal size="lg" :title="chosen.title" :close-label="t('stClose')" @close="closeUse">
   <form class="use" @submit.prevent="make">
    <img class="sample" :src="sized(chosen.sampleUrl, VIEW)" :alt="chosen.title">
    <div class="use-body">
     <p v-if="chosen.instructions" class="instructions" dir="auto">{{ chosen.instructions }}</p>
     <PhotoPicker v-if="chosen.maxInputs > 0" id="template-photos" v-model="photos" :max="chosen.maxInputs" :label="t('stYourPhotos')" :hint="hint"/>
     <div class="field"><label for="template-note">{{ t('stNote') }}</label><textarea id="template-note" v-model="note" class="input" dir="auto" rows="3" maxlength="1000" :placeholder="t('stNotePlaceholder')"></textarea></div>
     <p v-if="pay" class="cost"><Coins/><span>{{ pay.lead }}<b v-if="pay.name" dir="auto">{{ pay.name }}</b>{{ pay.tail }}</span></p>
    </div>
    <div class="dlg-actions">
     <button type="button" class="btn" @click="closeUse">{{ t('stCancel') }}</button>
     <button class="btn btn-primary" :disabled="busy || !complete"><WandSparkles/>{{ busy ? t('stWorking') : t('stMakeIt') }}</button>
    </div>
   </form>
  </Modal></Teleport>
  <ConfirmDialog v-else-if="dialog === 'deleteCategory'" :title="t('stDeleteCategory')" :body="t('stDeleteCategoryBody')" :confirm-label="t('stDeleteCategory')" @confirm="deleteCategory" @close="dialog = null"><p class="target" dir="auto">{{ categoryTarget?.name }}</p></ConfirmDialog>
  <TemplateEditor v-else-if="dialog === 'editor'" :template="editing" :categories="admin.categories" :defaults="editorDefaults" @saved="saved" @deleted="deleted" @generating="sampleStarted" @close="dialog = null"/>
 </div>
</template>
<style scoped>
.tpl-manage{display:flex;flex-direction:column;gap:20px;min-width:0}
.none{display:flex;align-items:center;gap:10px;color:var(--muted);font-size:13.5px}
.none svg.lucide{color:var(--faint)}
.row.cat{display:grid;grid-template-columns:minmax(0,1fr) auto auto 32px;gap:12px;align-items:center}
.row.cat.add{grid-template-columns:minmax(0,1fr) auto auto;padding-top:14px;border-top:1px solid var(--line)}
.cat .count{font-size:12.5px;color:var(--faint);white-space:nowrap}
.order{display:flex;align-items:center;gap:8px;font-size:12.5px;color:var(--muted);white-space:nowrap}
.order .input{width:76px;min-height:36px;padding:6px 10px}
.cat>.input{min-height:36px;padding:6px 12px}
.del:hover:not(:disabled){color:var(--bad)}
.bar-head{display:flex;align-items:center;gap:12px;flex-wrap:wrap}
.bar-head h2{font-size:17px;display:flex;align-items:baseline;gap:8px}
.chips{gap:6px}
.section{display:flex;flex-direction:column;gap:12px}
.section-title{font-size:15px;font-weight:600}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:18px 14px}
.cover-skel{aspect-ratio:4/5;border-radius:var(--r)}
.tcard{display:flex;flex-direction:column;gap:6px;min-width:0;padding:0;border:0;background:none;color:var(--text);text-align:start}
.tcard .cover{position:relative;display:grid;place-items:center;aspect-ratio:4/5;border-radius:var(--r);overflow:hidden;background:var(--s3);color:var(--faint)}
.tcard .cover img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;transition:transform .3s}
.tcard:hover .cover img{transform:scale(1.03)}
.tcard:focus-visible{outline:none}
.tcard:focus-visible .cover{outline:2px solid var(--accent);outline-offset:2px}
.tcard.off .cover img{opacity:.45}
.tcard b{font-size:14px;font-weight:600;line-height:1.4;margin-top:2px}
.tcard small{font-size:12px;color:var(--muted)}
.no-sample svg.lucide{width:30px;height:30px}
.badges{position:absolute;top:8px;inset-inline-start:8px;inset-inline-end:8px;display:flex;flex-wrap:wrap;gap:4px}
.badges .pill{background:color-mix(in srgb,var(--s1) 86%,transparent);backdrop-filter:blur(6px)}
.tcard.add .cover{display:flex;flex-direction:column;gap:8px;justify-content:center;align-items:center;border:1px dashed var(--line2);background:transparent;color:var(--muted);font-size:13px}
.tcard.add:hover:not(:disabled) .cover{border-color:var(--accent-line);color:var(--text)}
.tcard.add .cover svg.lucide{width:26px;height:26px}
.use{display:grid;grid-template-columns:minmax(0,.9fr) minmax(0,1.1fr);gap:16px 22px;align-items:start}
.use>.dlg-actions{grid-column:1/-1}
.sample{width:100%;max-height:62vh;object-fit:contain;border-radius:var(--r);background:var(--bg);display:block}
.use-body{display:flex;flex-direction:column;gap:16px;min-width:0}
.instructions{white-space:pre-wrap;overflow-wrap:anywhere;line-height:1.8}
.cost{display:flex;align-items:center;gap:8px;font-size:13px;color:var(--muted)}
.cost svg.lucide{width:16px;height:16px;color:var(--accent)}
.cost b{color:var(--text);font-weight:600;margin-inline:.2em}
.target{font-weight:600}
@media (max-width:860px){
 .grid{grid-template-columns:repeat(2,minmax(0,1fr));gap:14px 10px}
 .row.cat{grid-template-columns:minmax(0,1fr) auto 32px}
 .cat .count{display:none}
 .row.cat.add{grid-template-columns:minmax(0,1fr) auto}
 .cat.add .btn{grid-column:1/-1}
}
@media (max-width:640px){
 .use{grid-template-columns:1fr}
 .sample{max-height:36vh}
}
</style>
