<script setup>
import { computed } from 'vue';
import { Pencil, GitBranchPlus, Download, ExternalLink, X, LayoutTemplate, CircleAlert, LoaderCircle, Ban, MessageSquareText } from 'lucide-vue-next';
import { queueText, phaseOf } from './queue.js';
import { t, n, busy, queueInfo, FINISHED, ago, cancelOne } from '../core';
// One request as a card, for the timeline and the branch tree: a secondary way to look over a chat, so it keeps a small index to tell cards apart.
// latest: the chat's newest picture, which the next message works on anyway, so editing it needs nothing sent again.
const props = defineProps({ generation: Object, index: Number, latest: Boolean });
const emit = defineEmits(['edit', 'branch', 'preview']);
const g = computed(() => props.generation);
const settled = computed(() => FINISHED.includes(g.value.status));
const TITLES = { queue: 'stInQueue', sending: 'stSending', making: 'stMaking' };
const PILL = { Completed: 'pill-ok', Answered: 'pill-ok', Failed: 'pill-bad', Cancelled: 'pill-muted' };
const DONE = { Completed: 'stStatusDone', Answered: 'stStatusAnswered', Failed: 'stStatusFailed', Cancelled: 'stStatusCancelled' };
const label = computed(() => t(settled.value ? DONE[g.value.status] : TITLES[phaseOf(g.value.status)]));
</script>
<template>
 <article class="gcard" :class="{ failed: generation.status === 'Failed' }">
  <button v-if="generation.imageUrl" type="button" class="media" :aria-label="t('stOpenImage')" @click="emit('preview', generation)"><img :src="generation.imageUrl" :alt="generation.prompt || generation.templateTitle || ''" loading="lazy"></button>
  <div v-else-if="generation.replyText" class="media text" dir="auto"><MessageSquareText/><p>{{ generation.replyText }}</p></div>
  <div v-else class="media empty" :class="{ skel: !settled }">
   <CircleAlert v-if="generation.status === 'Failed'"/><Ban v-else-if="generation.status === 'Cancelled'"/><LoaderCircle v-else-if="!settled" class="spin"/>
   <span v-if="generation.status === 'Failed'">{{ t(generation.errorMessage || 'stFailed') }}</span>
   <span v-else-if="generation.status === 'Queued'">{{ queueText(queueInfo[generation.id]) }}</span>
  </div>
  <div class="body">
   <div class="meta"><span class="idx num">#{{ n(index) }}</span><span class="pill" :class="PILL[generation.status] || ''">{{ label }}</span><span class="grow"/><time :datetime="generation.createdAt">{{ ago(generation.createdAt) }}</time></div>
   <span v-if="generation.templateTitle" class="from"><LayoutTemplate/><bdi>{{ generation.templateTitle }}</bdi></span>
   <p v-if="generation.prompt" class="prompt" dir="auto">{{ generation.prompt }}</p>
   <div v-if="generation.inputs?.length" class="ins"><img v-for="(input, i) in generation.inputs" :key="i" :src="input.url" alt="" loading="lazy"></div>
  </div>
  <footer>
   <template v-if="generation.imageUrl">
    <button type="button" class="btn btn-ghost btn-sm" :title="t(latest ? 'stEditHint' : 'stEditOlderHint')" @click="emit('edit', generation)"><Pencil/>{{ t('stEdit') }}</button>
    <button type="button" class="btn btn-ghost btn-sm" :title="t('stBranchHint')" @click="emit('branch', generation)"><GitBranchPlus/>{{ t('stBranchShort') }}</button>
   </template>
   <button v-if="!settled" type="button" class="btn btn-ghost btn-sm" :disabled="busy" @click="cancelOne(generation)"><X/>{{ t('stCancelThis') }}</button>
   <span class="grow"/>
   <a v-if="generation.imageUrl" class="btn btn-ghost btn-icon btn-sm" :href="generation.imageUrl" :download="`loomi-${generation.id}`" :aria-label="t('stDownload')" :title="t('stDownload')"><Download/></a>
   <a v-if="generation.conversationUrl" class="btn btn-ghost btn-icon btn-sm" :href="generation.conversationUrl" target="_blank" rel="noopener noreferrer" :aria-label="t('stOpenInChatGPT')" :title="t('stOpenInChatGPT')"><ExternalLink/></a>
  </footer>
 </article>
</template>
<style scoped>
.gcard{display:flex;flex-direction:column;min-width:0;background:var(--s1);border:1px solid var(--line);border-radius:var(--r);overflow:hidden}
.gcard.failed{border-color:color-mix(in srgb,var(--bad) 45%,var(--line))}
.media{display:block;width:100%;aspect-ratio:5/4;padding:0;border:0;background:var(--bg);overflow:hidden}
button.media{cursor:zoom-in}
.media img{width:100%;height:100%;object-fit:contain;display:block}
.media.text{display:flex;gap:10px;padding:14px 16px;background:var(--s2);color:var(--text);font-size:13.5px;line-height:1.8}
.media.text svg.lucide{color:var(--accent);margin-top:4px}
.media.text p{overflow:hidden;display:-webkit-box;-webkit-line-clamp:7;line-clamp:7;-webkit-box-orient:vertical;white-space:pre-wrap;overflow-wrap:anywhere}
.media.empty{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:10px;padding:16px;background:var(--s2);color:var(--muted);font-size:12.5px;text-align:center;border-radius:0}
.media.empty svg.lucide{width:26px;height:26px}
.failed .media.empty svg.lucide{color:var(--bad)}
.body{display:flex;flex-direction:column;gap:8px;padding:12px 14px 4px}
.meta{display:flex;align-items:center;gap:8px;font-size:12px;color:var(--faint)}
.idx{color:var(--accent);font-weight:600}
.from{display:inline-flex;align-items:center;gap:6px;font-size:12.5px;color:var(--muted)}
.from svg.lucide{width:15px;height:15px;color:var(--accent)}
.prompt{font-size:13.5px;line-height:1.75;white-space:pre-wrap;overflow-wrap:anywhere;display:-webkit-box;-webkit-line-clamp:4;line-clamp:4;-webkit-box-orient:vertical;overflow:hidden}
.ins{display:flex;gap:6px;flex-wrap:wrap}
.ins img{width:40px;height:40px;object-fit:cover;border-radius:7px;display:block}
footer{display:flex;align-items:center;gap:2px;padding:6px 8px 8px;margin-top:auto}
</style>
