<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import { Plus, MessagesSquare, Search, LayoutTemplate, Images, LayoutDashboard, Users, MonitorSmartphone, Tag, Globe, Bot, Ellipsis, UserRound, LogOut, Infinity as InfinityIcon, CalendarDays, Coins } from 'lucide-vue-next';
import LoomiMark from './LoomiMark.vue';
import { t, tf, n, me, isOwner, ready, view, section, projects, activePackages, packageLabel, searchOpen, go, logout, action } from '../core';
// The side menu on wide screens: making, finding, management for the owner, and the wallet. It holds no chat list of its own: a column of titles
// squeezed under management read badly and scrolled in its own little box, so chats have their own page and the search box over the page.
// Phones use MobileBar instead.
const menu = ref(false), menuRoot = ref(null);
const ADMIN = [['overview', LayoutDashboard, 'shOverview'], ['users', Users, 'shUsers'], ['accounts', MonitorSmartphone, 'shAccounts'], ['templates', LayoutTemplate, 'shAdminTemplates'], ['pricing', Tag, 'shPricing'], ['site', Globe, 'shSite'], ['bot', Bot, 'shBot']];
const KIND_ICON = { Unlimited: InfinityIcon, Daily: CalendarDays, Credits: Coins };
const pkg = computed(() => activePackages.value[0] || null);
const initial = computed(() => (me.value?.username || '?').slice(0, 1));
const mac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);
const openSearch = () => { searchOpen.value = true; };
function close(e) { if (menu.value && !menuRoot.value?.contains(e.target)) menu.value = false; }
onMounted(() => document.addEventListener('mousedown', close));
onBeforeUnmount(() => document.removeEventListener('mousedown', close));
</script>
<template>
 <aside class="side" :aria-label="t('shAdmin')">
  <div class="head">
   <button class="brand" @click="go('home')"><span class="mark"><LoomiMark/></span><span>Loomi</span></button>
   <button class="btn btn-primary btn-lg btn-block" @click="go('home')"><Plus/>{{ t('shNewImage') }}</button>
  </div>
  <div class="body">
   <nav class="nav">
    <button class="item" :aria-current="view==='chats'||view==='chat'?'page':null" @click="go('chats')"><MessagesSquare/>{{ t('shAllChats') }}<span v-if="projects.length" class="count num">{{ n(projects.length) }}</span></button>
    <button class="item" @click="openSearch"><Search/>{{ t('shSearchChats') }}<kbd>{{ mac ? '⌘' : 'Ctrl' }} K</kbd></button>
    <button class="item" :aria-current="view==='templates'?'page':null" @click="go('templates')"><LayoutTemplate/>{{ t('shTemplates') }}</button>
    <button class="item" :aria-current="view==='gallery'?'page':null" @click="go('gallery')"><Images/>{{ t('shMyImages') }}</button>
   </nav>
   <nav v-if="isOwner" class="nav" :aria-label="t('shAdmin')">
    <span class="label">{{ t('shAdmin') }}</span>
    <button v-for="[key, icon, label] in ADMIN" :key="key" class="item" :aria-current="view==='admin'&&section===key?'page':null" @click="go('admin', key)"><component :is="icon"/>{{ t(label) }}<i v-if="key==='accounts'&&!ready" class="dot" :title="t('shServiceDownOwner')"></i></button>
   </nav>
  </div>
  <div class="foot">
   <button v-if="me?.credits!=null" class="wallet" @click="go('account')">
    <span class="row"><small>{{ t('shCredits') }}</small><strong class="num">{{ n(me.credits) }}</strong></span>
    <span v-if="pkg" class="row"><span class="pill"><component :is="KIND_ICON[pkg.kind]"/>{{ packageLabel(pkg) }}</span><small v-if="pkg.kind!=='Credits'" class="num">{{ pkg.amount ? tf('shTodayOf', pkg.usedToday, pkg.amount) : tf('shTodayCount', pkg.usedToday) }}</small></span>
   </button>
   <div ref="menuRoot" class="me">
    <button class="who" :aria-label="t('shMenu')" aria-haspopup="menu" :aria-expanded="menu" @click="menu=!menu"><span class="av">{{ initial }}</span><span class="name"><strong dir="auto" class="truncate">{{ me?.username }}</strong><small>{{ t(isOwner?'shOwner':'shMember') }}</small></span><Ellipsis/></button>
    <div v-if="menu" class="menu" role="menu">
     <button role="menuitem" @click="menu=false;go('account')"><UserRound/>{{ t('shAccount') }}</button>
     <button role="menuitem" class="danger" @click="menu=false;action(logout)"><LogOut/>{{ t('shLogout') }}</button>
    </div>
   </div>
  </div>
 </aside>
</template>
<style scoped>
.side{position:sticky;top:0;height:100vh;display:flex;flex-direction:column;background:var(--s1);border-inline-end:1px solid var(--line);min-height:0}
.head{display:flex;flex-direction:column;gap:14px;padding:18px 14px 12px;flex:none}
.brand{display:flex;align-items:center;gap:10px;padding:0 6px;background:none;border:0;font-weight:700;font-size:19px;letter-spacing:.01em;color:var(--text);align-self:flex-start}
.mark{width:32px;height:32px;border-radius:var(--r2);background:var(--s3);display:grid;place-items:center}
/* One scrolling middle, its bar shown only while the pointer is over it, instead of a small box scrolling inside the menu. */
.body{flex:1;min-height:0;overflow-y:auto;display:flex;flex-direction:column;gap:14px;padding:2px 14px 14px;scrollbar-color:transparent transparent}
.body:hover{scrollbar-color:var(--line2) transparent}
.nav{display:flex;flex-direction:column;gap:2px}
.label{display:block;font-size:12px;color:var(--faint);padding:8px 10px 2px}
.item{display:flex;align-items:center;gap:10px;height:38px;padding:0 10px;border-radius:var(--r2);border:0;background:transparent;color:var(--muted);font-size:13.5px;text-align:start;width:100%}
.item:hover{background:var(--s2);color:var(--text)}
.item[aria-current=page]{background:var(--accent-soft);color:var(--text);font-weight:600}
.item[aria-current=page] svg{color:var(--accent)}
.count{margin-inline-start:auto;font-size:12px;color:var(--faint);font-weight:400}
.item kbd{margin-inline-start:auto;padding:0 6px;border-radius:5px;border:1px solid var(--line);font:inherit;font-size:11px;line-height:19px;color:var(--faint);direction:ltr}
.dot{margin-inline-start:auto;width:8px;height:8px;border-radius:50%;background:var(--warn)}
.foot{display:flex;flex-direction:column;gap:10px;padding:12px 14px 14px;border-top:1px solid var(--line);flex:none}
.wallet{display:flex;flex-direction:column;gap:8px;padding:12px;border-radius:var(--r);border:1px solid var(--line);background:var(--s2);text-align:start}
.wallet:hover{border-color:var(--line2)}
.wallet .row{display:flex;align-items:center;justify-content:space-between;gap:8px}
.wallet small{color:var(--muted);font-size:12px}
.wallet strong{font-size:20px}
.me{position:relative}
.who{display:flex;align-items:center;gap:10px;width:100%;padding:4px;border-radius:var(--r2);border:0;background:transparent;text-align:start}
.who:hover{background:var(--s2)}
.name{flex:1;min-width:0;display:flex;flex-direction:column;line-height:1.35}
.name small{color:var(--faint);font-size:12px}
.menu{position:absolute;bottom:calc(100% + 6px);inset-inline:0;z-index:20;display:flex;flex-direction:column;padding:6px;border-radius:var(--r);background:var(--s1);border:1px solid var(--line2);box-shadow:var(--shadow)}
.menu button{display:flex;align-items:center;gap:10px;height:38px;padding:0 10px;border:0;border-radius:var(--r2);background:transparent;font-size:13.5px;text-align:start}
.menu button:hover{background:var(--s2)}
.menu .danger{color:var(--bad)}
</style>
