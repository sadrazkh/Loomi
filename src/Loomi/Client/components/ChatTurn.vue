<script setup>
import { ref, computed, watch } from 'vue';
import { Pencil, GitBranchPlus, Download, X, LayoutTemplate, CircleAlert, Ban, ImageOff } from 'lucide-vue-next';
import LoomiMark from './LoomiMark.vue';
import { queueText, STEPS, phaseOf, stepOf } from './queue.js';
import { t, isOwner, busy, queueInfo, FINISHED, ago, cancelOne } from '../core';
import { sized, inputSrc, VIEW } from '../images.js';
// One message and its answer, the way a chat app shows them: the person's words on their side, the picture (or the reply) on the other.
// latest: the chat's newest picture, which the next message works on anyway, so editing it needs nothing sent again.
const props = defineProps({ generation: Object, latest: Boolean });
const emit = defineEmits(['edit', 'branch', 'preview']);
const g = computed(() => props.generation);
const asked = computed(() => !!(g.value.prompt || g.value.inputs?.length || g.value.templateTitle));
const settled = computed(() => FINISHED.includes(g.value.status));
const phase = computed(() => phaseOf(g.value.status));
const step = computed(() => stepOf(g.value.status));
const TITLES = { queue: 'stInQueue', sending: 'stSending', making: 'stMaking' };
// Errors whose own text already says the cost came back, so it is not said twice.
const SAYS_REFUND = ['AutomationFailed'];
const detail = computed(() => phase.value === 'queue' ? queueText(queueInfo.value[g.value.id]) || t('stQueueWaiting') : t('stUsually'));
// The picture's shape is known only once it arrives; until then a placeholder holds its place, and then the frame takes the picture's own shape.
const ratio = ref(0);
watch(() => g.value.imageUrl, () => { ratio.value = 0; });
const arrived = e => { const { naturalWidth: w, naturalHeight: h } = e.target; ratio.value = w && h ? w / h : 1; };
</script>
<template>
 <article class="turn">
  <div v-if="asked" class="me">
   <div v-if="generation.inputs?.length" class="ins" role="group" :aria-label="t('stSentPhotos')"><img v-for="(input, i) in generation.inputs" :key="i" :src="inputSrc(input)" alt="" loading="lazy"></div>
   <span v-if="generation.templateTitle" class="from"><LayoutTemplate/><bdi>{{ generation.templateTitle }}</bdi></span>
   <p v-if="generation.prompt" dir="auto">{{ generation.prompt }}</p>
  </div>
  <div class="ai">
   <div class="ai-head"><span class="mark"><LoomiMark :size="15"/></span><span>Loomi{{ t('stSep').trimEnd() }}</span><time :datetime="generation.createdAt">{{ ago(generation.createdAt) }}</time></div>
   <template v-if="generation.imageUrl">
    <button type="button" class="shot" :class="{ waiting: !ratio }" :style="ratio ? { '--ratio': ratio } : null" :aria-label="t('stOpenImage')" @click="emit('preview', generation)"><img :src="sized(generation.imageUrl, VIEW)" :alt="generation.prompt || generation.templateTitle || ''" @load="arrived"></button>
    <div class="acts">
     <button type="button" class="chip" :title="t(latest ? 'stEditHint' : 'stEditOlderHint')" @click="emit('edit', generation)"><Pencil/>{{ t('stEdit') }}</button>
     <button type="button" class="chip" :title="t('stBranchHint')" @click="emit('branch', generation)"><GitBranchPlus/><span class="long">{{ t('stBranch') }}</span><span class="short">{{ t('stBranchShort') }}</span></button>
     <a class="chip" :href="generation.imageUrl" :download="`loomi-${generation.id}`"><Download/>{{ t('stDownload') }}</a>
    </div>
   </template>
   <div v-else-if="generation.replyText" class="reply" dir="auto">{{ generation.replyText }}</div>
   <div v-else-if="generation.status === 'Failed'" class="notice notice-bad failed"><CircleAlert/><span><b>{{ t(generation.errorMessage || 'stFailed') }}</b><small v-if="!isOwner && !SAYS_REFUND.includes(generation.errorMessage)">{{ t('stRefunded') }}</small></span></div>
   <p v-else-if="generation.status === 'Cancelled'" class="quiet"><Ban/>{{ t('stCancelled') }}</p>
   <p v-else-if="settled" class="quiet"><ImageOff/>{{ t('stNoPicture') }}</p>
   <div v-else class="progress" role="status">
    <div class="skel ph" aria-hidden="true"></div>
    <div class="pinfo">
     <b>{{ t(TITLES[phase]) }}</b>
     <span class="sub">{{ detail }}</span>
     <ol class="steps"><li v-for="(s, i) in STEPS" :key="s" :class="{ done: i < step, now: i === step }" :aria-current="i === step ? 'step' : null"><i></i>{{ t(s) }}</li></ol>
     <div><button type="button" class="btn btn-ghost btn-sm" :disabled="busy" @click="cancelOne(generation)"><X/>{{ t('stCancelThis') }}</button></div>
    </div>
   </div>
  </div>
 </article>
</template>
<style scoped>
.turn{display:flex;flex-direction:column;gap:16px}
.me{align-self:flex-end;max-width:min(78%,540px);display:flex;flex-direction:column;gap:8px;padding:10px 14px;border-radius:var(--r);background:var(--s3);font-size:14.5px;line-height:1.75}
.me p{white-space:pre-wrap;overflow-wrap:anywhere}
.ins{display:flex;flex-wrap:wrap;gap:6px}
.ins img{width:76px;height:76px;object-fit:cover;border-radius:var(--r2);display:block;background:var(--s2)}
.from{display:inline-flex;align-items:center;gap:6px;align-self:flex-start;max-width:100%;font-size:12.5px;color:var(--muted)}
.from svg.lucide{width:15px;height:15px;color:var(--accent)}
.ai{display:flex;flex-direction:column;align-items:flex-start;gap:10px;min-width:0}
.ai-head{display:flex;align-items:center;gap:5px;color:var(--muted);font-size:12.5px}
.mark{width:22px;height:22px;border-radius:6px;background:var(--s3);display:grid;place-items:center;margin-inline-end:2px}
.shot{--tall:min(70vh,600px);position:relative;display:block;width:min(100%,420px,calc(var(--tall) * var(--ratio,1)));padding:0;border:0;border-radius:var(--r);overflow:hidden;background:var(--s2);cursor:zoom-in}
.shot img{display:block;width:100%;height:auto}
.shot.waiting{width:min(320px,100%);aspect-ratio:4/5}
.shot.waiting img{position:absolute;width:1px;height:1px;opacity:0}
.shot:focus-visible{outline-offset:3px}
.acts{display:flex;flex-wrap:wrap;gap:6px}
.acts .short{display:none}
.reply{max-width:100%;padding:12px 16px;border-radius:var(--r);border:1px solid var(--line);background:var(--s1);white-space:pre-wrap;overflow-wrap:anywhere;font-size:14.5px;line-height:1.85}
.failed{max-width:min(100%,520px)}
.failed span{display:flex;flex-direction:column;gap:2px}
.failed b{font-weight:600}
.failed small{color:var(--muted);font-size:12.5px}
.quiet{display:inline-flex;align-items:center;gap:8px;color:var(--faint);font-size:13.5px}
.quiet svg.lucide{width:16px;height:16px}
.progress{width:100%;display:grid;grid-template-columns:108px minmax(0,1fr);gap:16px;align-items:center;padding:14px;border:1px solid var(--line);border-radius:var(--r);background:var(--s1)}
.ph{width:108px;aspect-ratio:4/5}
.pinfo{display:flex;flex-direction:column;align-items:flex-start;gap:8px;min-width:0}
.pinfo b{font-size:15px}
.sub{color:var(--muted);font-size:12.5px;margin-top:-4px}
.steps{display:flex;flex-wrap:wrap;align-items:center;gap:6px;margin:2px 0 0;padding:0;list-style:none;font-size:12.5px;color:var(--muted)}
.steps li{display:inline-flex;align-items:center;gap:6px;white-space:nowrap}
.steps li+li::before{content:"";width:16px;height:1px;background:var(--line2);margin-inline-end:2px}
.steps i{width:8px;height:8px;border-radius:50%;background:var(--line2);flex:none}
.steps .done i{background:var(--ok)}
.steps .now{color:var(--text);font-weight:600}
.steps .now i{background:var(--accent);box-shadow:0 0 0 4px var(--accent-soft)}
@media (prefers-reduced-motion:no-preference){.steps .now i{animation:pulse 1.6s ease-in-out infinite}}
@keyframes pulse{50%{box-shadow:0 0 0 6px var(--accent-soft)}}
.pinfo .btn{margin-inline-start:-10px}
@media (max-width:860px){
 .me{max-width:86%}
 .shot{width:min(100%,calc(var(--tall) * var(--ratio,1)))}
 .progress{grid-template-columns:72px minmax(0,1fr);gap:12px;padding:12px}
 .ph{width:72px}
 .acts .long{display:none}
 .acts .short{display:inline}
}
@media (max-width:520px){.steps{display:none}}
</style>
