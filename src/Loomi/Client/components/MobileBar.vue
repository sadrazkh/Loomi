<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import { Plus, MessagesSquare, LayoutTemplate, UserRound, LayoutDashboard, Infinity as InfinityIcon, CalendarDays, Coins } from 'lucide-vue-next';
import LoomiMark from './LoomiMark.vue';
import PrefControls from './PrefControls.vue';
import { t, n, me, isOwner, ready, view, theme, activePackages, daysLeft, tf, go } from '../core';
// On a phone the side menu becomes a header and four buttons at the bottom. Inside a chat both step aside: the chat has its own header and composer.
// The header's moon or sun opens language and theme, the same controls the wide screen's top bar has.
const TABS = [['home', Plus, 'shTabNew'], ['chats', MessagesSquare, 'shTabChats'], ['templates', LayoutTemplate, 'shTabTemplates'], ['account', UserRound, 'shTabAccount']];
const KIND_ICON = { Unlimited: InfinityIcon, Daily: CalendarDays, Credits: Coins };
const pkg = computed(() => activePackages.value[0] || null);
const active = key => view.value === key || (key === 'account' && view.value === 'gallery');
const prefs = ref(false), prefsRoot = ref(null);
function close(e) { if (prefs.value && !prefsRoot.value?.contains(e.target)) prefs.value = false; }
onMounted(() => document.addEventListener('mousedown', close));
onBeforeUnmount(() => document.removeEventListener('mousedown', close));
</script>
<template>
 <header v-if="view!=='chat'" class="mhead">
  <button class="brand" @click="go('home')"><span class="mark"><LoomiMark :size="20"/></span>Loomi</button>
  <span class="grow"/>
  <button v-if="pkg" class="pill" @click="go('account')"><component :is="KIND_ICON[pkg.kind]"/>{{ tf('shDaysLeft', daysLeft(pkg)) }}</button>
  <button v-else-if="me?.credits!=null" class="pill" @click="go('account')"><Coins/>{{ n(me.credits) }}</button>
  <button v-if="isOwner" class="btn btn-sm" :class="view==='admin'?'btn-primary':''" @click="go('admin')"><LayoutDashboard/>{{ t('shAdmin') }}<i v-if="!ready" class="dot"></i></button>
  <div ref="prefsRoot" class="prefs">
   <button class="btn btn-ghost btn-icon btn-sm emoji" :aria-label="t('shDisplay')" aria-haspopup="dialog" :aria-expanded="prefs" @click="prefs=!prefs">{{ theme==='dark' ? '🌙' : '☀️' }}</button>
   <div v-if="prefs" class="panel" role="dialog" :aria-label="t('shDisplay')"><PrefControls stacked/></div>
  </div>
 </header>
 <nav v-if="view!=='chat'" class="mtabs" :aria-label="t('shChats')">
  <button v-for="[key, icon, label] in TABS" :key="key" :aria-current="active(key)?'page':null" @click="go(key)"><component :is="icon"/><span>{{ t(label) }}</span></button>
 </nav>
</template>
<style scoped>
.mhead,.mtabs{display:none}
@media (max-width:860px){
 .mhead{display:flex;align-items:center;gap:8px;position:sticky;top:0;z-index:15;height:calc(56px + env(safe-area-inset-top,0px));padding:env(safe-area-inset-top,0px) max(16px,env(safe-area-inset-right,0px)) 0 max(16px,env(safe-area-inset-left,0px));background:color-mix(in srgb,var(--bg) 88%,transparent);backdrop-filter:blur(10px);border-bottom:1px solid var(--line)}
 .brand{display:flex;align-items:center;gap:8px;background:none;border:0;font-weight:700;font-size:17px;color:var(--text);padding:0}
 .mark{width:30px;height:30px;border-radius:var(--r2);background:var(--s3);display:grid;place-items:center}
 .pill{border:0}
 .btn{position:relative}
 .dot{position:absolute;top:4px;inset-inline-end:4px;width:8px;height:8px;border-radius:50%;background:var(--warn)}
 .prefs{position:relative}
 .emoji{font-size:17px;font-family:'Segoe UI Emoji','Apple Color Emoji','Noto Color Emoji',sans-serif}
 .panel{position:absolute;top:calc(100% + 8px);inset-inline-end:0;z-index:30;width:min(300px,calc(100vw - 32px));padding:14px;border-radius:var(--r);background:var(--s1);border:1px solid var(--line2);box-shadow:var(--shadow)}
 .mtabs{display:grid;grid-template-columns:repeat(4,1fr);position:fixed;inset-inline:0;bottom:0;z-index:15;padding:6px max(8px,env(safe-area-inset-right,0px)) calc(6px + env(safe-area-inset-bottom,0px)) max(8px,env(safe-area-inset-left,0px));background:var(--s1);border-top:1px solid var(--line)}
 .mtabs button{display:flex;flex-direction:column;align-items:center;gap:2px;padding:6px 0;border:0;background:none;color:var(--muted);font-size:11.5px}
 .mtabs button svg{width:22px;height:22px}
 .mtabs button[aria-current=page]{color:var(--accent);font-weight:600}
}
</style>
