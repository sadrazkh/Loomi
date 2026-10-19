<script setup>
import { ref, computed } from 'vue';
import { Search, Plus, MessagesSquare, ChevronLeft } from 'lucide-vue-next';
import { counted } from '../components/studio/shared.js';
import { t, statusKnown, projects, dayGroup, ago, when, go } from '../core';
// Every chat of the person's, newest first and grouped by day, the way the side menu lists them; it is the phone's «Chats» tab.
const query = ref('');
const GROUPS = ['today', 'yesterday', 'thisWeek', 'earlier'], LABELS = { today: 'shToday', yesterday: 'shYesterday', thisWeek: 'shThisWeek', earlier: 'shEarlier' };
const groups = computed(() => {
 const q = query.value.trim().toLocaleLowerCase();
 const list = projects.value.filter(p => !q || p.title.toLocaleLowerCase().includes(q)).sort((a, b) => when(b.updatedAt) - when(a.updatedAt));
 return GROUPS.map(key => ({ key, items: list.filter(p => dayGroup(p.updatedAt) === key) })).filter(g => g.items.length);
});
</script>
<template>
 <div class="view view-narrow chats">
  <div class="view-head">
   <div><h1>{{ t('shChats') }}</h1><p v-if="projects.length">{{ counted(projects.length, 'stChatsCountOne', 'stChatsCount') }}</p></div>
   <button type="button" class="btn btn-primary" @click="go('home')"><Plus/>{{ t('shNewImage') }}</button>
  </div>
  <label v-if="projects.length" class="search"><Search/><input v-model="query" type="search" :placeholder="t('shSearchChats')" :aria-label="t('shSearchChats')"></label>
  <div v-if="!statusKnown" class="stack" aria-hidden="true"><div v-for="i in 4" :key="i" class="skel row-skel"></div></div>
  <div v-else-if="!projects.length" class="blank"><MessagesSquare/><h2>{{ t('stNoChatsTitle') }}</h2><p>{{ t('stNoChatsBody') }}</p><div class="cluster"><button type="button" class="btn btn-primary" @click="go('home')"><Plus/>{{ t('shNewImage') }}</button></div></div>
  <p v-else-if="!groups.length" class="none">{{ t('shNoMatch') }}</p>
  <section v-for="g in groups" :key="g.key" class="group" :aria-labelledby="`chats-${g.key}`">
   <h2 :id="`chats-${g.key}`">{{ t(LABELS[g.key]) }}</h2>
   <div class="rows">
    <button v-for="p in g.items" :key="p.id" type="button" class="row" @click="go('chat', p.id)">
     <span class="thumb"><img v-if="p.coverId" :src="`/api/generations/${p.coverId}/image`" alt="" loading="lazy"><MessagesSquare v-else/></span>
     <span class="text"><b class="truncate" dir="auto">{{ p.title }}</b><small>{{ ago(p.updatedAt) }}{{ t('stSep') }}{{ counted(p.generationCount, 'stMessagesOne', 'stMessages') }}</small></span>
     <ChevronLeft class="go"/>
    </button>
   </div>
  </section>
 </div>
</template>
<style scoped>
.search{display:flex;align-items:center;gap:8px;height:42px;padding:0 12px;border-radius:var(--r2);background:var(--s1);border:1px solid var(--line);color:var(--faint)}
.search:focus-within{border-color:var(--accent-line);box-shadow:0 0 0 3px var(--accent-soft)}
.search input{flex:1;min-width:0;border:0;background:none;outline:none;font-size:14px;color:var(--text)}
.row-skel{height:62px}
.none{color:var(--muted)}
.group{display:flex;flex-direction:column;gap:8px}
.group h2{font-size:12.5px;font-weight:500;color:var(--faint)}
.rows{display:flex;flex-direction:column;border:1px solid var(--line);border-radius:var(--r);background:var(--s1);overflow:hidden}
.row{display:flex;align-items:center;gap:12px;width:100%;min-height:62px;padding:8px 12px;border:0;border-top:1px solid var(--line);background:transparent;color:var(--text);text-align:start}
.row:first-child{border-top:0}
.row:hover{background:var(--s2)}
.thumb{width:44px;height:44px;flex:none;border-radius:var(--r2);overflow:hidden;background:var(--s3);display:grid;place-items:center;color:var(--faint)}
.thumb img{width:100%;height:100%;object-fit:cover;display:block}
.text{flex:1;display:flex;flex-direction:column;min-width:0;line-height:1.45}
.text b{font-size:14px;font-weight:600}
.text small{font-size:12.5px;color:var(--muted)}
.go{color:var(--faint)}
:root[dir=ltr] .go{transform:scaleX(-1)}
@media (max-width:860px){.view-head .btn{display:none}}
</style>
