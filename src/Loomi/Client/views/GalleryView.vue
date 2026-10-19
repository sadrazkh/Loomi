<script setup>
import { ref, computed, watch } from 'vue';
import { Images, Plus } from 'lucide-vue-next';
import ImagePreview from '../components/studio/ImagePreview.vue';
import { counted } from '../components/studio/shared.js';
import { t, statusKnown, projects, api, when, go } from '../core';
// Every picture the person has made, newest first, gathered from each of their chats; one opens at full size with a way back to its chat.
const items = ref([]), loaded = ref(false), shown = ref(null);
let asked = 0;
async function load() {
 const ask = ++asked;
 const lists = await Promise.all(projects.value.map(p => api(`/api/projects/${p.id}/generations`).then(gs => gs.map(g => ({ ...g, projectTitle: p.title }))).catch(() => [])));
 // A slower answer to an older question must not overwrite a newer one.
 if (ask !== asked) return;
 items.value = lists.flat().filter(g => g.imageUrl).sort((a, b) => when(b.createdAt) - when(a.createdAt));
 loaded.value = true;
}
// The chat list is refreshed every few seconds; the pictures are gathered again only when a chat gained or lost one.
const signature = computed(() => statusKnown.value ? projects.value.map(p => `${p.id}:${p.generationCount}:${p.coverId}`).join('|') : null);
watch(signature, s => { if (s != null) load(); }, { immediate: true });
</script>
<template>
 <div class="view gallery">
  <div class="view-head">
   <div><h1>{{ t('shMyImages') }}</h1><p v-if="items.length">{{ counted(items.length, 'stImagesCountOne', 'stImagesCount') }}</p></div>
  </div>
  <div v-if="!loaded" class="grid" aria-hidden="true"><div v-for="i in 8" :key="i" class="skel tile-skel"></div></div>
  <div v-else-if="!items.length" class="blank"><Images/><h2>{{ t('stNoImagesTitle') }}</h2><p>{{ t('stNoImagesBody') }}</p><div class="cluster"><button type="button" class="btn btn-primary" @click="go('home')"><Plus/>{{ t('shNewImage') }}</button></div></div>
  <div v-else class="grid">
   <button v-for="g in items" :key="g.id" type="button" class="pic" :aria-label="g.prompt || g.projectTitle" @click="shown = g">
    <img :src="g.imageUrl" alt="" loading="lazy">
    <span class="cap" dir="auto">{{ g.prompt || g.templateTitle || g.projectTitle }}</span>
   </button>
  </div>
  <ImagePreview v-if="shown" :generation="shown" chat-link @close="shown = null"/>
 </div>
</template>
<style scoped>
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:12px}
.tile-skel{aspect-ratio:1;border-radius:var(--r)}
.pic{position:relative;display:block;aspect-ratio:1;padding:0;border:0;border-radius:var(--r);overflow:hidden;background:var(--s3);cursor:zoom-in}
.pic img{width:100%;height:100%;object-fit:cover;display:block;transition:transform .3s}
.pic:hover img{transform:scale(1.03)}
.cap{position:absolute;inset-inline:0;bottom:0;padding:24px 12px 10px;background:linear-gradient(to bottom,transparent,color-mix(in srgb,var(--bg) 88%,transparent));color:var(--text);font-size:12.5px;line-height:1.5;text-align:start;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;opacity:0;transition:opacity .15s}
.pic:hover .cap,.pic:focus-visible .cap{opacity:1}
@media (hover:none){.cap{display:none}}
@media (max-width:860px){.grid{grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}.pic,.tile-skel{border-radius:var(--r2)}}
</style>
