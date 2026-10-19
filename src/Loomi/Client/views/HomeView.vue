<script setup>
import { ref, computed, onMounted } from 'vue';
import { MessagesSquare, ChevronLeft, PenLine, Hourglass, Repeat } from 'lucide-vue-next';
import Composer from '../components/Composer.vue';
import { flatten, photoMeta } from '../components/templates.js';
import { counted } from '../components/studio/shared.js';
import { sized, THUMB, CARD } from '../images.js';
import { t, tf, me, statusKnown, projects, api, startChat, ago, when, go } from '../core';
// The landing page has one job, starting an image: the box to describe it in, a few templates a click away, and the chats to carry on with.
const composer = ref(null), templates = ref([]);
const name = computed(() => me.value?.username || '');
// Brand new: nothing made yet, so the page explains itself instead of listing chats that do not exist.
const fresh = computed(() => statusKnown.value && !projects.value.length);
const recent = computed(() => [...projects.value].sort((a, b) => when(b.updatedAt) - when(a.updatedAt)).slice(0, 6));
const IDEAS = ['stIdea1', 'stIdea2', 'stIdea3'];
// Quietly empty when the list cannot be had: templates are a shortcut here, not the page.
onMounted(async () => { try { templates.value = flatten(await api('/api/templates')).slice(0, 8); } catch { templates.value = []; } });
</script>
<template>
 <div class="view home">
  <section class="hero">
   <h1>{{ name ? tf('stHello', name) : t('stHelloNoName') }}</h1>
   <p v-if="fresh" class="lede">{{ t('stFreshLede') }}</p>
   <Composer ref="composer" mode="home" draft-key="home" show-provider :submit="startChat" :placeholder="t('stHomePlaceholder')"/>
   <div v-if="fresh" class="ideas"><span>{{ t('stIdeas') }}</span><button v-for="k in IDEAS" :key="k" type="button" class="chip" dir="auto" @click="composer?.fill(t(k))">{{ t(k) }}</button></div>
  </section>

  <section v-if="templates.length" class="blk" aria-labelledby="home-templates">
   <div class="bh"><h2 id="home-templates">{{ t('stStartTemplate') }}</h2><button type="button" class="more" @click="go('templates')">{{ t('stAllTemplates') }}<ChevronLeft class="dir"/></button></div>
   <div class="tpls">
    <button v-for="tpl in templates" :key="tpl.id" type="button" class="tpl" @click="go('templates', tpl.id)">
     <span class="cover"><img :src="sized(tpl.sampleUrl, CARD)" alt="" loading="lazy"></span>
     <b dir="auto">{{ tpl.title }}</b>
     <small><bdi>{{ tpl.category }}</bdi>{{ t('stSep') }}{{ photoMeta(tpl.minInputs, tpl.maxInputs) }}</small>
    </button>
   </div>
  </section>

  <section v-if="recent.length" class="blk" aria-labelledby="home-recent">
   <div class="bh"><h2 id="home-recent">{{ t('stContinue') }}</h2><button v-if="projects.length > recent.length" type="button" class="more" @click="go('chats')">{{ t('stAllChats') }}<ChevronLeft class="dir"/></button></div>
   <div class="recent">
    <button v-for="p in recent" :key="p.id" type="button" class="rc" @click="go('chat', p.id)">
     <span class="thumb"><img v-if="p.coverId" :src="sized(`/api/generations/${p.coverId}/image`, THUMB)" alt="" loading="lazy"><MessagesSquare v-else/></span>
     <span class="rc-text"><b class="truncate" dir="auto">{{ p.title }}</b><small>{{ ago(p.updatedAt) }}{{ t('stSep') }}{{ counted(p.generationCount, 'stMessagesOne', 'stMessages') }}</small></span>
    </button>
   </div>
  </section>

  <section v-else-if="fresh" class="blk" aria-labelledby="home-how">
   <div class="bh"><h2 id="home-how">{{ t('stHowTitle') }}</h2></div>
   <ol class="how">
    <li><span class="ic"><PenLine/></span><b>{{ t('stHow1') }}</b><small>{{ t('stHow1Body') }}</small></li>
    <li><span class="ic"><Hourglass/></span><b>{{ t('stHow2') }}</b><small>{{ t('stHow2Body') }}</small></li>
    <li><span class="ic"><Repeat/></span><b>{{ t('stHow3') }}</b><small>{{ t('stHow3Body') }}</small></li>
   </ol>
  </section>
 </div>
</template>
<style scoped>
.home{max-width:840px;padding-top:64px;gap:34px}
.hero{display:flex;flex-direction:column;gap:18px}
.hero h1{font-size:34px;line-height:1.3}
.lede{color:var(--muted);font-size:15px;margin-top:-8px;max-width:60ch}
.ideas{display:flex;flex-wrap:wrap;align-items:center;gap:8px;font-size:13px;color:var(--faint)}
.ideas .chip{max-width:100%;overflow:hidden;text-overflow:ellipsis}
.blk{display:flex;flex-direction:column;gap:12px}
.bh{display:flex;align-items:baseline;justify-content:space-between;gap:12px}
.bh h2{font-size:15px;font-weight:600}
.more{display:inline-flex;align-items:center;gap:2px;border:0;background:none;padding:0;color:var(--muted);font-size:13px}
.more:hover{color:var(--accent)}
.more svg.lucide{width:16px;height:16px}
:root[dir=ltr] .dir{transform:scaleX(-1)}
.tpls{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px}
.tpl:nth-child(n+5){display:none}
.tpl{display:flex;flex-direction:column;gap:6px;min-width:0;padding:0;border:0;background:none;text-align:start;color:var(--text)}
.cover{display:block;aspect-ratio:4/5;border-radius:var(--r);overflow:hidden;background:var(--s3)}
.cover img{width:100%;height:100%;object-fit:cover;display:block;transition:transform .3s}
.tpl:hover .cover img{transform:scale(1.03)}
.tpl:focus-visible{outline:none}
.tpl:focus-visible .cover{outline:2px solid var(--accent);outline-offset:2px}
.tpl b{font-size:13.5px;font-weight:600;line-height:1.35;margin-top:2px}
.tpl small{font-size:12px;color:var(--muted)}
.recent{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}
.rc{display:flex;align-items:center;gap:10px;min-width:0;padding:8px;border:1px solid var(--line);border-radius:var(--r);background:var(--s1);text-align:start;color:var(--text);transition:border-color .15s,background .15s}
.rc:hover{border-color:var(--line2);background:var(--s2)}
.thumb{width:46px;height:46px;flex:none;border-radius:var(--r2);overflow:hidden;background:var(--s3);display:grid;place-items:center;color:var(--faint)}
.thumb img{width:100%;height:100%;object-fit:cover;display:block}
.rc-text{display:flex;flex-direction:column;min-width:0;line-height:1.45}
.rc-text b{font-size:13.5px;font-weight:600}
.rc-text small{font-size:12px;color:var(--muted)}
.how{list-style:none;margin:0;padding:0;display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}
.how li{display:flex;flex-direction:column;gap:4px;padding:16px;border:1px solid var(--line);border-radius:var(--r);background:var(--s1)}
.how .ic{width:34px;height:34px;border-radius:var(--r2);background:var(--accent-soft);color:var(--accent);display:grid;place-items:center;margin-bottom:6px}
.how b{font-size:14px}
.how small{font-size:12.5px;color:var(--muted);line-height:1.7}
@media (max-width:860px){
 .home{padding-top:22px;gap:26px}
 .hero h1{font-size:26px}
 .ideas{flex-wrap:nowrap;overflow-x:auto;margin-inline:-16px;padding-inline:16px;scrollbar-width:none}
 .ideas::-webkit-scrollbar{display:none}
 .ideas>*{flex:none}
 /* Templates slide sideways on a phone, as many as the server gave, instead of four in a row. */
 .tpls{display:flex;overflow-x:auto;scroll-snap-type:x mandatory;gap:10px;margin-inline:-16px;padding-inline:16px;scrollbar-width:none}
 .tpls::-webkit-scrollbar{display:none}
 .tpl,.tpl:nth-child(n+5){display:flex;flex:0 0 132px;scroll-snap-align:start}
 .recent{grid-template-columns:1fr;gap:8px}
 .how{grid-template-columns:1fr}
 .how li{display:grid;grid-template-columns:34px minmax(0,1fr);column-gap:12px;align-items:center}
 .how .ic{grid-row:span 2;margin:0}
}
</style>
