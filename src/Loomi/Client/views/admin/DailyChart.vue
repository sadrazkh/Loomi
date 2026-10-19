<script setup>
import { ref, computed } from 'vue';
import { t, tf, n, locale } from '../../core';
// Requests per day, oldest first, today last and in full colour. Columns grow from one baseline to scale against a rounded top; the failed share
// of each day sits on top of it in the alert colour. Hovering a day shows its figures; a table carries the same numbers for screen readers.
const props = defineProps({ days: { type: Array, required: true } });
const hover = ref(null);
// The server's day is a local calendar date, so it is read as one: parsed as UTC it would fall on the day before west of Greenwich.
const day = s => { const [y, m, d] = s.split('-').map(Number); return new Date(y, m - 1, d); };
const short = s => day(s).toLocaleDateString(locale.value, { day: 'numeric', month: 'short' });
const long = s => day(s).toLocaleDateString(locale.value, { weekday: 'long', day: 'numeric', month: 'long' });
/** The smallest of 2, 4, 6 or 10 times a power of ten that holds the busiest day, so the middle line is a whole, round number too. */
const top = computed(() => {
 const most = Math.max(0, ...props.days.map(d => d.requests));
 for (let p = 1; ; p *= 10) for (const m of [2, 4, 6, 10]) if (m * p >= most) return m * p;
});
const ticks = computed(() => [top.value, top.value / 2, 0]);
const last = computed(() => props.days.length - 1);
const height = v => (v / top.value * 100) + '%';
// The x-axis names the first day, the middle one and today; the rest are in the hover and the table.
const labelled = i => i === 0 || i === last.value || i === Math.floor(last.value / 2);
const edge = i => i < 2 ? 'start' : i > last.value - 2 ? 'end' : 'mid';
</script>

<template>
 <div class="chart">
  <div class="legend" aria-hidden="true"><span><i class="sw ok"></i>{{ t('ovLegendDone') }}</span><span><i class="sw bad"></i>{{ t('usageFailed') }}</span></div>
  <div class="frame" aria-hidden="true">
   <div class="yax"><span v-for="v in ticks" :key="v" :style="{ bottom: height(v) }">{{ n(v) }}</span></div>
   <div class="plot">
    <i v-for="v in ticks" :key="v" class="line" :style="{ bottom: height(v) }"></i>
    <div class="cols">
     <div v-for="(d, i) in days" :key="d.date" class="slot" :class="{ today: i === last, on: hover === i }" @mouseenter="hover = i" @mouseleave="hover = null">
      <span class="col" :style="{ height: height(d.requests) }">
       <b v-if="i === last" class="cap num">{{ n(d.requests) }}</b>
       <i v-if="d.failed" class="seg-bad" :style="{ flexGrow: d.failed }"></i>
       <i v-if="d.requests - d.failed > 0" class="seg-ok" :style="{ flexGrow: d.requests - d.failed }"></i>
      </span>
      <div v-if="hover === i" class="tip" :class="edge(i)" :style="{ bottom: `calc(${height(d.requests)} + 10px)` }">
       <strong>{{ i === last ? t('shToday') : long(d.date) }}</strong>
       <span>{{ tf('ovTipRequests', d.requests) }}</span>
       <span>{{ tf('ovTipImages', d.images) }}</span>
       <span v-if="d.failed" class="tip-bad"><i class="sw bad"></i>{{ tf('ovTipFailed', d.failed) }}</span>
      </div>
     </div>
    </div>
   </div>
   <span></span>
   <div class="xax"><span v-for="(d, i) in days" :key="d.date">{{ labelled(i) ? (i === last ? t('shToday') : short(d.date)) : '' }}</span></div>
  </div>
  <table class="sr-only">
   <caption>{{ t('ovChartTitle') }}</caption>
   <thead><tr><th scope="col">{{ t('ovDay') }}</th><th scope="col">{{ t('usageRequests') }}</th><th scope="col">{{ t('usageImagesCol') }}</th><th scope="col">{{ t('usageFailed') }}</th></tr></thead>
   <tbody><tr v-for="d in days" :key="d.date"><th scope="row">{{ long(d.date) }}</th><td>{{ n(d.requests) }}</td><td>{{ n(d.images) }}</td><td>{{ n(d.failed) }}</td></tr></tbody>
  </table>
 </div>
</template>

<style scoped>
.chart{display:flex;flex-direction:column;gap:10px;min-width:0}
.legend{display:flex;gap:14px;font-size:12px;color:var(--muted)}
.legend span{display:inline-flex;align-items:center;gap:6px}
.sw{width:10px;height:10px;border-radius:3px;display:inline-block}
.sw.ok{background:var(--accent)}
.sw.bad{background:var(--bad)}
.frame{display:grid;grid-template-columns:auto minmax(0,1fr);grid-template-rows:180px auto;column-gap:10px;row-gap:6px}
.yax{position:relative;min-width:22px;font-size:11px;color:var(--faint);font-variant-numeric:tabular-nums}
.yax span{position:absolute;inset-inline-end:0;transform:translateY(50%);line-height:1}
.plot{position:relative}
.line{position:absolute;inset-inline:0;height:1px;background:var(--line)}
.cols{position:absolute;inset:0;display:flex;align-items:stretch;gap:2px}
.slot{flex:1;position:relative;display:flex;align-items:flex-end;justify-content:center;min-width:0}
.slot.on{background:color-mix(in srgb,var(--s3) 55%,transparent);border-radius:6px 6px 0 0}
.col{position:relative;width:min(24px,72%);display:flex;flex-direction:column;gap:2px}
.col i{display:block;min-height:2px;flex-basis:0}
.col>i:first-of-type{border-radius:4px 4px 0 0}
.seg-ok{background:color-mix(in srgb,var(--accent) 42%,var(--s1))}
.seg-bad{background:color-mix(in srgb,var(--bad) 55%,var(--s1))}
.today .seg-ok{background:var(--accent)}
.today .seg-bad{background:var(--bad)}
.cap{position:absolute;bottom:100%;inset-inline:-12px;text-align:center;font-size:11.5px;font-weight:700;color:var(--text);padding-bottom:3px}
.xax{display:flex;gap:2px;font-size:11px;color:var(--faint)}
.xax span{flex:1;min-width:0;text-align:center;white-space:nowrap;overflow:visible;display:flex;justify-content:center}
.tip{position:absolute;z-index:3;display:flex;flex-direction:column;gap:1px;min-width:132px;padding:8px 10px;border-radius:var(--r2);background:var(--s1);border:1px solid var(--line2);box-shadow:var(--shadow-sm);font-size:12px;color:var(--muted);white-space:nowrap;pointer-events:none}
.tip strong{color:var(--text);font-size:12.5px;font-weight:600}
.tip.mid{left:50%;transform:translateX(-50%)}
.tip.start{inset-inline-start:0}
.tip.end{inset-inline-end:0}
.tip-bad{display:flex;align-items:center;gap:6px}
.tip-bad .sw{width:8px;height:8px}
</style>
