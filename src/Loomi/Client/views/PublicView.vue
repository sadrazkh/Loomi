<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import { ArrowRight } from 'lucide-vue-next';
import LoginView from './LoginView.vue';
import LinkLoginView from './LinkLoginView.vue';
import LandingView from './LandingView.vue';
import { t, initialized } from '../core';
// What someone who is not signed in sees. The address decides: the bot's one-time link, the intro page for an empty address, or the sign-in form for the
// rest, so a saved link to a chat still lands on that chat once they are in.
const hash = ref(location.hash);
const sync = () => { hash.value = location.hash; };
onMounted(() => window.addEventListener('hashchange', sync));
onBeforeUnmount(() => window.removeEventListener('hashchange', sync));
const intro = computed(() => ['', '#', '#/', '#/home'].includes(hash.value));
</script>
<template>
 <LinkLoginView v-if="hash.startsWith('#/link/')"/>
 <!-- Not until the session is known: someone already signed in would otherwise see an advert for what they are about to open. -->
 <template v-else-if="intro"><LandingView v-if="initialized"/></template>
 <template v-else>
  <!-- An advert opens inside another app's browser, which often has no back button to return with. -->
  <a v-if="hash==='#/login'" class="about" href="#/"><ArrowRight class="dir"/>{{ t('laAbout') }}</a>
  <LoginView/>
 </template>
</template>
<style scoped>
.about{position:absolute;z-index:1;top:calc(10px + env(safe-area-inset-top,0px));inset-inline-start:16px;display:inline-flex;align-items:center;gap:6px;height:32px;color:var(--muted);font-size:13px;text-decoration:none}
.about:hover{color:var(--text)}
.about svg.lucide{width:16px;height:16px}
:root[dir=ltr] .dir{transform:scaleX(-1)}
</style>
