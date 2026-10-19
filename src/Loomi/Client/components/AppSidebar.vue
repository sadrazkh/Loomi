<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import { Plus, LayoutTemplate, Images, LayoutDashboard, Users, MonitorSmartphone, Tag, Bot, Search, Ellipsis, UserRound, Moon, Sun, LogOut, Languages, Infinity as InfinityIcon, CalendarDays, Coins } from 'lucide-vue-next';
import LoomiMark from './LoomiMark.vue';
import { t, tf, n, lang, theme, me, isOwner, ready, view, section, current, projects, activePackages, packageLabel, dayGroup, go, logout, action } from '../core';
// The side menu on wide screens: making, the chat list grouped by day, management for the owner, and the wallet. Phones use MobileBar instead.
const query = ref(''), menu = ref(false), menuRoot = ref(null);
const GROUPS = ['today', 'yesterday', 'thisWeek', 'earlier'], GROUP_KEYS = { today: 'shToday', yesterday: 'shYesterday', thisWeek: 'shThisWeek', earlier: 'shEarlier' };
const groups = computed(() => {
  const q = query.value.trim().toLocaleLowerCase();
  const list = projects.value.filter(p => !q || p.title.toLocaleLowerCase().includes(q));
  return GROUPS.map(g => ({ key: g, items: list.filter(p => dayGroup(p.updatedAt) === g) })).filter(g => g.items.length);
});
const ADMIN = [['overview', LayoutDashboard, 'shOverview'], ['users', Users, 'shUsers'], ['accounts', MonitorSmartphone, 'shAccounts'], ['templates', LayoutTemplate, 'shAdminTemplates'], ['pricing', Tag, 'shPricing'], ['bot', Bot, 'shBot']];
const KIND_ICON = { Unlimited: InfinityIcon, Daily: CalendarDays, Credits: Coins };
const pkg = computed(() => activePackages.value[0] || null);
const initial = computed(() => (me.value?.username || '?').slice(0, 1));
const toggleLang = () => { lang.value = lang.value === 'fa' ? 'en' : 'fa'; };
const toggleTheme = () => { theme.value = theme.value === 'dark' ? 'light' : 'dark'; };
function close(e) { if (menu.value && !menuRoot.value?.contains(e.target)) menu.value = false; }
onMounted(() => document.addEventListener('mousedown', close));
onBeforeUnmount(() => document.removeEventListener('mousedown', close));
</script>
<template>
 <aside class="side" :aria-label="t('shChats')">
  <button class="brand" @click="go('home')"><span class="mark"><LoomiMark/></span><span>Loomi</span></button>
  <button class="btn btn-primary btn-lg btn-block" @click="go('home')"><Plus/>{{ t('shNewImage') }}</button>
  <nav class="nav">
   <button class="item" :aria-current="view==='templates'?'page':null" @click="go('templates')"><LayoutTemplate/>{{ t('shTemplates') }}</button>
   <button class="item" :aria-current="view==='gallery'?'page':null" @click="go('gallery')"><Images/>{{ t('shMyImages') }}</button>
  </nav>
  <nav v-if="isOwner" class="nav" :aria-label="t('shAdmin')">
   <span class="label">{{ t('shAdmin') }}</span>
   <button v-for="[key, icon, label] in ADMIN" :key="key" class="item" :aria-current="view==='admin'&&section===key?'page':null" @click="go('admin', key)"><component :is="icon"/>{{ t(label) }}<i v-if="key==='accounts'&&!ready" class="dot" :title="t('shServiceDownOwner')"></i></button>
  </nav>
  <div class="chats">
   <label class="search"><Search/><input v-model="query" type="search" :placeholder="t('shSearchChats')" :aria-label="t('shSearchChats')"></label>
   <div class="chat-scroll">
    <p v-if="!projects.length" class="empty">{{ t('shNoChats') }}</p>
    <p v-else-if="!groups.length" class="empty">{{ t('shNoMatch') }}</p>
    <template v-for="g in groups" :key="g.key">
     <span class="label">{{ t(GROUP_KEYS[g.key]) }}</span>
     <button v-for="p in g.items" :key="p.id" class="chat" :aria-current="view==='chat'&&current?.id===p.id?'page':null" @click="go('chat', p.id)">
      <span class="thumb"><img v-if="p.coverId" :src="`/api/generations/${p.coverId}/image`" alt="" loading="lazy"></span><span class="truncate" dir="auto">{{ p.title }}</span>
     </button>
    </template>
   </div>
  </div>
  <button v-if="me?.credits!=null" class="wallet" @click="go('account')">
   <span class="row"><small>{{ t('shCredits') }}</small><strong class="num">{{ n(me.credits) }}</strong></span>
   <span v-if="pkg" class="row"><span class="pill"><component :is="KIND_ICON[pkg.kind]"/>{{ packageLabel(pkg) }}</span><small v-if="pkg.kind!=='Credits'" class="num">{{ pkg.amount ? tf('shTodayOf', pkg.usedToday, pkg.amount) : tf('shTodayCount', pkg.usedToday) }}</small></span>
  </button>
  <div ref="menuRoot" class="me">
   <button class="who" :aria-label="t('shMenu')" aria-haspopup="menu" :aria-expanded="menu" @click="menu=!menu"><span class="av">{{ initial }}</span><span class="name"><strong dir="auto" class="truncate">{{ me?.username }}</strong><small>{{ t(isOwner?'shOwner':'shMember') }}</small></span><Ellipsis/></button>
   <div v-if="menu" class="menu" role="menu">
    <button role="menuitem" @click="menu=false;go('account')"><UserRound/>{{ t('shAccount') }}</button>
    <button role="menuitem" @click="toggleLang"><Languages/>{{ t('shLanguage') }}<span class="grow"/><span class="faint-text">{{ lang==='fa'?'فارسی':'English' }}</span></button>
    <button role="menuitem" @click="toggleTheme"><Moon v-if="theme==='dark'"/><Sun v-else/>{{ t('shTheme') }}<span class="grow"/><span class="faint-text">{{ t(theme==='dark'?'shDark':'shLight') }}</span></button>
    <button role="menuitem" class="danger" @click="menu=false;action(logout)"><LogOut/>{{ t('shLogout') }}</button>
   </div>
  </div>
 </aside>
</template>
<style scoped>
.side{position:sticky;top:0;height:100vh;display:flex;flex-direction:column;gap:14px;padding:18px 14px 14px;background:var(--s1);border-inline-end:1px solid var(--line);min-height:0}
.brand{display:flex;align-items:center;gap:10px;padding:0 6px;background:none;border:0;font-weight:700;font-size:19px;letter-spacing:.01em;color:var(--text);align-self:flex-start}
.mark{width:32px;height:32px;border-radius:var(--r2);background:var(--s3);display:grid;place-items:center}
.nav{display:flex;flex-direction:column;gap:2px}
.label{display:block;font-size:12px;color:var(--faint);padding:8px 10px 2px}
.item{display:flex;align-items:center;gap:10px;height:36px;padding:0 10px;border-radius:var(--r2);border:0;background:transparent;color:var(--muted);font-size:13.5px;text-align:start;width:100%}
.item:hover{background:var(--s2);color:var(--text)}
.item[aria-current=page]{background:var(--accent-soft);color:var(--text);font-weight:600}
.item[aria-current=page] svg{color:var(--accent)}
.dot{margin-inline-start:auto;width:8px;height:8px;border-radius:50%;background:var(--warn)}
.chats{flex:1;min-height:0;display:flex;flex-direction:column;gap:6px;border-top:1px solid var(--line);padding-top:12px}
.search{display:flex;align-items:center;gap:8px;height:34px;padding:0 10px;border-radius:var(--r2);background:var(--bg);border:1px solid var(--line);color:var(--faint)}
.search svg{width:16px;height:16px}
.search input{flex:1;min-width:0;border:0;background:none;outline:none;font-size:13px;color:var(--text)}
.search:focus-within{border-color:var(--accent-line)}
.chat-scroll{flex:1;min-height:0;overflow-y:auto;display:flex;flex-direction:column;gap:1px;margin-inline:-6px;padding-inline:6px}
.empty{color:var(--faint);font-size:13px;padding:8px 10px}
.chat{display:flex;align-items:center;gap:10px;min-height:40px;padding:0 8px;border-radius:var(--r2);border:0;background:transparent;font-size:13.5px;text-align:start;width:100%;flex:none}
.chat:hover{background:var(--s2)}
.chat[aria-current=page]{background:var(--s3)}
.thumb{width:28px;height:28px;border-radius:7px;background:var(--s3);overflow:hidden;flex:none}
.thumb img{width:100%;height:100%;object-fit:cover;display:block}
.wallet{display:flex;flex-direction:column;gap:8px;padding:12px;border-radius:var(--r);border:1px solid var(--line);background:var(--s2);text-align:start;flex:none}
.wallet:hover{border-color:var(--line2)}
.wallet .row{display:flex;align-items:center;justify-content:space-between;gap:8px}
.wallet small{color:var(--muted);font-size:12px}
.wallet strong{font-size:20px}
.me{position:relative;flex:none}
.who{display:flex;align-items:center;gap:10px;width:100%;padding:4px;border-radius:var(--r2);border:0;background:transparent;text-align:start}
.who:hover{background:var(--s2)}
.name{flex:1;min-width:0;display:flex;flex-direction:column;line-height:1.35}
.name small{color:var(--faint);font-size:12px}
.menu{position:absolute;bottom:calc(100% + 6px);inset-inline:0;z-index:20;display:flex;flex-direction:column;padding:6px;border-radius:var(--r);background:var(--s1);border:1px solid var(--line2);box-shadow:var(--shadow)}
.menu button{display:flex;align-items:center;gap:10px;height:38px;padding:0 10px;border:0;border-radius:var(--r2);background:transparent;font-size:13.5px;text-align:start}
.menu button:hover{background:var(--s2)}
.menu .danger{color:var(--bad)}
</style>
