<script setup>
import { Search } from 'lucide-vue-next';
import PrefControls from './PrefControls.vue';
import { t, searchOpen } from '../core';
// The bar above every page on a wide screen: what the page itself puts here (a chat puts its title and actions), search, and the language and theme.
// Phones have MobileBar instead.
const mac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);
const open = () => { searchOpen.value = true; };
</script>
<template>
 <header class="topbar">
  <div id="topbar-page" class="page"></div>
  <button type="button" class="search" @click="open"><Search/><span>{{ t('shSearchChats') }}</span><kbd>{{ mac ? '⌘' : 'Ctrl' }} K</kbd></button>
  <PrefControls/>
 </header>
</template>
<style scoped>
.topbar{position:sticky;top:0;z-index:16;display:flex;align-items:center;gap:12px;min-height:60px;padding:8px 24px;background:color-mix(in srgb,var(--bg) 88%,transparent);backdrop-filter:blur(10px);-webkit-backdrop-filter:blur(10px);border-bottom:1px solid var(--line)}
.page{flex:1;min-width:0;display:flex;align-items:center}
.search{display:flex;align-items:center;gap:8px;height:36px;padding:0 8px 0 12px;border-radius:var(--r2);border:1px solid var(--line);background:var(--s1);color:var(--faint);font-size:13px;min-width:200px}
:root[dir=rtl] .search{padding:0 12px 0 8px}
.search:hover{border-color:var(--line2);color:var(--muted)}
.search span{flex:1;text-align:start}
.search svg{width:16px;height:16px}
kbd{padding:0 6px;border-radius:5px;border:1px solid var(--line2);background:var(--s2);font:inherit;font-size:11px;line-height:20px;color:var(--muted);direction:ltr}
@media (max-width:1100px){.search{min-width:0}.search span{display:none}}
@media (max-width:860px){.topbar{display:none}}
</style>
