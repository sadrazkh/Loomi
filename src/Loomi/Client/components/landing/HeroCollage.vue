<script setup>
import { computed } from 'vue';
import { sized, CARD } from '../../images.js';
// The hero's picture: up to five of the owner's samples fanned out, the first in front. Placed by hand for each count, since a page with two templates has
// to look as deliberate as one with fifty. Percent of the square it stands in: left, top, width, tilt in degrees, layer.
const props = defineProps({ items: { type: Array, required: true } });
defineEmits(['broken']);
const LAYOUTS = [
 [[16, 7.5, 68, 0, 1]],
 [[40, 22, 56, 3, 2], [4, 4, 56, -4, 1]],
 [[24, 15, 52, 0, 3], [1, 6, 38, -6, 1], [61, 42, 38, 5, 1]],
 [[26, 17, 48, 0, 3], [2, 5, 34, -6, 1], [64, 2, 34, 5, 1], [62, 54, 34, -4, 1]],
 [[27, 19, 46, 0, 3], [2, 4, 32, -6, 1], [66, 1, 32, 5, 1], [1, 54, 32, 4, 1], [67, 56, 32, -5, 1]],
];
// One of each category before a second of any, so the fan shows the range rather than five of the same look.
const picks = computed(() => {
 const turns = new Map();
 return props.items.map(item => { const turn = turns.get(item.category) || 0; turns.set(item.category, turn + 1); return [turn, item]; })
  .sort((a, b) => a[0] - b[0]).slice(0, LAYOUTS.length).map(([, item]) => item);
});
const place = i => { const [left, top, width, tilt, layer] = LAYOUTS[picks.value.length - 1][i]; return { left: `${left}%`, top: `${top}%`, width: `${width}%`, zIndex: layer, '--tilt': `${tilt}deg`, '--wait': `${i * 70}ms` }; };
</script>
<template>
 <div class="collage">
  <img v-for="(s, i) in picks" :key="s.id" class="piece" :src="sized(s.sample, CARD)" alt="" width="640" height="800" loading="lazy" :style="place(i)" @error="$emit('broken', s.id)">
 </div>
</template>
<style scoped>
.collage{position:relative;width:100%;height:100%}
.collage::before{content:"";position:absolute;inset:6%;border-radius:50%;background:radial-gradient(closest-side,var(--accent-soft),transparent)}
.piece{position:absolute;height:auto;aspect-ratio:4/5;object-fit:cover;border-radius:var(--r);border:1px solid var(--line2);background:var(--s3);box-shadow:var(--shadow);transform:rotate(var(--tilt))}
@media (prefers-reduced-motion:no-preference){
 .piece{animation:settle .55s cubic-bezier(.2,.7,.2,1) var(--wait) backwards}
 @keyframes settle{from{opacity:0;transform:translateY(12px) rotate(var(--tilt)) scale(.97)}}
}
</style>
