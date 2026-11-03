<script setup>
import { ref, watch } from 'vue';
import { LoaderCircle, LogIn, RefreshCw, TimerOff } from 'lucide-vue-next';
import LoomiMark from '../components/LoomiMark.vue';
import { t, lang, initialized, loginWithLink } from '../core';
// Coming into the web through the bot's one-time link. The code sits in the address's fragment, so it is read from there and spent in one request.
const code = location.hash.slice('#/link/'.length);
const state = ref('working'), problem = ref('');
async function enter() {
 // The server's 401 for a link that is no good reaches here as SessionExpired, the way any 401 does. Anything else says nothing about the link, which may well still work.
 try { await loginWithLink(code); }
 catch (e) { problem.value = e.message === 'SessionExpired' ? '' : e.message; state.value = 'failed'; }
}
// The request carries the CSRF token that the start-up's first look at the session brings, so it waits for that.
watch(initialized, ready => { if (ready) enter(); }, { immediate: true });
const reload = () => location.reload();
const toggleLang = () => { lang.value = lang.value === 'fa' ? 'en' : 'fa'; };
</script>
<template>
 <main class="login">
  <div class="panel">
   <div class="head"><span class="mark"><LoomiMark :size="30"/></span><span class="word">Loomi</span><span class="grow"/><button type="button" class="btn btn-ghost btn-sm" @click="toggleLang">{{ lang==='fa'?'English':'فارسی' }}</button></div>
   <p v-if="state === 'working'" class="working" role="status"><LoaderCircle class="spin"/><span>{{ t('suSigningIn') }}</span></p>
   <template v-else-if="problem">
    <p class="notice notice-warn" role="alert"><TimerOff/><span>{{ t(problem) }}</span></p>
    <button type="button" class="btn btn-primary btn-lg btn-block" @click="reload"><RefreshCw/>{{ t('suTryAgain') }}</button>
   </template>
   <template v-else>
    <div class="intro" role="alert"><h1>{{ t('suLinkBadTitle') }}</h1><p>{{ t('suLinkBad') }}</p></div>
    <a class="btn btn-lg btn-block" href="#/login"><LogIn/>{{ t('suToLogin') }}</a>
   </template>
  </div>
 </main>
</template>
<style scoped>
.login{min-height:100vh;display:grid;place-items:center;padding:32px max(16px,env(safe-area-inset-right,0px)) 32px max(16px,env(safe-area-inset-left,0px));background:radial-gradient(60rem 30rem at 50% -10%,var(--accent-soft),transparent 70%),var(--bg)}
.panel{width:min(400px,100%);display:flex;flex-direction:column;gap:16px;padding:28px;border-radius:var(--r3);background:var(--s1);border:1px solid var(--line);box-shadow:var(--shadow)}
.head{display:flex;align-items:center;gap:10px}
.mark{width:44px;height:44px;border-radius:12px;background:var(--s3);display:grid;place-items:center}
.word{font-size:22px;font-weight:800;letter-spacing:.01em}
.working{display:flex;align-items:center;gap:10px;padding:14px 0;color:var(--muted)}
.intro{display:flex;flex-direction:column;gap:6px;margin-top:6px}
.intro h1{font-size:24px}
.intro p{color:var(--muted)}
</style>
