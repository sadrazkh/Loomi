<script setup>
import { computed } from 'vue';
import { t, tf, daysLeft } from '../../core';
import { KIND_ICON, pkgName, pkgDetail, pkgLeft, endingSoon, dayMonth, days } from './kit';
// One package as a row: its kind, its name, what is left of it and when it ends. My account and the user drawer both use it; the drawer adds its actions in the slot.
const props = defineProps({ pkg: { type: Object, required: true } });
const p = computed(() => props.pkg);
const left = computed(() => Math.round(pkgLeft(p.value) * 100));
</script>

<template>
 <div class="pkg" :class="{ old: p.state !== 'Active', soon: endingSoon(p) }">
  <span class="ic" aria-hidden="true"><component :is="KIND_ICON[p.kind]"/></span>
  <div class="body">
   <div class="title"><b><bdi>{{ pkgName(p) }}</bdi></b><small>{{ pkgDetail(p) }}</small></div>
   <div v-if="p.state === 'Active'" class="bar" :class="{ 'bar-warn': endingSoon(p) }" role="meter" aria-valuemin="0" aria-valuemax="100" :aria-valuenow="left" :aria-label="t('pkLeftLabel')"><i :style="{ width: left + '%' }"></i></div>
  </div>
  <div class="end num">
   <template v-if="p.state === 'Active'"><b>{{ days(daysLeft(p)) }}</b>{{ tf('pkUntil', dayMonth(p.endsAt)) }}</template>
   <template v-else-if="p.state === 'Scheduled'"><b>{{ dayMonth(p.startsAt) }}</b>{{ t('pkStartLabel') }}</template>
   <template v-else><b>{{ dayMonth(p.endsAt) }}</b>{{ t('pkEndLabel') }}</template>
  </div>
  <div v-if="$slots.default" class="acts"><slot/></div>
 </div>
</template>

<style scoped>
.pkg{display:grid;grid-template-columns:38px minmax(0,1fr) auto;gap:6px 12px;align-items:center}
.ic{width:38px;height:38px;border-radius:var(--r2);background:var(--accent-soft);color:var(--accent);display:grid;place-items:center}
.ic svg{width:20px;height:20px}
.soon .ic{background:var(--warn-soft);color:var(--warn)}
.body{display:flex;flex-direction:column;gap:6px;min-width:0}
.title{display:flex;flex-wrap:wrap;align-items:baseline;gap:2px 8px;min-width:0}
.title b{font-size:14px;font-weight:600;overflow-wrap:anywhere}
.title small{font-size:12.5px;color:var(--muted)}
.end{font-size:12.5px;color:var(--muted);text-align:end;line-height:1.5;white-space:nowrap}
.end b{display:block;color:var(--text);font-size:14px;font-weight:600}
.old>:not(.acts){opacity:.55}
.old .ic{background:var(--s3);color:var(--muted)}
.acts{grid-column:2/-1;display:flex;flex-wrap:wrap;gap:6px;align-items:center}
</style>
