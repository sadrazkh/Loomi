<script setup>
import { Languages } from 'lucide-vue-next';
import { t, lang, theme } from '../core';
// Language and theme, one click each, wherever the frame shows them: the top bar on wide screens, the phone header's panel, the account menu.
defineProps({ stacked: Boolean });
const LANGS = [['fa', 'فارسی', 'فا'], ['en', 'English', 'EN']];
const THEMES = [['dark', '🌙', 'shDark'], ['light', '☀️', 'shLight']];
const setLang = v => { lang.value = v; };
const setTheme = v => { theme.value = v; };
</script>
<template>
 <div class="prefs" :class="{ stacked }">
  <div class="group">
   <span v-if="stacked" class="label">{{ t('shLanguage') }}</span>
   <div class="seg" role="group" :aria-label="t('shLanguage')">
    <Languages v-if="!stacked" class="lead" aria-hidden="true"/>
    <button v-for="[key, name, badge] in LANGS" :key="key" type="button" :lang="key" :aria-pressed="lang===key" @click="setLang(key)"><span class="badge" aria-hidden="true">{{ badge }}</span>{{ name }}</button>
   </div>
  </div>
  <div class="group">
   <span v-if="stacked" class="label">{{ t('shTheme') }}</span>
   <div class="seg" role="group" :aria-label="t('shTheme')">
    <button v-for="[key, emoji, label] in THEMES" :key="key" type="button" :aria-pressed="theme===key" @click="setTheme(key)"><span class="emoji" aria-hidden="true">{{ emoji }}</span>{{ t(label) }}</button>
   </div>
  </div>
 </div>
</template>
<style scoped>
.prefs{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.prefs.stacked{flex-direction:column;align-items:stretch;gap:14px}
.group{display:flex;flex-direction:column;gap:6px}
.label{font-size:12px;color:var(--faint)}
.seg{align-items:center}
.stacked .seg{display:flex}
.stacked .seg>button{flex:1;justify-content:center;height:34px}
.lead{width:16px;height:16px;color:var(--faint);margin-inline:6px 2px}
.badge{display:inline-grid;place-items:center;min-width:22px;height:18px;padding:0 4px;border-radius:5px;background:var(--s3);color:var(--muted);font-size:10.5px;font-weight:700;letter-spacing:.02em}
.seg>button[aria-pressed=true] .badge{background:var(--accent);color:var(--accent-ink)}
.emoji{font-size:14px;line-height:1;font-family:'Segoe UI Emoji','Apple Color Emoji','Noto Color Emoji',sans-serif}
</style>
