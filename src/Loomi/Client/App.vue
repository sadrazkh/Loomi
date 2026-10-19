<script setup>
import { onMounted } from 'vue';
import { X, AlertTriangle } from 'lucide-vue-next';
import AppSidebar from './components/AppSidebar.vue';
import MobileBar from './components/MobileBar.vue';
import LoginView from './views/LoginView.vue';
import HomeView from './views/HomeView.vue';
import ChatView from './views/ChatView.vue';
import ChatsView from './views/ChatsView.vue';
import GalleryView from './views/GalleryView.vue';
import AccountView from './views/AccountView.vue';
import AdminView from './views/AdminView.vue';
import TemplatesView from './components/TemplatesView.vue';
import { t, authenticated, initialized, error, isOwner, ready, statusKnown, view, go, boot } from './core';
// The frame only: which page shows, the side menu or the phone bars around it, and the one place errors surface. Each page owns its own markup and state.
onMounted(boot);
const dismiss = () => { error.value = ''; };
</script>

<template>
 <LoginView v-if="!authenticated"/>
 <div v-else class="shell" :class="{ 'in-chat': view==='chat' }">
  <AppSidebar/>
  <div class="stage">
   <MobileBar/>
   <p v-if="!ready && statusKnown" class="service" role="status">
    <AlertTriangle/><span>{{ t(isOwner ? 'shServiceDownOwner' : 'shServiceDown') }}</span>
    <button v-if="isOwner" class="btn btn-sm" @click="go('admin','accounts')">{{ t('shOpenAccounts') }}</button>
   </p>
   <HomeView v-if="view==='home'"/>
   <ChatView v-else-if="view==='chat'"/>
   <ChatsView v-else-if="view==='chats'"/>
   <TemplatesView v-else-if="view==='templates'"/>
   <GalleryView v-else-if="view==='gallery'"/>
   <AccountView v-else-if="view==='account'"/>
   <AdminView v-else-if="view==='admin' && isOwner"/>
  </div>
  <div v-if="error" class="toast" role="alert"><AlertTriangle/><span>{{ t(error) }}</span><button class="btn btn-ghost btn-icon btn-sm" :aria-label="t('shDismiss')" @click="dismiss"><X/></button></div>
 </div>
</template>

<style scoped>
.shell{display:grid;grid-template-columns:272px minmax(0,1fr);min-height:100vh}
.stage{min-width:0;display:flex;flex-direction:column}
.service{display:flex;align-items:center;gap:10px;margin:16px 32px 0;padding:10px 14px;border-radius:var(--r2);background:var(--warn-soft);font-size:13.5px}
.service svg{color:var(--warn)}
.service span{flex:1}
.toast{position:fixed;z-index:60;bottom:24px;inset-inline-start:50%;transform:translateX(-50%);display:flex;align-items:center;gap:10px;max-width:min(560px,calc(100vw - 32px));padding:10px 10px 10px 16px;border-radius:var(--r);background:var(--s1);border:1px solid color-mix(in srgb,var(--bad) 45%,var(--line));box-shadow:var(--shadow);font-size:13.5px}
:root[dir=rtl] .toast{transform:translateX(50%);padding:10px 16px 10px 10px}
.toast>svg{color:var(--bad)}
.toast span{flex:1}
@media (max-width:860px){
 .shell{display:block}
 .shell :deep(.side){display:none}
 .service{margin:12px 16px 0}
 .toast{bottom:calc(84px + env(safe-area-inset-bottom,0px))}
 .in-chat .toast{bottom:calc(120px + env(safe-area-inset-bottom,0px))}
}
</style>
