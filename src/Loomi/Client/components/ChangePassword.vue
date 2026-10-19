<script setup>
import { ref } from 'vue';
import { LockKeyhole, CircleCheck } from 'lucide-vue-next';
import { t, api } from '../core';
// The signed-in person's own password. Its busy and error stay here rather than in the shell's toast, so a wrong current password is said beside the box.
const emit = defineEmits(['done', 'cancel']);
defineProps({ cancellable: Boolean });
const currentPassword = ref(''), newPassword = ref(''), busy = ref(false), error = ref(''), done = ref(false);
async function submit() {
 if (busy.value) return;
 busy.value = true; error.value = ''; done.value = false;
 try {
  await api('/api/session/password', 'POST', { currentPassword: currentPassword.value, newPassword: newPassword.value });
  currentPassword.value = ''; newPassword.value = ''; done.value = true;
  emit('done');
 } catch (e) { error.value = e.message; }
 finally { busy.value = false; }
}
</script>

<template>
 <form class="stack" @submit.prevent="submit">
  <p class="muted-text">{{ t('changePasswordHelp') }}</p>
  <div class="field"><label for="cp-current">{{ t('currentPassword') }}</label><input id="cp-current" v-model="currentPassword" class="input" dir="ltr" type="password" autocomplete="current-password" required maxlength="512"></div>
  <div class="field"><label for="cp-new">{{ t('newPassword') }}</label><input id="cp-new" v-model="newPassword" class="input" dir="ltr" type="password" autocomplete="new-password" required minlength="8" maxlength="512"><span class="field-help">{{ t('acPasswordRule') }}</span></div>
  <p v-if="done" class="notice notice-ok" role="status"><CircleCheck/><span>{{ t('passwordChanged') }}</span></p>
  <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
  <div class="dlg-actions">
   <button v-if="cancellable" type="button" class="btn" @click="emit('cancel')">{{ t(done ? 'close' : 'cancel') }}</button>
   <button class="btn btn-primary" type="submit" :disabled="busy || !currentPassword || newPassword.length < 8"><LockKeyhole/>{{ busy ? t('processing') : t('changePassword') }}</button>
  </div>
 </form>
</template>
