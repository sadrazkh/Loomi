<script setup>
import { ref, onMounted, onBeforeUnmount } from 'vue';
import LoginView from './LoginView.vue';
import LinkLoginView from './LinkLoginView.vue';
// What someone who is not signed in sees. The address decides: the bot's one-time link, or the sign-in form. The intro page takes the rest.
const hash = ref(location.hash);
const sync = () => { hash.value = location.hash; };
onMounted(() => window.addEventListener('hashchange', sync));
onBeforeUnmount(() => window.removeEventListener('hashchange', sync));
</script>
<template>
 <LinkLoginView v-if="hash.startsWith('#/link/')"/>
 <LoginView v-else/>
</template>
