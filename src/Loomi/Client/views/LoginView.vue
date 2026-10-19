<script setup>
import { ref, watch } from 'vue';
import { ShieldCheck, LogIn } from 'lucide-vue-next';
import LoomiMark from '../components/LoomiMark.vue';
import { t, lang, devKey, error, busy, initialized, action, login } from '../core';
const username = ref(''), password = ref('');
// Development only: the server names its built-in key, and the form fills itself so a local run is one click.
watch(devKey, key => { if (key && !password.value) { username.value = 'owner'; password.value = key; } }, { immediate: true });
const submit = () => action(async () => { await login(username.value, password.value); password.value = ''; });
const toggleLang = () => { lang.value = lang.value === 'fa' ? 'en' : 'fa'; };
</script>
<template>
 <main class="login">
  <form class="panel" @submit.prevent="submit">
   <div class="head"><span class="mark"><LoomiMark :size="30"/></span><span class="word">Loomi</span><span class="grow"/><button type="button" class="btn btn-ghost btn-sm" @click="toggleLang">{{ lang==='fa'?'English':'فارسی' }}</button></div>
   <div class="intro"><h1>{{ t('shLoginTitle') }}</h1><p>{{ t('shLoginHelp') }}</p></div>
   <p v-if="devKey" class="notice"><ShieldCheck/><span>{{ t('devMode') }} <code dir="ltr">{{ devKey }}</code></span></p>
   <div class="field"><label for="login-user">{{ t('username') }}</label><input id="login-user" v-model="username" class="input" dir="ltr" autocomplete="username" required maxlength="64"></div>
   <div class="field"><label for="login-pass">{{ t('password') }}</label><input id="login-pass" v-model="password" class="input" dir="ltr" type="password" autocomplete="current-password" required maxlength="512"></div>
   <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
   <button class="btn btn-primary btn-lg btn-block" :disabled="busy||!initialized||!username.trim()||!password"><LogIn/>{{ busy?t('shLoginBusy'):t('shLoginButton') }}</button>
  </form>
 </main>
</template>
<style scoped>
.login{min-height:100vh;display:grid;place-items:center;padding:32px max(16px,env(safe-area-inset-right,0px)) 32px max(16px,env(safe-area-inset-left,0px));background:radial-gradient(60rem 30rem at 50% -10%,var(--accent-soft),transparent 70%),var(--bg)}
.panel{width:min(400px,100%);display:flex;flex-direction:column;gap:16px;padding:28px;border-radius:var(--r3);background:var(--s1);border:1px solid var(--line);box-shadow:var(--shadow)}
.head{display:flex;align-items:center;gap:10px}
.mark{width:44px;height:44px;border-radius:12px;background:var(--s3);display:grid;place-items:center}
.word{font-size:22px;font-weight:800;letter-spacing:.01em}
.intro{display:flex;flex-direction:column;gap:6px;margin-top:6px}
.intro h1{font-size:26px}
.intro p{color:var(--muted)}
.notice code{word-break:break-all}
</style>
