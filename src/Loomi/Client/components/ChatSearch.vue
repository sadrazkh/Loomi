<script setup>
import { ref, computed, watch, nextTick, onMounted, onBeforeUnmount } from 'vue';
import { Search, MessagesSquare, X } from 'lucide-vue-next';
import { t, projects, ago, go } from '../core';
import { sized, THUMB } from '../images.js';
// Finding a chat, the way chat apps do it now: a box that opens over the page, the latest chats before anything is typed, the arrow keys to pick and
// Enter to open. It replaces a search field that took a row of the side menu for something used now and then.
const emit = defineEmits(['close']);
const query = ref(''), active = ref(0), input = ref(null), list = ref(null);
let previous;
const results = computed(() => {
  const q = query.value.trim().toLocaleLowerCase();
  return (q ? projects.value.filter(p => p.title.toLocaleLowerCase().includes(q)) : projects.value).slice(0, 30);
});
watch(query, () => { active.value = 0; });
function open(p) { emit('close'); go('chat', p.id); }
async function move(by) {
  if (!results.value.length) return;
  active.value = (active.value + by + results.value.length) % results.value.length;
  await nextTick(); list.value?.querySelector('[aria-selected=true]')?.scrollIntoView({ block: 'nearest' });
}
function key(e) {
  if (e.key === 'Escape') { e.preventDefault(); emit('close'); }
  else if (e.key === 'ArrowDown') { e.preventDefault(); move(1); }
  else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
  else if (e.key === 'Enter' && results.value[active.value]) { e.preventDefault(); open(results.value[active.value]); }
}
onMounted(() => { previous = document.activeElement; document.body.style.overflow = 'hidden'; input.value?.focus(); });
onBeforeUnmount(() => { document.body.style.overflow = ''; previous?.focus?.(); });
</script>
<template>
 <Teleport to="body">
  <div class="backdrop" @mousedown.self="emit('close')">
   <section class="palette" role="dialog" aria-modal="true" :aria-label="t('shSearchChats')" @keydown="key">
    <label class="box"><Search/><input ref="input" v-model="query" type="search" :placeholder="t('shSearchChats')" :aria-label="t('shSearchChats')" role="combobox" aria-controls="chat-search-list" :aria-expanded="results.length>0" autocomplete="off"><button class="btn btn-ghost btn-icon btn-sm" :aria-label="t('shDismiss')" @click="emit('close')"><X/></button></label>
    <p class="label">{{ query.trim() ? t('shResults') : t('shRecent') }}</p>
    <ul v-if="results.length" id="chat-search-list" ref="list" class="list" role="listbox">
     <li v-for="(p, i) in results" :key="p.id" role="option" :aria-selected="i===active" @mousemove="active=i" @click="open(p)">
      <span class="thumb"><img v-if="p.coverId" :src="sized(`/api/generations/${p.coverId}/image`, THUMB)" alt="" loading="lazy"><MessagesSquare v-else/></span>
      <span class="title truncate" dir="auto">{{ p.title }}</span>
      <small class="faint-text">{{ ago(p.updatedAt) }}</small>
     </li>
    </ul>
    <p v-else class="none">{{ projects.length ? t('shNoMatch') : t('shNoChats') }}</p>
    <footer class="keys"><span><kbd>↑</kbd><kbd>↓</kbd> {{ t('shKeysMove') }}</span><span><kbd>Enter</kbd> {{ t('shKeysOpen') }}</span><span><kbd>Esc</kbd> {{ t('shKeysClose') }}</span></footer>
   </section>
  </div>
 </Teleport>
</template>
<style scoped>
.backdrop{position:fixed;inset:0;z-index:70;background:var(--dim);display:flex;justify-content:center;align-items:flex-start;padding:12vh 16px 16px}
.palette{width:min(560px,100%);max-height:72vh;display:flex;flex-direction:column;background:var(--s1);border:1px solid var(--line2);border-radius:var(--r3);box-shadow:var(--shadow);overflow:hidden}
@media (prefers-reduced-motion:no-preference){.palette{animation:drop .16s ease-out}}
@keyframes drop{from{transform:translateY(-6px);opacity:0}to{transform:none;opacity:1}}
.box{display:flex;align-items:center;gap:10px;padding:10px 12px 10px 16px;border-bottom:1px solid var(--line);color:var(--faint)}
:root[dir=rtl] .box{padding:10px 16px 10px 12px}
.box input{flex:1;min-width:0;height:36px;border:0;background:none;outline:none;font-size:15.5px;color:var(--text)}
.box input::-webkit-search-cancel-button{display:none}
.label{font-size:12px;color:var(--faint);padding:10px 16px 4px}
.list{list-style:none;margin:0;padding:4px 8px 8px;overflow-y:auto;min-height:0}
.list li{display:flex;align-items:center;gap:12px;min-height:46px;padding:4px 8px;border-radius:var(--r2);cursor:pointer}
.list li[aria-selected=true]{background:var(--s3)}
.thumb{width:32px;height:32px;border-radius:8px;background:var(--s3);display:grid;place-items:center;overflow:hidden;flex:none;color:var(--faint)}
.thumb img{width:100%;height:100%;object-fit:cover;display:block}
.thumb svg{width:16px;height:16px}
.title{flex:1;font-size:14px}
.none{padding:18px 16px 22px;color:var(--muted);font-size:14px}
.keys{display:flex;flex-wrap:wrap;gap:6px 16px;padding:10px 16px;border-top:1px solid var(--line);font-size:12px;color:var(--faint)}
kbd{display:inline-block;min-width:20px;padding:0 5px;margin-inline-end:2px;border-radius:5px;border:1px solid var(--line2);background:var(--s2);font:inherit;font-size:11px;text-align:center;line-height:18px}
@media (max-width:860px){.keys{display:none}.backdrop{padding-top:max(12px,env(safe-area-inset-top,0px))}}
</style>
