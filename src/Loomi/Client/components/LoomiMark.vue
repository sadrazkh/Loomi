<script setup>
// Warp and weft: three threads down, one across in saffron, passing under the middle one. A loom's own mark, for an app named after one.
defineProps({ size: { type: Number, default: 22 } });
</script>
<template>
 <svg class="loomi-mark" :width="size" :height="size" viewBox="0 0 24 24" fill="none" stroke-linecap="round" aria-hidden="true">
  <path d="M7 4v16M12 4v5M12 13v7M17 4v16" stroke="currentColor" stroke-width="2.2"/>
  <path d="M3.5 11h1.5M9 11h6M19 11h1.5" stroke="var(--accent)" stroke-width="3.4"/>
 </svg>
</template>
