<script setup>
import { ref } from 'vue';
import { LockKeyhole } from 'lucide-vue-next';
// Standalone by design: props are the same t/api contract every other piece of the shell already uses,
// so whoever wires this in does not need to change how it talks to the server.
const props = defineProps({ t:Function, api:Function });
const emit = defineEmits(['done']);
const currentPassword = ref(''), newPassword = ref(''), busy = ref(false), error = ref(''), done = ref(false);
async function submit() {
 if (busy.value) return;
 busy.value = true; error.value = ''; done.value = false;
 try {
  await props.api('/api/session/password', 'POST', { currentPassword: currentPassword.value, newPassword: newPassword.value });
  currentPassword.value = ''; newPassword.value = ''; done.value = true;
  emit('done');
 } catch (e) { error.value = e.message; }
 finally { busy.value = false; }
}
</script>
<template>
 <form class="change-password" @submit.prevent="submit">
  <p>{{ t('changePasswordHelp') }}</p>
  <label for="change-password-current">{{ t('currentPassword') }}</label>
  <input id="change-password-current" v-model="currentPassword" dir="ltr" type="password" autocomplete="current-password" required maxlength="512">
  <label for="change-password-new">{{ t('newPassword') }}</label>
  <input id="change-password-new" v-model="newPassword" dir="ltr" type="password" autocomplete="new-password" required minlength="8" maxlength="512">
  <p v-if="done" class="dev-note">{{ t('passwordChanged') }}</p>
  <p v-if="error" role="alert" class="error-text">{{ t(error) }}</p>
  <footer class="modal-actions">
   <button class="primary" type="submit" :disabled="busy || !currentPassword || newPassword.length < 8"><LockKeyhole/>{{ busy ? t('processing') : t('changePassword') }}</button>
  </footer>
 </form>
</template>
