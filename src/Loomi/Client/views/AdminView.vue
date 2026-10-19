<script setup>
import { ref, watch, nextTick, onMounted } from 'vue';
import { LayoutDashboard, Users, MonitorSmartphone, LayoutTemplate, Tag, Bot } from 'lucide-vue-next';
import TemplatesView from '../components/TemplatesView.vue';
import OverviewPage from './admin/OverviewPage.vue';
import UsersPage from './admin/UsersPage.vue';
import AccountsPage from './admin/AccountsPage.vue';
import PricingPage from './admin/PricingPage.vue';
import BotPage from './admin/BotPage.vue';
import { t, section, ready, go } from '../core';
// Management, for the owner: the page for the section in the address. On a wide screen the side menu switches sections; on a phone it is hidden,
// so the page carries its own row of the six, scrolled so the open one is in view.
const SECTIONS = [['overview', LayoutDashboard, 'shOverview'], ['users', Users, 'shUsers'], ['accounts', MonitorSmartphone, 'shAccounts'], ['templates', LayoutTemplate, 'shAdminTemplates'], ['pricing', Tag, 'shPricing'], ['bot', Bot, 'shBot']];
const strip = ref(null);
async function reveal() { await nextTick(); strip.value?.querySelector('[aria-current=page]')?.scrollIntoView({ block: 'nearest', inline: 'center' }); }
onMounted(reveal);
watch(section, reveal);
</script>

<template>
 <div class="admin">
  <nav ref="strip" class="switch" :aria-label="t('shAdmin')">
   <div class="seg">
    <button v-for="[key, icon, label] in SECTIONS" :key="key" :aria-current="section===key ? 'page' : null" @click="go('admin', key)"><component :is="icon"/>{{ t(label) }}<i v-if="key==='accounts' && !ready" class="dot" aria-hidden="true"></i></button>
   </div>
  </nav>
  <OverviewPage v-if="section==='overview'"/>
  <UsersPage v-else-if="section==='users'"/>
  <AccountsPage v-else-if="section==='accounts'"/>
  <div v-else-if="section==='templates'" class="view">
   <header class="view-head"><div><h1>{{ t('shAdminTemplates') }}</h1><p>{{ t('adTemplatesHelp') }}</p></div></header>
   <TemplatesView manage/>
  </div>
  <PricingPage v-else-if="section==='pricing'"/>
  <BotPage v-else-if="section==='bot'"/>
 </div>
</template>

<style scoped>
.admin{min-width:0}
.switch{display:none}
@media (max-width:860px){
 .switch{display:block;overflow-x:auto;padding:14px 16px 0;scrollbar-width:none}
 .switch::-webkit-scrollbar{display:none}
 .seg{display:inline-flex;flex-wrap:nowrap}
 .seg>button{position:relative;height:34px}
 .seg>button[aria-current=page]{background:var(--s1);color:var(--text);font-weight:600;box-shadow:var(--shadow-sm)}
 :root[data-theme=dark] .seg>button[aria-current=page]{background:var(--s3)}
 .seg>button[aria-current=page] svg{color:var(--accent)}
 .dot{width:7px;height:7px;border-radius:50%;background:var(--warn)}
}
</style>
